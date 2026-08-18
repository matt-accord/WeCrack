using System.IO;

namespace WeCrack;

// UI-agnostic service layer. Every method returns a structured report — no Console I/O,
// no side channels — so WPF, CLI, or unit tests all render the same data the same way.

public enum LogLevel { Info, Ok, Warn, Error }

public sealed record LogLine(LogLevel Level, string Text)
{
    public override string ToString() => $"[{Level.ToString().ToUpperInvariant(),-5}] {Text}";
}

public sealed record FileStat(string Path, bool Exists, long Size, string? Sha256Short);

public enum InstallState
{
    NotFound,     // couldn't locate WeMod / Wand on this machine — user needs to Browse manually
    Clean,        // fuse ENABLED and no *.orig backups — pristine, never touched
    Ready,        // fuse ENABLED, *.orig backups present AND match live (last install stopped after backup step)
    Patched,      // fuse DISABLED and both *.orig backups present (matching or not) — crack installed
    Mixed,        // anything else — genuinely inconsistent, inspect manually
}

public sealed record VerifyReport(
    string InstallRoot,
    string VersionDir,
    long FuseOffset,
    byte FuseValue,
    string FuseState,
    InstallState State,
    FileStat WandExe,
    FileStat AppAsar,
    FileStat WandExeBackup,
    FileStat AppAsarBackup,
    IReadOnlyList<LogLine> Log);

public sealed record ActionReport(bool Success, InstallState FinalState, IReadOnlyList<LogLine> Log);

public static class CrackService
{
    public static VerifyReport Verify()
    {
        var log = new List<LogLine>();
        try
        {
            var cfg = Config.Load();
            var install = WandInstall.Discover(cfg.OverrideInstallRoot);
            log.Add(new(LogLevel.Ok,   $"discovered: {install.AppVersionDir}"));
            log.Add(new(LogLevel.Info, $"fuse sentinel at 0x{install.FuseIntegrityOffset:X}"));

            var wand   = Stat(install.WandExe);
            var asar   = Stat(install.AppAsar);
            var wandBk = Stat(install.WandExeBackup);
            var asarBk = Stat(install.AppAsarBackup);
            var fuseByte = install.ReadFuseByte();
            var (state, stateName) = ClassifyState(fuseByte, wand, asar, wandBk, asarBk);

            log.Add(new(LogLevel.Info, $"fuse byte = 0x{fuseByte:X2} ({FuseText(fuseByte)})"));
            var lvl = state == InstallState.Mixed ? LogLevel.Warn : LogLevel.Ok;
            log.Add(new(lvl, $"state: {stateName}"));

            return new VerifyReport(
                install.InstallRoot, install.AppVersionDir,
                install.FuseIntegrityOffset, fuseByte, FuseText(fuseByte),
                state, wand, asar, wandBk, asarBk, log);
        }
        catch (WandInstallNotFoundException nf)
        {
            log.Add(new(LogLevel.Warn, "WeMod / Wand install not found automatically."));
            foreach (var a in nf.Attempts) log.Add(new(LogLevel.Info, $"tried  {a}"));
            log.Add(new(LogLevel.Info, "click [ Browse for WeMod folder... ] in the STATUS card to point WeCrack at your install."));
            return new VerifyReport("", "", 0, 0, "—",
                InstallState.NotFound,
                new FileStat("", false, 0, null), new FileStat("", false, 0, null),
                new FileStat("", false, 0, null), new FileStat("", false, 0, null), log);
        }
        catch (Exception ex)
        {
            log.Add(new(LogLevel.Error, ex.Message));
            return new VerifyReport("", "", 0, 0, "ERROR",
                InstallState.Mixed,
                new FileStat("", false, 0, null), new FileStat("", false, 0, null),
                new FileStat("", false, 0, null), new FileStat("", false, 0, null), log);
        }
    }

    public static ActionReport Install()
    {
        var log = new List<LogLine>();
        try
        {
            var cfg = Config.Load();
            var install = WandInstall.Discover(cfg.OverrideInstallRoot);
            WandInstall.EnsureNotRunning();
            log.Add(new(LogLevel.Info, $"target: {install.AppVersionDir}"));

            // Backups first — never overwrite an existing backup.
            Backup(install.WandExe, install.WandExeBackup, log);
            Backup(install.AppAsar, install.AppAsarBackup, log);

            log.Add(new(LogLevel.Info, "loading asar from backup..."));
            var archive = AsarArchive.Load(install.AppAsarBackup);

            log.Add(new(LogLevel.Info, "applying patches:"));
            var apps = Patches.ApplyAll(archive);
            foreach (var a in apps)
            {
                var s = a.SizeDelta >= 0 ? $"+{a.SizeDelta}" : a.SizeDelta.ToString();
                log.Add(new(LogLevel.Ok, $"{a.ArchivePath}  {s} chars  ({a.PatchDescription})"));
            }
            log.Add(new(LogLevel.Info, $"{apps.Count} patch application(s) total."));

            // Write to sidecar then move — survives a mid-write crash.
            var tmp = install.AppAsar + ".new";
            archive.Save(tmp, stripIntegrity: true);
            File.Move(tmp, install.AppAsar, overwrite: true);
            log.Add(new(LogLevel.Ok, $"asar written  size={new FileInfo(install.AppAsar).Length:N0}  sha256={WandInstall.Sha256Hex(install.AppAsar)[..16]}..."));

            log.Add(new(LogLevel.Info, "patching fuse..."));
            log.Add(new(LogLevel.Ok, install.DisableIntegrityFuse()));
            log.Add(new(LogLevel.Ok, $"Wand.exe sha256={WandInstall.Sha256Hex(install.WandExe)[..16]}..."));

            log.Add(new(LogLevel.Ok, "DONE — launch Wand, timer gone, Pro visible."));
            return new ActionReport(true, ReclassifyAfter(install), log);
        }
        catch (Exception ex)
        {
            log.Add(new(LogLevel.Error, ex.Message));
            return new ActionReport(false, InstallState.Mixed, log);
        }
    }

    public static ActionReport Uninstall()
    {
        var log = new List<LogLine>();
        try
        {
            var cfg = Config.Load();
            var install = WandInstall.Discover(cfg.OverrideInstallRoot);
            WandInstall.EnsureNotRunning();

            bool anyRestored = false;
            if (File.Exists(install.WandExeBackup))
            {
                File.Copy(install.WandExeBackup, install.WandExe, overwrite: true);
                log.Add(new(LogLevel.Ok, $"restored Wand.exe from {install.WandExeBackup}"));
                anyRestored = true;
            }
            else log.Add(new(LogLevel.Warn, $"no backup: {install.WandExeBackup}"));

            if (File.Exists(install.AppAsarBackup))
            {
                File.Copy(install.AppAsarBackup, install.AppAsar, overwrite: true);
                log.Add(new(LogLevel.Ok, $"restored app.asar from {install.AppAsarBackup}"));
                anyRestored = true;
            }
            else log.Add(new(LogLevel.Warn, $"no backup: {install.AppAsarBackup}"));

            if (!anyRestored)
            {
                log.Add(new(LogLevel.Warn, "nothing restored — no *.orig backups were present."));
                return new ActionReport(false, InstallState.Mixed, log);
            }

            log.Add(new(LogLevel.Ok, "DONE — install restored to original state."));
            return new ActionReport(true, ReclassifyAfter(install), log);
        }
        catch (Exception ex)
        {
            log.Add(new(LogLevel.Error, ex.Message));
            return new ActionReport(false, InstallState.Mixed, log);
        }
    }

    private static void Backup(string source, string backup, List<LogLine> log)
    {
        if (File.Exists(backup))
        {
            log.Add(new(LogLevel.Info, $"backup already present: {backup}"));
            return;
        }
        File.Copy(source, backup);
        log.Add(new(LogLevel.Ok, $"backup: {source} → {backup}"));
    }

    private static FileStat Stat(string path)
    {
        if (!File.Exists(path)) return new FileStat(path, false, 0, null);
        var info = new FileInfo(path);
        // Skip hashing files larger than 400MB to keep the UI snappy — none of ours qualify but be safe.
        var sha = info.Length < 400_000_000 ? WandInstall.Sha256Hex(path)[..16] + "..." : null;
        return new FileStat(path, true, info.Length, sha);
    }

    private static (InstallState State, string Name) ClassifyState(
        byte fuseByte, FileStat wand, FileStat asar, FileStat wandBackup, FileStat asarBackup)
    {
        var fuseOff = fuseByte == WandInstall.FuseDisabled;
        var fuseOn  = fuseByte == WandInstall.FuseEnabled;
        var haveBackups = wandBackup.Exists && asarBackup.Exists;
        var noBackups   = !wandBackup.Exists && !asarBackup.Exists;

        // Two files count as "matching" when both hashes are non-null and equal.
        // (A null hash means we skipped hashing — treat as unknown / no-match.)
        bool Same(FileStat a, FileStat b) => a.Sha256Short != null && a.Sha256Short == b.Sha256Short;
        var liveMatchesBackup = haveBackups && Same(wand, wandBackup) && Same(asar, asarBackup);

        if (fuseOn  && noBackups)                            return (InstallState.Clean,   "CLEAN — pristine, never patched");
        if (fuseOn  && haveBackups && liveMatchesBackup)     return (InstallState.Ready,   "READY — backups present, patch not applied yet");
        if (fuseOff && haveBackups)                          return (InstallState.Patched, "PATCHED — Pro unlocked");
        return (InstallState.Mixed, "MIXED — inspect files above");
    }

    private static InstallState ReclassifyAfter(WandInstall install)
    {
        var wand   = Stat(install.WandExe);
        var asar   = Stat(install.AppAsar);
        var wandBk = Stat(install.WandExeBackup);
        var asarBk = Stat(install.AppAsarBackup);
        var (state, _) = ClassifyState(install.ReadFuseByte(), wand, asar, wandBk, asarBk);
        return state;
    }

    private static string FuseText(byte b) => b switch
    {
        WandInstall.FuseEnabled  => "ENABLED",
        WandInstall.FuseDisabled => "DISABLED",
        _                        => $"UNKNOWN(0x{b:X2})",
    };
}
