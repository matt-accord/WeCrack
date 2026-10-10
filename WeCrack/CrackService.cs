using System.IO;

namespace WeCrack;

// UI-agnostic service layer. Every method returns a structured report — no Console I/O,
// no side channels — so WPF, CLI, or unit tests all render the same data the same way.
//
// v1.4 switch: the install no longer flips Electron's integrity fuse. Instead, after rewriting
// the asar, it recomputes the asar HEADER SHA256 and overwrites Wand.exe's embedded stored hash
// so the fuse-driven check validates the patched asar legitimately. See WandInstall.cs for the
// background on why (WeMod 12.56.0 added a secondary tamper check that spotted fuse flips and
// refused mod activation; leaving the fuse at its default value eliminates that signal).

public enum LogLevel { Info, Ok, Warn, Error }

public sealed record LogLine(LogLevel Level, string Text)
{
    public override string ToString() => $"[{Level.ToString().ToUpperInvariant(),-5}] {Text}";
}

public sealed record FileStat(string Path, bool Exists, long Size, string? Sha256Short);

public enum InstallState
{
    NotFound,     // couldn't locate WeMod / Wand on this machine — user needs to Browse manually
    Clean,        // pristine: stored hash matches live asar header, no *.orig backups
    Ready,        // backups present AND live == backup (install started, no patches applied yet)
    Patched,      // backups present, live != backup, AND stored hash matches live asar header
                  //   (also includes legacy v1.3 installs where the fuse was flipped instead)
    Mixed,        // anything else — genuinely inconsistent, inspect manually
}

public sealed record VerifyReport(
    string InstallRoot,
    string VersionDir,
    long FuseOffset,
    byte FuseValue,
    string FuseState,
    long HashSlotOffset,
    string StoredHash,          // 64-char hex read from Wand.exe (or "ERROR" / "—")
    string AsarHeaderHash,      // 64-char hex computed from live asar header (or "—")
    bool HashMatches,           // StoredHash == AsarHeaderHash
    string AuxServiceState,     // "PATCHED" / "ORIGINAL" / "UNKNOWN" / "MISSING"
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
            log.Add(new(LogLevel.Info, $"fuse sentinel at 0x{install.FuseIntegrityOffset:X}, hash slot at 0x{install.HashSlotOffset:X}"));

            var wand   = Stat(install.WandExe);
            var asar   = Stat(install.AppAsar);
            var wandBk = Stat(install.WandExeBackup);
            var asarBk = Stat(install.AppAsarBackup);
            var fuseByte = install.ReadFuseByte();

            // Hash state: pull Wand.exe's stored hash and compute the live asar header's actual
            // hash. Equality is what Electron checks when the fuse is enabled.
            var storedHash = install.ReadStoredHash() ?? "ERROR";
            string actualHash;
            try { actualHash = WandInstall.ComputeAsarHeaderHash(install.AppAsar); }
            catch (Exception ex)
            {
                actualHash = "—";
                log.Add(new(LogLevel.Warn, $"couldn't compute asar header hash: {ex.Message}"));
            }
            var hashMatches = storedHash.Length == WandInstall.HashHexLength
                           && storedHash == actualHash;

            var auxState = AuxServiceStateText(install.AuxService);

            var (state, stateName) = ClassifyState(
                fuseByte, hashMatches, wand, asar, wandBk, asarBk);

            log.Add(new(LogLevel.Info, $"fuse byte = 0x{fuseByte:X2} ({FuseText(fuseByte)})"));
            log.Add(new(LogLevel.Info, $"stored hash: {Short(storedHash)}  |  live asar header: {Short(actualHash)}  |  match: {hashMatches}"));
            log.Add(new(LogLevel.Info, $"aux service (injection integrity gate): {auxState}"));
            var lvl = state == InstallState.Mixed ? LogLevel.Warn : LogLevel.Ok;
            log.Add(new(lvl, $"state: {stateName}"));

            return new VerifyReport(
                install.InstallRoot, install.AppVersionDir,
                install.FuseIntegrityOffset, fuseByte, FuseText(fuseByte),
                install.HashSlotOffset, storedHash, actualHash, hashMatches,
                auxState, state, wand, asar, wandBk, asarBk, log);
        }
        catch (WandInstallNotFoundException nf)
        {
            log.Add(new(LogLevel.Warn, "WeMod / Wand install not found automatically."));
            foreach (var a in nf.Attempts) log.Add(new(LogLevel.Info, $"tried  {a}"));
            log.Add(new(LogLevel.Info, "click [ Browse for WeMod folder... ] in the STATUS card to point WeCrack at your install."));
            return NotFoundReport(log);
        }
        catch (Exception ex)
        {
            log.Add(new(LogLevel.Error, ex.Message));
            return ErrorReport(log);
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

            // Backups first — never overwrite an existing backup. The .orig files are the
            // source-of-truth we read from (so re-INSTALL always starts pristine) and the
            // bytes UNINSTALL restores verbatim.
            Backup(install.WandExe, install.WandExeBackup, log);
            Backup(install.AppAsar, install.AppAsarBackup, log);

            // Patch the auxiliary service BEFORE saving the asar, so the asar's per-file integrity
            // record for this (unpacked) file is recomputed from the patched bytes. Always patch a
            // fresh copy of the pristine service from backup, so re-INSTALL is idempotent.
            if (File.Exists(install.AuxService))
            {
                Backup(install.AuxService, install.AuxServiceBackup, log);
                File.Copy(install.AuxServiceBackup, install.AuxService, overwrite: true);
                var svcResult = ServicePatch.NeutralizeClientIntegrityGate(install.AuxService);
                ServicePatch.FinalizeSwap();
                log.Add(new(LogLevel.Ok, $"WandAuxiliaryService.exe: {svcResult}"));
            }
            else
            {
                log.Add(new(LogLevel.Warn, $"auxiliary service not found at {install.AuxService} — trainer injection may fail on 12.62.0+ (client integrity check)"));
            }

            // Migration: if a previous WeCrack (v1.3 or earlier) flipped the integrity fuse,
            // restore Wand.exe from the backup so we start from a pristine state before writing
            // the new hash. The .orig backup is pristine (we never overwrite backups), so this
            // is safe. Users upgrading from v1.3 don't need to UNINSTALL first.
            var fuseNow = install.ReadFuseByte();
            if (fuseNow != WandInstall.FuseEnabled && File.Exists(install.WandExeBackup))
            {
                File.Copy(install.WandExeBackup, install.WandExe, overwrite: true);
                log.Add(new(LogLevel.Info, $"restored Wand.exe from backup to clear legacy fuse state (was 0x{fuseNow:X2})"));
            }

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

            // Write to sidecar then move — survives a mid-write crash. Point the integrity
            // recomputation at the REAL on-disk unpacked dir (app.asar.unpacked), not the
            // sidecar's non-existent ".new.unpacked", so unpacked-file hashes are recomputed
            // from the actual files (including any we patch, e.g. WandAuxiliaryService.exe).
            var tmp = install.AppAsar + ".new";
            archive.Save(tmp, stripIntegrity: true, asarUnpackedDir: install.AppAsar + ".unpacked");
            File.Move(tmp, install.AppAsar, overwrite: true);
            log.Add(new(LogLevel.Ok, $"asar written  size={new FileInfo(install.AppAsar).Length:N0}  sha256={WandInstall.Sha256Hex(install.AppAsar)[..16]}..."));

            // Compute the patched asar's header hash and overwrite Wand.exe's embedded stored
            // hash. This is the v1.4 approach: the fuse stays ENABLED, Electron validates the
            // patched asar legitimately, and no byte of the fuse wire gets flipped (which is
            // what WeMod 12.56.0's secondary tamper check watches for).
            var newHeaderHash = WandInstall.ComputeAsarHeaderHash(install.AppAsar);
            install.WriteStoredHash(newHeaderHash);
            log.Add(new(LogLevel.Ok, $"stored hash updated → {newHeaderHash[..16]}... at 0x{install.HashSlotOffset:X}"));
            log.Add(new(LogLevel.Info, $"fuse left at 0x{install.ReadFuseByte():X2} (ENABLED) — integrity check now validates patched asar"));
            log.Add(new(LogLevel.Ok, $"Wand.exe sha256={WandInstall.Sha256Hex(install.WandExe)[..16]}..."));

            log.Add(new(LogLevel.Ok, "DONE — launch Wand, timer gone, Pro visible, ads removed, mods will activate."));
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

            if (File.Exists(install.AuxServiceBackup))
            {
                File.Copy(install.AuxServiceBackup, install.AuxService, overwrite: true);
                log.Add(new(LogLevel.Ok, $"restored WandAuxiliaryService.exe from {install.AuxServiceBackup}"));
                anyRestored = true;
            }

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

    // Two files count as "matching" when both hashes are non-null and equal.
    // (A null hash means we skipped hashing — treat as unknown / no-match.)
    private static bool Same(FileStat a, FileStat b)
        => a.Sha256Short != null && a.Sha256Short == b.Sha256Short;

    private static (InstallState State, string Name) ClassifyState(
        byte fuseByte,
        bool hashMatches,
        FileStat wand, FileStat asar, FileStat wandBackup, FileStat asarBackup)
    {
        var fuseOff = fuseByte == WandInstall.FuseDisabled;
        var fuseOn  = fuseByte == WandInstall.FuseEnabled;
        var haveBackups = wandBackup.Exists && asarBackup.Exists;
        var noBackups   = !wandBackup.Exists && !asarBackup.Exists;
        var liveMatchesBackup = haveBackups && Same(wand, wandBackup) && Same(asar, asarBackup);
        var liveAsarDiffersBackup = haveBackups && !Same(asar, asarBackup);

        // Pristine state: no backups ever created AND Electron would validate the asar cleanly.
        if (fuseOn && noBackups && hashMatches)
            return (InstallState.Clean, "CLEAN — pristine, never patched");

        // Install started (backups taken) but no patches applied yet.
        if (haveBackups && liveMatchesBackup)
            return (InstallState.Ready, "READY — backups present, patch not applied yet");

        // v1.4 patched state: asar modified, Wand.exe's stored hash matches the modified asar,
        // fuse untouched (ENABLED). This is the clean modern state.
        if (fuseOn && haveBackups && liveAsarDiffersBackup && hashMatches)
            return (InstallState.Patched, "PATCHED — Pro unlocked (hash-matched)");

        // Legacy v1.3 patched state: asar modified AND fuse flipped to DISABLED. Still functional
        // for Pro/themes/ads but may fail mod activation on 12.56.0+. UNINSTALL + re-INSTALL
        // from v1.4 moves the user to the modern state.
        if (fuseOff && haveBackups && liveAsarDiffersBackup)
            return (InstallState.Patched, "PATCHED (legacy v1.3) — reinstall recommended to fix mod loading");

        return (InstallState.Mixed, "MIXED — inspect files above");
    }

    private static InstallState ReclassifyAfter(WandInstall install)
    {
        var wand   = Stat(install.WandExe);
        var asar   = Stat(install.AppAsar);
        var wandBk = Stat(install.WandExeBackup);
        var asarBk = Stat(install.AppAsarBackup);
        var fuse   = install.ReadFuseByte();
        var stored = install.ReadStoredHash() ?? "";
        string actual;
        try { actual = WandInstall.ComputeAsarHeaderHash(install.AppAsar); } catch { actual = ""; }
        var hashMatches = stored.Length == WandInstall.HashHexLength && stored == actual;
        var (state, _) = ClassifyState(fuse, hashMatches, wand, asar, wandBk, asarBk);
        return state;
    }

    private static string FuseText(byte b) => b switch
    {
        WandInstall.FuseEnabled  => "ENABLED",
        WandInstall.FuseDisabled => "DISABLED",
        _                        => $"UNKNOWN(0x{b:X2})",
    };

    private static string Short(string hash)
        => hash.Length >= 16 ? hash[..16] + "..." : hash;

    private static string AuxServiceStateText(string auxServicePath) => ServicePatch.IsNeutralized(auxServicePath) switch
    {
        true  => "PATCHED",
        false => "ORIGINAL",
        null  => File.Exists(auxServicePath) ? "UNKNOWN" : "MISSING",
    };

    private static VerifyReport NotFoundReport(IReadOnlyList<LogLine> log)
        => new("", "", 0, 0, "—", 0, "—", "—", false, "—",
               InstallState.NotFound,
               new FileStat("", false, 0, null), new FileStat("", false, 0, null),
               new FileStat("", false, 0, null), new FileStat("", false, 0, null), log);

    private static VerifyReport ErrorReport(IReadOnlyList<LogLine> log)
        => new("", "", 0, 0, "ERROR", 0, "—", "—", false, "—",
               InstallState.Mixed,
               new FileStat("", false, 0, null), new FileStat("", false, 0, null),
               new FileStat("", false, 0, null), new FileStat("", false, 0, null), log);
}
