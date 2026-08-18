using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeCrack;

// Minimal reader/writer for Electron's ASAR v1 archive format.
//
// The format nests two Chromium Pickles. Each Pickle is [uint32 payloadSize][payload].
//
//   Outer "size" pickle (8 bytes total):
//     [4]  = 4                        // payloadSize of size-pickle
//     [4]  = headerPickleSize         // its payload: a single uint32 telling us how big the header pickle is
//
//   Header pickle (headerPickleSize bytes total, immediately after the size pickle):
//     [4]  = headerPayloadSize        // = 4 + align(jsonByteLength, 4)
//     [4]  = jsonByteLength           // unpadded byte length of the JSON string
//     [jsonByteLength] JSON header (UTF-8)
//     [0..3] zero padding to 4-byte alignment
//
//   Then concatenated file bodies at the "offset" positions declared in the JSON. Offsets are decimal
//   strings measured from the first byte after the padded JSON (i.e. from `8 + headerPickleSize`).
//   Directories are `{ "files": { ... } }` nodes with no offset/size. Unpacked files
//   (`"unpacked": true`) live outside the archive under `<name>.asar.unpacked/` and are not touched here.
internal sealed class AsarArchive
{
    private const int PickleAlign = 4;

    private readonly Dictionary<string, byte[]> _fileOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonObject _header;
    private readonly byte[] _sourceBytes;
    private readonly long _dataStart;

    private AsarArchive(byte[] sourceBytes, JsonObject header, long dataStart)
    {
        _sourceBytes = sourceBytes;
        _header = header;
        _dataStart = dataStart;
    }

    public static AsarArchive Load(string path)
    {
        var raw = File.ReadAllBytes(path);
        if (raw.Length < 16) throw new InvalidDataException("asar: file too small");

        // Size pickle: payloadSize must be 4 (payload is a single uint32).
        var sizePicklePayloadSize = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4));
        if (sizePicklePayloadSize != 4)
            throw new InvalidDataException($"asar: size-pickle payload width was {sizePicklePayloadSize}, expected 4");

        var headerPickleSize = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4));
        if (8L + headerPickleSize > raw.Length)
            throw new InvalidDataException("asar: declared header pickle size exceeds file length");

        // Header pickle body starts at byte 8. Its own payloadSize is at bytes 8..12, then the string pickle inside it.
        var headerPayloadSize = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8, 4));
        if (headerPayloadSize + 4 != headerPickleSize)
            throw new InvalidDataException(
                $"asar: header payload/size mismatch (payload={headerPayloadSize}, pickle={headerPickleSize})");

        var jsonByteLength = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12, 4));
        if (jsonByteLength == 0 || 16 + jsonByteLength > raw.Length)
            throw new InvalidDataException("asar: bad json byte length");

        var json = Encoding.UTF8.GetString(raw, 16, (int)jsonByteLength);
        var header = (JsonNode.Parse(json) ?? throw new InvalidDataException("asar: json parse failed")).AsObject();

        var dataStart = 8L + headerPickleSize;
        return new AsarArchive(raw, header, dataStart);
    }

    public IEnumerable<(string Path, JsonObject Node)> EnumerateFiles()
    {
        var stack = new Stack<(string Prefix, JsonObject Node)>();
        stack.Push(("", _header));
        while (stack.Count > 0)
        {
            var (prefix, obj) = stack.Pop();
            if (obj["files"] is not JsonObject children) continue;
            foreach (var kv in children)
            {
                if (kv.Value is not JsonObject child) continue;
                var childPath = string.IsNullOrEmpty(prefix) ? kv.Key : $"{prefix}/{kv.Key}";
                if (child["files"] is JsonObject) stack.Push((childPath, child));
                else yield return (childPath, child);
            }
        }
    }

    public byte[] ReadFile(string archivePath)
    {
        if (_fileOverrides.TryGetValue(archivePath, out var overridden)) return overridden;
        var node = ResolveFileNode(archivePath) ?? throw new FileNotFoundException($"asar: {archivePath} not present");
        if (node["unpacked"]?.GetValue<bool>() == true)
            throw new InvalidOperationException($"asar: {archivePath} is unpacked; edit the on-disk copy instead");

        var offset = long.Parse(node["offset"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var size = node["size"]!.GetValue<long>();
        var buffer = new byte[size];
        Array.Copy(_sourceBytes, _dataStart + offset, buffer, 0, size);
        return buffer;
    }

    public void ReplaceFile(string archivePath, byte[] contents)
    {
        var node = ResolveFileNode(archivePath) ?? throw new FileNotFoundException($"asar: {archivePath} not present");
        if (node["unpacked"]?.GetValue<bool>() == true)
            throw new InvalidOperationException($"asar: {archivePath} is unpacked; edit the on-disk copy instead");
        _fileOverrides[archivePath] = contents;
    }

    // Serialize the archive with recomputed offsets. Integrity manifests are stripped by default because
    // the crack disables the ASAR-integrity fuse; leaving stale integrity entries in the header would be
    // fine at runtime but is wasted bytes.
    public void Save(string path, bool stripIntegrity = true)
    {
        // Snapshot original (offset,size) for every file BEFORE we mutate the header — the source bytes
        // still need to be located by their pre-existing positions while we're writing.
        var originals = new Dictionary<string, (long Offset, long Size)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (archivePath, node) in EnumerateFiles())
        {
            if (node["unpacked"]?.GetValue<bool>() == true) continue;
            originals[archivePath] = (
                long.Parse(node["offset"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                node["size"]!.GetValue<long>());
        }

        long cursor = 0;
        var writeOrder = new List<(string ArchivePath, long Size, byte[]? Override)>();
        foreach (var (archivePath, node) in EnumerateFiles())
        {
            if (node["unpacked"]?.GetValue<bool>() == true) continue;

            byte[]? overrideBytes = _fileOverrides.TryGetValue(archivePath, out var v) ? v : null;
            long newSize = overrideBytes?.LongLength ?? originals[archivePath].Size;

            node["offset"] = cursor.ToString(CultureInfo.InvariantCulture);
            node["size"] = newSize;
            if (stripIntegrity) node.Remove("integrity");

            writeOrder.Add((archivePath, newSize, overrideBytes));
            cursor += newSize;
        }

        var jsonBytes = Encoding.UTF8.GetBytes(_header.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        var jsonPaddedLen = Align(jsonBytes.Length, PickleAlign);
        // headerPayloadSize covers: [uint32 jsonByteLength][json bytes][padding]
        var headerPayloadSize = (uint)(4 + jsonPaddedLen);
        // headerPickleSize covers: [uint32 headerPayloadSize] + the payload above
        var headerPickleSize = 4 + headerPayloadSize;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
        Span<byte> hdr = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[0..4], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..8], headerPickleSize);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[8..12], headerPayloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[12..16], (uint)jsonBytes.Length);
        fs.Write(hdr);
        fs.Write(jsonBytes);
        var pad = jsonPaddedLen - jsonBytes.Length;
        if (pad > 0) fs.Write(new byte[pad]);

        foreach (var (archivePath, _, overrideBytes) in writeOrder)
        {
            if (overrideBytes is not null)
            {
                fs.Write(overrideBytes);
            }
            else
            {
                var (origOffset, origSize) = originals[archivePath];
                var absolute = _dataStart + origOffset;
                // FileStream.Write(byte[], int, int) requires int, and asar files are far under 2 GB — safe.
                fs.Write(_sourceBytes, checked((int)absolute), checked((int)origSize));
            }
        }
    }

    private JsonObject? ResolveFileNode(string archivePath)
    {
        var parts = archivePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        JsonObject cursor = _header;
        foreach (var part in parts)
        {
            if (cursor["files"] is not JsonObject children) return null;
            if (children[part] is not JsonObject child) return null;
            cursor = child;
        }
        return cursor;
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}
