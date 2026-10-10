using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace WeCrack;

// Thrown when Discover exhausts every lookup source without finding a workable Wand install.
// The list of attempts is preserved so the UI can render exactly what was tried.
internal sealed class WandInstallNotFoundException : Exception
{
    public IReadOnlyList<string> Attempts { get; }
    public WandInstallNotFoundException(IReadOnlyList<string> attempts)
        : base("WeMod / Wand install not found. Sources tried:\n  " + string.Join("\n  ", attempts))
    {
        Attempts = attempts;
    }
}

// Resolves the current Wand install layout, exposes paths, checks lock/running state,
// and provides read/write access to Electron's asar-integrity metadata in Wand.exe.
//
// There are TWO pieces of asar-integrity metadata inside Wand.exe:
//   1. The Electron FUSE wire (sentinel-anchored). Fuse index 4 is EmbeddedAsarIntegrityValidation
//      (0x31 = ENABLED, 0x30 = DISABLED). Flipping it tells Electron "skip the hash check".
//   2. A STORED-HASH JSON record `[{"file":"resources\app.asar","alg":"SHA256","value":"<64 hex>"}]`
//      sitting in the executable's resource section. When the fuse is ENABLED, Electron computes
//      SHA256 of the loaded asar's HEADER (not the whole file) and compares it to this stored
//      value; a mismatch aborts launch.
//
// WeCrack v1.0..v1.3 took approach (1) — flip the fuse. v1.4 switched to approach (2) — leave the
// fuse ENABLED and OVERWRITE the stored hash to match the patched asar. Reason: WeMod 12.56.0
// added a secondary native check that notices when the fuse byte has been modified from its
// shipped-default state (ENABLED by default on 12.56.0+) and refuses to activate mods for games.
// The hash-update approach leaves the fuse byte untouched, so that secondary check sees nothing
// suspicious and lets the trainer run normally. Fuse methods are preserved on this class for
// state reporting and for UNINSTALL's full restore (which copies Wand.exe.orig back verbatim).
internal sealed class WandInstall
{
    public string InstallRoot { get; }           // e.g. %LocalAppData%\Wand
    public string AppVersionDir { get; }         // e.g. %LocalAppData%\Wand\app-12.56.0
    public string WandExe => Path.Combine(AppVersionDir, "Wand.exe");
    public string AppAsar => Path.Combine(AppVersionDir, "resources", "app.asar");
    public string WandExeBackup => WandExe + ".orig";
    public string AppAsarBackup => AppAsar + ".orig";

    // The privileged helper that performs trainer DLL injection and (since WeMod 12.62.0) runs the
    // client integrity check. It's an UNPACKED asar file living on disk; see ServicePatch.cs.
    public string AuxService => Path.Combine(
        AppVersionDir, "resources", "app.asar.unpacked", "static", "unpacked", "auxiliary",
        "WandAuxiliaryService.exe");
    public string AuxServiceBackup => AuxService + ".orig";

    public long FuseIntegrityOffset { get; }
    public long HashSlotOffset      { get; }   // byte offset in Wand.exe of the 64-char hex hash

    public const byte FuseEnabled  = 0x31; // ASCII '1'
    public const byte FuseDisabled = 0x30; // ASCII '0'

    public const int HashHexLength = 64; // SHA256 as lowercase hex

    private static readonly byte[] FuseSentinel = Encoding.ASCII.GetBytes(
        "dL7pKGdnNz796PbbjQWNKmHXBZaB9tsX");

    // Prefix of the JSON blob that precedes the 64-char hash. Contains ONE literal pair of
    // backslashes (JSON-escaped path separator) between "resources" and "app.asar".
    private static readonly byte[] HashSlotPrefix = Encoding.ASCII.GetBytes(
        @"[{""file"":""resources\\app.asar"",""alg"":""SHA256"",""value"":""");

    private WandInstall(string installRoot, string versionDir, long fuseOffset, long hashSlotOffset)
    {
        InstallRoot = installRoot;
        AppVersionDir = versionDir;
        FuseIntegrityOffset = fuseOffset;
        HashSlotOffset = hashSlotOffset;
    }

    // Tries every known way to locate a Wand install, in order:
    //   1. explicit override path (from user config / manual browse)
    //   2. Squirrel's Uninstall registry keys (per-user, per-machine, per-machine 32-bit view)
    //   3. the default %LocalAppData%\Wand folder
    // Throws WandInstallNotFoundException with the full list of attempts on total failure.
    public static WandInstall Discover(string? overrideRoot = null)
    {
        var attempts = new List<string>();

        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            attempts.Add($"config override: {overrideRoot}");
            var inst = TryLoad(overrideRoot);
            if (inst != null) return inst;
        }

        foreach (var (view, path) in RegistryCandidates())
        {
            attempts.Add($"registry: {view}\\{path}\\InstallLocation");
            var regPath = TryReadRegistryString(view, path, "InstallLocation");
            if (!string.IsNullOrWhiteSpace(regPath))
            {
                var inst = TryLoad(regPath);
                if (inst != null) return inst;
            }
        }

        var defaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wand");
        attempts.Add($"default: {defaultRoot}");
        var d = TryLoad(defaultRoot);
        if (d != null) return d;

        throw new WandInstallNotFoundException(attempts);
    }

    // Given ANY plausible path (install root or a specific app-XX.YY.Z subfolder), returns a
    // usable WandInstall or null. Handles the common user-confusion case of pointing at the
    // version folder instead of the parent. Also refuses to return a non-fuses-build install
    // (sentinel missing) — safer than patching blind.
    private static WandInstall? TryLoad(string path)
    {
        path = path.Trim().TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;

        // If the caller pointed at an app-XX.YY.Z subfolder, walk up to the true install root.
        var basename = Path.GetFileName(path);
        if (basename.StartsWith("app-", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(path);
            if (parent != null && Directory.Exists(parent)) path = parent;
        }

        var versionDir = Directory.GetDirectories(path, "app-*")
            .Select(d => (Dir: d, Version: ParseVersion(Path.GetFileName(d))))
            .Where(x => x.Version is not null)
            .OrderByDescending(x => x.Version)
            .Select(x => x.Dir)
            .FirstOrDefault();
        if (versionDir == null) return null;

        var wandExe = Path.Combine(versionDir, "Wand.exe");
        if (!File.Exists(wandExe)) return null;

        long fuseOffset, hashSlotOffset;
        try
        {
            var raw = File.ReadAllBytes(wandExe);
            fuseOffset = FindFuseOffset(raw);
            hashSlotOffset = FindHashSlotOffset(raw);
        }
        catch { return null; } // sentinel / hash slot missing = not a build we can safely patch
        return new WandInstall(path, versionDir, fuseOffset, hashSlotOffset);
    }

    private static IEnumerable<(string ViewLabel, string SubKey)> RegistryCandidates()
    {
        yield return ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Wand");
        yield return ("HKLM", @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Wand");
        yield return ("HKLM\\WOW6432Node", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Wand");
    }

    private static string? TryReadRegistryString(string viewLabel, string subKey, string valueName)
    {
        try
        {
            var root = viewLabel.StartsWith("HKCU", StringComparison.Ordinal)
                ? Registry.CurrentUser
                : Registry.LocalMachine;
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    public static void EnsureNotRunning()
    {
        var procs = Process.GetProcesses()
            .Where(p =>
            {
                try { return p.ProcessName.StartsWith("Wand", StringComparison.OrdinalIgnoreCase)
                              || p.ProcessName.StartsWith("WeMod", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            })
            .Select(p => $"{p.ProcessName} (pid {p.Id})")
            .ToList();

        if (procs.Count > 0)
            throw new InvalidOperationException(
                "Wand / WeMod is currently running — close it before continuing.\n  " + string.Join("\n  ", procs));
    }

    public byte ReadFuseByte()
    {
        using var fs = new FileStream(WandExe, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(FuseIntegrityOffset, SeekOrigin.Begin);
        var b = fs.ReadByte();
        if (b < 0) throw new EndOfStreamException("Fuse offset beyond EOF");
        return (byte)b;
    }

    // Legacy (v1.3 and earlier) — flips Electron's EmbeddedAsarIntegrityValidation fuse.
    // v1.4+ installs leave the fuse alone; this method is retained only for callers that
    // might still want it (e.g. a future compatibility fallback). The current Install path
    // uses WriteStoredHash instead.
    public string DisableIntegrityFuse()
    {
        using var fs = new FileStream(WandExe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Seek(FuseIntegrityOffset, SeekOrigin.Begin);
        var current = fs.ReadByte();
        if (current == FuseDisabled) return $"fuse already DISABLED (0x{current:X2}) — no change";
        if (current != FuseEnabled)
            throw new InvalidDataException(
                $"unexpected fuse byte 0x{current:X2} at 0x{FuseIntegrityOffset:X} — refusing to patch");

        fs.Seek(FuseIntegrityOffset, SeekOrigin.Begin);
        fs.WriteByte(FuseDisabled);
        return $"flipped 0x{current:X2} → 0x{FuseDisabled:X2} at 0x{FuseIntegrityOffset:X}";
    }

    // Reads the 64-char lowercase-hex SHA256 string that Electron validates the asar header
    // against. Returns null if the slot bytes aren't valid hex (indicates a bug or layout drift).
    public string? ReadStoredHash()
    {
        using var fs = new FileStream(WandExe, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(HashSlotOffset, SeekOrigin.Begin);
        var buf = new byte[HashHexLength];
        var read = fs.Read(buf, 0, HashHexLength);
        if (read != HashHexLength) return null;
        var s = Encoding.ASCII.GetString(buf);
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return null;
        return s;
    }

    // Overwrites the 64-char stored hash in-place. The hash must be exactly 64 lowercase hex
    // chars; the method validates and refuses otherwise (silent corruption would brick launch).
    // FileShare.None keeps concurrent readers out while we write.
    public void WriteStoredHash(string newHashHex)
    {
        if (newHashHex.Length != HashHexLength)
            throw new ArgumentException($"hash must be {HashHexLength} hex chars (got {newHashHex.Length})", nameof(newHashHex));
        foreach (var c in newHashHex)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                throw new ArgumentException("hash must be lowercase hex (a-f, 0-9)", nameof(newHashHex));

        using var fs = new FileStream(WandExe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Seek(HashSlotOffset, SeekOrigin.Begin);
        fs.Write(Encoding.ASCII.GetBytes(newHashHex), 0, HashHexLength);
    }

    // Computes the SHA256 that Electron validates against: hash of the asar JSON HEADER bytes
    // (NOT the whole file). The asar layout is a nested Chromium Pickle; the JSON lives at
    // offset 16 with length stored at offset 12 (little-endian uint32).
    public static string ComputeAsarHeaderHash(string asarPath)
    {
        using var fs = File.OpenRead(asarPath);
        var sizeHeader = new byte[16];
        if (fs.Read(sizeHeader, 0, 16) != 16)
            throw new InvalidDataException("asar too short for header");
        var jsonLen = BitConverter.ToUInt32(sizeHeader, 12);
        var json = new byte[jsonLen];
        var totalRead = 0;
        while (totalRead < jsonLen)
        {
            var n = fs.Read(json, totalRead, (int)(jsonLen - totalRead));
            if (n <= 0) throw new InvalidDataException("asar truncated inside header");
            totalRead += n;
        }
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(json)).ToLowerInvariant();
    }

    public static string Sha256Hex(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Version? ParseVersion(string folderName)
    {
        if (!folderName.StartsWith("app-", StringComparison.Ordinal)) return null;
        return Version.TryParse(folderName[4..], out var v) ? v : null;
    }

    // Post-sentinel layout: [version byte][count byte][N fuse-state bytes].
    // Fuse index 4 is EmbeddedAsarIntegrityValidation.
    private static long FindFuseOffset(byte[] raw)
    {
        var idx = IndexOfBytes(raw, FuseSentinel, 0);
        if (idx < 0)
            throw new InvalidDataException("Electron fuse sentinel not found in Wand.exe.");

        var fusesStart = idx + FuseSentinel.Length + 2;
        return fusesStart + 4;
    }

    // Finds the SHA256 slot. Scans for the exact JSON prefix that always precedes the hash,
    // then returns the offset of the first hex char (64 chars long, lowercase a-f/0-9).
    private static long FindHashSlotOffset(byte[] raw)
    {
        var idx = IndexOfBytes(raw, HashSlotPrefix, 0);
        if (idx < 0)
            throw new InvalidDataException("Electron asar-integrity hash slot not found in Wand.exe.");
        return idx + HashSlotPrefix.Length;
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle, int start)
    {
        var last = haystack.Length - needle.Length;
        for (int i = start; i <= last; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }
}
