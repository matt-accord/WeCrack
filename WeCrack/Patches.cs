using System.Text;
using System.Text.RegularExpressions;

namespace WeCrack;

// Regex-based JS patches applied across every .js file in the asar. Filenames are NOT hardcoded —
// each patch declares a pattern and lets the driver find the file(s) that contain it.
//
// This survives webpack content-hash bumps and minifier variable-name reshuffling between builds:
//   * bundle filenames like `app-14a492d5.02f94af5....bundle.js` change every rebuild, but the
//     JS shape inside is stable across WeMod releases we've seen
//   * minifier picks single-letter identifiers like `i`, `a`, `r` non-deterministically — captured
//     via `(?<var>[A-Za-z_$][\w$]*)` and re-emitted verbatim in the replacement
//
// The four patch shapes have been verified in Wand 12.45.1 and 12.46.1. If any shape ever fails to
// match, ApplyAll throws with the shape's description — that's the signal to re-recon the JS.
internal static class Patches
{
    public sealed record JsPatch(string Description, string Pattern, string Replacement, int MinFiles = 1, int MaxFiles = 2);

    public static readonly JsPatch[] All = new[]
    {
        // Shared entitlement module: `const <var>=e=>!!e?.subscription`.
        // Present in both the app bundle and the overlay bundle (same module, two copies).
        new JsPatch(
            Description: "isPro getter → always true (app + overlay)",
            Pattern:     @"const (?<v>[A-Za-z_$][\w$]*)=e=>!!e\?\.subscription",
            Replacement: "const ${v}=e=>!0/*P*/           ",
            MinFiles:    1, MaxFiles: 2),

        // DailyTimeLimit.isEnabled — the 2h daily-limit gate that determines whether the timer runs at all.
        // Required, and independently sufficient for the timer: DailyTimeLimit reads `account.subscription`
        // DIRECTLY (not through the `am` getter above), so #1 alone would NOT lift the 2h cap.
        new JsPatch(
            Description: "DailyTimeLimit.isEnabled → false (kills the 2h timer)",
            Pattern:     @"get isEnabled\(\)\{return this\.isEnabledForUser&&!this\.account\?\.subscription\}",
            Replacement: "get isEnabled(){return !1/*P*/                                            }",
            MinFiles:    1, MaxFiles: 1),

        // NOTE — a former 4th patch wrapped AccountApi.getUserAccount to inject a fake
        // {state:"active",tier:"pro"} subscription. It was REMOVED in v1.1: that object is missing every
        // other field a real subscription carries (nextInvoice, pastDueInvoice, period, endsAt, remoteChannel,
        // …). Account-update subscribers shared with the trainer page recompute on every account fetch and
        // threw on those undefined fields, which froze trainer launch permanently on "Loading...".
        // The `am` getter (#1/#2) already reports Pro to everything that gates on it, so the injection was
        // both unnecessary and actively harmful. Do NOT reintroduce it without a SHAPE-COMPLETE object.
    };

    public sealed record PatchApplication(string ArchivePath, string PatchDescription, int SizeDelta);

    // Latin-1 gives us byte-perfect roundtrip: every byte 0x00-0xFF maps to codepoints U+0000-U+00FF
    // with no invalid-sequence substitution. UTF-8 would risk mangling any stray non-UTF-8 byte
    // (rare in minified JS, but "rare" is not "never").
    private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

    public static List<PatchApplication> ApplyAll(AsarArchive archive)
    {
        var applications = new List<PatchApplication>();
        var patchHitCount = All.ToDictionary(p => p, _ => 0);
        var compiled = All.ToDictionary(p => p, p => new Regex(p.Pattern, RegexOptions.CultureInvariant));

        foreach (var (archivePath, node) in archive.EnumerateFiles())
        {
            if (!archivePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) continue;
            // Skip files stored outside the asar (they aren't editable through the archive object).
            if (node["unpacked"]?.GetValue<bool>() == true) continue;

            var bytes = archive.ReadFile(archivePath);
            var text = Latin1.GetString(bytes);
            var originalLength = text.Length;
            bool modified = false;

            foreach (var patch in All)
            {
                var regex = compiled[patch];
                var matches = regex.Matches(text);
                if (matches.Count == 0) continue;
                if (matches.Count > 1)
                    throw new InvalidOperationException(
                        $"patch '{patch.Description}': {matches.Count} matches in {archivePath} (expected 1) — refusing");

                var before = text.Length;
                text = regex.Replace(text, patch.Replacement, 1);
                var delta = text.Length - before;

                patchHitCount[patch]++;
                applications.Add(new PatchApplication(archivePath, patch.Description, delta));
                modified = true;
            }

            if (modified)
            {
                var newBytes = Latin1.GetBytes(text);
                archive.ReplaceFile(archivePath, newBytes);
            }
        }

        // Validate that every patch fired within its allowed range. A patch that finds zero files
        // means the JS shape has drifted — bail loudly rather than silently install a half-crack.
        var failures = new List<string>();
        foreach (var patch in All)
        {
            var hits = patchHitCount[patch];
            if (hits < patch.MinFiles || hits > patch.MaxFiles)
                failures.Add($"  * '{patch.Description}': hit {hits} file(s), expected {patch.MinFiles}..{patch.MaxFiles}");
        }
        if (failures.Count > 0)
            throw new InvalidOperationException(
                "one or more patches did not fire the expected number of times — the JS has likely been refactored:\n" +
                string.Join("\n", failures));

        return applications;
    }
}
