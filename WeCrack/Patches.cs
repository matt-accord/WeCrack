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
//
// NOTE (v1.3) — a v1.2 experiment added four SPECULATIVE gate patches (inline !!x?.subscription,
// subscription?.state==="active", subscription?.tier==="pro", generic `get isX(){...subscription...}`)
// to try to unlock themes. They were MinFiles=0 so they were safe to *install*, but at least one of
// them matched code that WeMod's launch path depends on and froze the app on "Loading...". LESSON:
// safe-to-install (MinFiles=0, won't abort) is NOT the same as safe-to-run. A patch that MATCHES and
// FIRES can still break WeMod at runtime — this is the same failure mode that killed the v1.1 injection.
// Do NOT add blind/speculative patches again. Any new patch must be written against ACTUAL extracted
// app.asar JS, verified to target exactly the intended gate, and tested against a real launch.
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

        // ThemeSelector.isPro — the theme picker (settings page) carries its OWN class-local isPro
        // getter, `get isPro(){return!!this.account?.subscription}`, and gates selectTheme() on it:
        //     selectTheme(e){("default"===e||this.isPro)&&this.#r.dispatch(...,{theme:e},...)}
        // Non-Pro users can therefore only pick "default"; every other theme fires the pro-CTA.
        // This is a DIFFERENT shape than #1 (`const <v>=e=>!!e?.subscription`), which is why #1 never
        // touched it and themes stayed locked after INSTALL. Added v1.4 via real recon of the extracted
        // 12.54.1 bundle: this EXACT getter body is unique across all 494 .js files (verified), and it
        // lives on a settings-page click handler — NOT the launch/trainer path — so forcing it true is
        // display-only and cannot recreate the "Loading…" freeze the v1.2 speculative patches caused.
        // Length-preserving (47→47). MinFiles/MaxFiles=1: if the shape ever drifts, INSTALL aborts loudly.
        new JsPatch(
            Description: "ThemeSelector.isPro getter → true (unlocks Pro themes: selection gate)",
            Pattern:     @"get isPro\(\)\{return!!this\.account\?\.subscription\}",
            Replacement: "get isPro(){return!0/*P*/                     }",
            MinFiles:    1, MaxFiles: 1),

        // Theme applier reset-gate — NECESSARY PARTNER to the getter above. Patch #3 alone is
        // insufficient: it lets ThemeSelector.selectTheme() DISPATCH a pro theme, but a separate
        // applier class (module 60990 in app-14a492d5) re-checks the REAL account on every theme/
        // subscription change and silently reverts:
        //     #p(){return"default"!==this.theme&&!this.subscription&&(this.#a.dispatch(o.Kc,{theme:"default"},"theme_applier",!0),!0)}
        //     themeChanged(e,t){this.#p()||(t&&body.classList.remove(`theme-${t}`),body.classList.add(`theme-${e}`))}
        //     subscriptionChanged(){this.#p()||this.themeChanged(this.theme)}
        // For a free account (`this.subscription` is null via selector `e=>e.account?.subscription`),
        // #p() is truthy for any non-default theme, so themeChanged NEVER applies it AND #p() dispatches
        // a reset back to "default". Net effect with only #3: clicking a Pro theme does nothing —
        // no upsell (correctly suppressed by #3), no theme change. That was the "can't click" report.
        // Fix: force `!this.subscription` → `!1` so #p() is always false → the applier applies the
        // chosen theme and never resets. The reset dispatch is short-circuited out of the && chain.
        // Verified at runtime (12.54.1, patched asar + fuse, CDP): Pro theme applies on click AND
        // persists across in-app navigation; app launches clean (no "Loading…" freeze). NOTE: the
        // theme setting is backend-synced, so it may reset to default on a full app RESTART — that
        // deeper sync layer is deliberately NOT touched (faking it risks the v1.1 shape-incomplete
        // freeze). Unique across all 494 .js files (verified). Length-preserving (35→35).
        new JsPatch(
            Description: "Theme applier reset-gate → disabled (Pro theme applies + sticks in-session)",
            Pattern:     @"!==this\.theme&&!this\.subscription&&",
            Replacement: "!==this.theme&&!1/*            */&&",
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
