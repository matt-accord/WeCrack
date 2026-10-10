using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
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

    // Serialize the archive with recomputed offsets AND recomputed per-file integrity records.
    // Electron 12.56.0+ validates block-level SHA256 for every file when the integrity fuse is
    // enabled; the whole asar fails to launch if any integrity record is stale, missing, or
    // inconsistent across files. We recompute integrity cleanly for every file (packed + unpacked)
    // so the caller can leave the fuse ENABLED and simply update Wand.exe's top-level stored
    // header hash — Electron then validates everything legitimately end-to-end.
    //
    // `asarUnpackedDir` is the on-disk directory where unpacked files live (`<asar path>.unpacked`),
    // used to read bytes of unpacked files for their integrity recomputation. When null, defaults
    // to <path>.unpacked next to the output. If an unpacked file isn't on disk, we keep its
    // original integrity record untouched (we didn't modify it, so the original hash is still valid).
    public void Save(string path, bool stripIntegrity = true, string? asarUnpackedDir = null)
    {
        asarUnpackedDir ??= path + ".unpacked";

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
        var writeOrder = new List<(string ArchivePath, long Size, byte[]? Override, JsonObject Node)>();
        foreach (var (archivePath, node) in EnumerateFiles())
        {
            if (node["unpacked"]?.GetValue<bool>() == true)
            {
                // Unpacked file: live on disk, not inside the asar. Recompute integrity from the
                // on-disk file if it exists; otherwise leave the original integrity record alone
                // (nothing in this archive touches unpacked files, so the original is still valid).
                if (stripIntegrity)
                {
                    var diskPath = Path.Combine(asarUnpackedDir, archivePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(diskPath))
                    {
                        var bytes = File.ReadAllBytes(diskPath);
                        node["integrity"] = BuildIntegrityNode(bytes);
                    }
                }
                continue;
            }

            byte[]? overrideBytes = _fileOverrides.TryGetValue(archivePath, out var v) ? v : null;
            long newSize = overrideBytes?.LongLength ?? originals[archivePath].Size;

            node["offset"] = cursor.ToString(CultureInfo.InvariantCulture);
            node["size"] = newSize;

            if (stripIntegrity)
            {
                // Compute integrity from the bytes we're about to write — the override if present,
                // otherwise the source slice. This keeps the integrity record consistent with the
                // actual file bytes in the output archive.
                byte[] contentBytes = overrideBytes ?? SliceSourceFile(archivePath, originals[archivePath]);
                node["integrity"] = BuildIntegrityNode(contentBytes);
            }

            writeOrder.Add((archivePath, newSize, overrideBytes, node));
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

        foreach (var (archivePath, _, overrideBytes, _) in writeOrder)
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

    private byte[] SliceSourceFile(string archivePath, (long Offset, long Size) orig)
    {
        var buffer = new byte[orig.Size];
        Array.Copy(_sourceBytes, _dataStart + orig.Offset, buffer, 0, orig.Size);
        return buffer;
    }

    // Builds Electron's per-file integrity record:
    //   { algorithm: "SHA256", hash: <sha256 of whole file>, blockSize: 4194304, blocks: [<sha256 of each 4MB block>] }
    // For files ≤ 4MB, `hash` and `blocks[0]` are identical (one block covers the whole file).
    // Verified empirically from a pristine WeMod asar — matches the exact shape Electron validates.
    private const int IntegrityBlockSize = 4 * 1024 * 1024;
    private static JsonObject BuildIntegrityNode(byte[] data)
    {
        using var sha = SHA256.Create();
        var topHash = Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();

        var blocks = new JsonArray();
        if (data.Length == 0)
        {
            blocks.Add(topHash); // empty-file hash as a single block, matches pack behavior
        }
        else
        {
            for (int offset = 0; offset < data.Length; offset += IntegrityBlockSize)
            {
                var len = Math.Min(IntegrityBlockSize, data.Length - offset);
                var blockHash = Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>(data, offset, len)))
                    .ToLowerInvariant();
                blocks.Add(blockHash);
            }
        }

        return new JsonObject
        {
            ["algorithm"] = "SHA256",
            ["hash"] = topHash,
            ["blockSize"] = IntegrityBlockSize,
            ["blocks"] = blocks,
        };
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
