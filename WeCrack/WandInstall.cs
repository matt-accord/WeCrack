using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
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
// and applies the one-byte Electron fuse patch. All state lives on disk; nothing static.
internal sealed class WandInstall
{
    public string InstallRoot { get; }           // e.g. %LocalAppData%\Wand
    public string AppVersionDir { get; }         // e.g. %LocalAppData%\Wand\app-12.46.1
    public string WandExe => Path.Combine(AppVersionDir, "Wand.exe");
    public string AppAsar => Path.Combine(AppVersionDir, "resources", "app.asar");
    public string WandExeBackup => WandExe + ".orig";
    public string AppAsarBackup => AppAsar + ".orig";

    public long FuseIntegrityOffset { get; }

    public const byte FuseEnabled  = 0x31; // ASCII '1'
    public const byte FuseDisabled = 0x30; // ASCII '0'

    private static readonly byte[] FuseSentinel = System.Text.Encoding.ASCII.GetBytes(
        "dL7pKGdnNz796PbbjQWNKmHXBZaB9tsX");

    private WandInstall(string installRoot, string versionDir, long fuseOffset)
    {
        InstallRoot = installRoot;
        AppVersionDir = versionDir;
        FuseIntegrityOffset = fuseOffset;
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
    // version folder instead of the parent.
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

        long fuseOffset;
        try { fuseOffset = FindFuseOffset(wandExe); }
        catch { return null; } // sentinel missing = not an Electron-fuses build; refuse rather than patch blind

        return new WandInstall(path, versionDir, fuseOffset);
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
    private static long FindFuseOffset(string wandExe)
    {
        var raw = File.ReadAllBytes(wandExe);
        var idx = IndexOfBytes(raw, FuseSentinel);
        if (idx < 0)
            throw new InvalidDataException("Electron fuse sentinel not found in Wand.exe.");

        var fusesStart = idx + FuseSentinel.Length + 2;
        return fusesStart + 4;
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle)
    {
        var last = haystack.Length - needle.Length;
        for (int i = 0; i <= last; i++)
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
