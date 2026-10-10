using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace WeCrack;

// Patches WandAuxiliaryService.exe — the privileged .NET helper (net4.7.1) that WeMod uses to
// inject the trainer runtime (tophat_service_x64.dll) into the game process.
//
// Background: WeMod 12.62.0 rewrote this service to run a "Client integrity check" before
// honoring injection commands. When Wand.exe connects over the named pipe, the service resolves
// the connecting process, confirms it is Wand.exe, and verifies Wand.exe's AUTHENTICODE SIGNATURE
// is valid and signed by WeMod. Because WeCrack has to modify Wand.exe (updating the embedded
// asar-integrity hash so the patched app.asar loads), that signature is no longer valid, so the
// gate rejects every "InjectDll"/"CreateRemoteThread" command with "Client integrity check failed"
// and no trainer ever loads. The UI surfaces this as "problem loading the mods".
//
// The gate is a single method: a `public static bool <M>(PipeStream)` on the one type that holds
// the string constants "Wand.exe", "WandAuxiliaryService.exe", and "version.dll". We locate that
// method by those stable markers (robust to the per-build obfuscated names) and replace its body
// with `return true`, so the integrity check always passes. Nothing verifies the service's own
// signature before it runs (its launcher invokes it by path; its Main does no self-check), so a
// re-written, now-unsigned service still executes normally.
//
// The service is an UNPACKED asar file, so after patching it on disk, AsarArchive.Save recomputes
// its per-file integrity record from the patched bytes — keeping Electron's asar validation happy.
internal static class ServicePatch
{
    // Applies the bypass to the assembly at `path`, writing the patched assembly back to the same
    // path. Returns a short human-readable description of what was patched. Throws on failure
    // (caller treats a throw as a hard install error — better to abort than ship a half-patch).
    public static string NeutralizeClientIntegrityGate(string path)
    {
        // dnlib keeps the file mapped while the module is open; load fully into memory and close
        // the handle before we overwrite the file.
        ModuleDefMD mod;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            mod = ModuleDefMD.Load(fs);

        using (mod)
        {
            var gateType = FindGateType(mod)
                ?? throw new InvalidDataException(
                    "WandAuxiliaryService: couldn't find the integrity-gate type (markers Wand.exe / WandAuxiliaryService.exe / version.dll). Service layout may have changed.");

            var gate = FindGateMethod(gateType)
                ?? throw new InvalidDataException(
                    $"WandAuxiliaryService: gate type '{gateType.Name}' found but no 'static bool (PipeStream)' method in it. Service layout may have changed.");

            if (IsAlreadyNeutralized(gate))
                return $"already neutralized ({gateType.Name}.{gate.Name})";

            var body = new CilBody();
            body.Instructions.Add(OpCodes.Ldc_I4_1.ToInstruction()); // push true
            body.Instructions.Add(OpCodes.Ret.ToInstruction());
            gate.Body = body;

            var tmp = path + ".patched";
            mod.Write(tmp);
            // mod still holds the original file open until disposed; defer the swap to after the using.
            File.Move(tmp, path + ".__swap", overwrite: true);
            PendingSwap = (path + ".__swap", path);
            return $"neutralized {gateType.Name}.{gate.Name}(PipeStream) → return true";
        }
    }

    // dnlib releases the source file handle on Dispose; we write to a sidecar and finalize the swap
    // here so the original is never held open during the rename.
    private static (string From, string To)? PendingSwap;

    public static void FinalizeSwap()
    {
        if (PendingSwap is { } s)
        {
            File.Move(s.From, s.To, overwrite: true);
            PendingSwap = null;
        }
    }

    // Read-only check: is the gate already neutralized in the assembly at `path`?
    // Returns null if the file is missing or the gate can't be located (unknown layout).
    public static bool? IsNeutralized(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var mod = ModuleDefMD.Load(fs);
            var gateType = FindGateType(mod);
            if (gateType == null) return null;
            var gate = FindGateMethod(gateType);
            if (gate == null) return null;
            return IsAlreadyNeutralized(gate);
        }
        catch { return null; }
    }

    private static TypeDef? FindGateType(ModuleDefMD mod)
    {
        foreach (var t in mod.GetTypes())
        {
            bool wand = false, svc = false, ver = false;
            foreach (var f in t.Fields)
            {
                if (f.Constant?.Value is string s)
                {
                    if (s == "Wand.exe") wand = true;
                    else if (s == "WandAuxiliaryService.exe") svc = true;
                    else if (s == "version.dll") ver = true;
                }
            }
            if (wand && svc && ver) return t;
        }
        return null;
    }

    private static MethodDef? FindGateMethod(TypeDef gateType)
    {
        foreach (var m in gateType.Methods)
        {
            if (!m.IsStatic || m.ReturnType.FullName != "System.Boolean") continue;
            if (m.Parameters.Count != 1) continue;
            if (m.Parameters[0].Type.FullName.Contains("PipeStream")) return m;
        }
        return null;
    }

    private static bool IsAlreadyNeutralized(MethodDef gate)
    {
        var instrs = gate.Body?.Instructions;
        return instrs is { Count: 2 }
            && instrs[0].OpCode == OpCodes.Ldc_I4_1
            && instrs[1].OpCode == OpCodes.Ret;
    }
}
