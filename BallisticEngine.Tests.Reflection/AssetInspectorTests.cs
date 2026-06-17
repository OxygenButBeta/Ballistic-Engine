using System.Reflection;
using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// B2 (editor-rework Phase B, Rule 1) asset-inspector registry contract + oracle, tested HEADLESSLY at the
// substrate level. B2 deletes the `switch (ext) { case ".mat": DrawMaterialEditor(...); case ".png" or ...:
// DrawTextureImportSettings(...); ... }` god-switch in InspectorPanel.DrawAssetInspector and replaces it with a
// self-registering, extension-resolved inspector registry — the asset-side mirror of B1's ComponentPreview
// registry (which killed the `if (behaviour is Renderer/Volume/...)` chain). The editor's AssetInspectorRegistry
// + IAssetInspector live in the host assembly and can't be referenced here, so (like ComponentPreviewTests for
// B1) this suite proves the engine-side pieces the registry is built on — TypeCache discovery of
// [AssetInspector], the by-extension match, the attribute's extension normalisation, and DeterministicResolver
// single-winner ordering — and that, fed the SAME inputs, they yield the exact inspector the editor would draw.
// If these pass, the registry's only remaining job (Activator.CreateInstance + delegating into InspectorPanel's
// internal DrawXxx methods) is trivial glue.
//
// The ONLY structural difference from B1 (see plan §3.2/B2): asset selection is path+EXTENSION-keyed (no single
// loaded asset object to switch on), so resolution is by the lower-cased file extension and returns ONE
// inspector (a file has one extension) rather than B1's preview LIST.
//
// Covered: (1) the [AssetInspector] attribute NORMALISES + round-trips Extension/Priority; (2) discovery finds
// the fixtures; (3) an extension resolves to its single inspector, an unregistered extension to NONE (the
// header-only fallback, R1.9); (4) AllowMultiple → one entry per extension (one class covers several exts);
// (5) a same-extension tie resolves to a single deterministic winner (priority desc then type name),
// independent of registration order (P0.4); (6) the rebuild-over-engine-only hot-reload substrate the
// registry's ReloadCaches callback rides on.
internal static class AssetInspectorTests {
    // A mirror of one resolved entry (the editor Entry is unreferenceable). Discovered the SAME way the editor
    // registry discovers it, so an ordering/membership regression in the shared primitives surfaces here.
    readonly record struct InspectorEntry(string Extension, int Priority, string TieKey, Type InspectorType);

    // Re-runs the registry's discovery over the fixture inspectors using the real engine primitives — the exact
    // shape of AssetInspectorRegistry.Discover (minus the Activator.CreateInstance the editor does).
    static List<InspectorEntry> Discover() {
        var entries = new List<InspectorEntry>();
        foreach (Type type in TypeCache.GetTypesWithAttribute<AssetInspectorAttribute>()) {
            // Editor side filters to IAssetInspector; here the stand-in marker is ISampleAssetInspector. Keep
            // only the fixtures so an unrelated [AssetInspector] elsewhere can't perturb the counts.
            if (!typeof(ISampleAssetInspector).IsAssignableFrom(type))
                continue;
            foreach (AssetInspectorAttribute attr in type.GetCustomAttributes<AssetInspectorAttribute>()) {
                if (string.IsNullOrEmpty(attr.Extension)) continue;
                string tieKey = $"{attr.Extension} {type.FullName}";
                entries.Add(new InspectorEntry(attr.Extension, attr.Priority, tieKey, type));
            }
        }
        return entries;
    }

    // The resolution InspectorFor performs: the single best inspector whose Extension matches, by priority desc
    // then ordinal tie-key (the real DeterministicResolver.Resolve single-winner), or null when none.
    static Type InspectorFor(List<InspectorEntry> all, string ext) {
        var resolver = new DeterministicResolver<Type>();
        foreach (InspectorEntry e in all)
            if (string.Equals(e.Extension, ext, StringComparison.OrdinalIgnoreCase))
                resolver.Register(e.InspectorType, priority: e.Priority, tieKey: e.TieKey);
        return resolver.Resolve(_ => true);
    }

    public static int Run() {
        var h = new Harness();

        Assembly engine = typeof(ComponentRegistry).Assembly;
        Assembly tests = typeof(AssetInspectorTests).Assembly;
        TypeCache.Build(engine, tests);

        // ── (1) Attribute normalisation + round-trip ────────────────────────────────────────────────────────
        var matHigh = typeof(SampleMatInspectorHigh).GetCustomAttribute<AssetInspectorAttribute>();
        h.Check("[AssetInspector] Extension round-trips", matHigh is { Extension: ".mat" });
        h.Check("[AssetInspector] Priority round-trips", matHigh is { Priority: 10 });

        var matDefault = typeof(SampleMatInspector).GetCustomAttribute<AssetInspectorAttribute>();
        h.Check("[AssetInspector] default Priority is 0", matDefault is { Priority: 0 });

        // Normalisation: "PNG" (no dot, upper-case) → ".png"; ".jpg" stays.
        var imageAttrs = typeof(SampleImageInspector).GetCustomAttributes<AssetInspectorAttribute>()
            .Select(a => a.Extension).ToHashSet();
        h.Check("[AssetInspector] normalises 'PNG' → '.png'", imageAttrs.Contains(".png"),
            $"got [{string.Join(", ", imageAttrs)}]");
        h.Check("[AssetInspector] keeps '.jpg'", imageAttrs.Contains(".jpg"));

        // ── (2) Discovery finds the fixtures via TypeCache ──────────────────────────────────────────────────
        List<InspectorEntry> all = Discover();
        var inspectorTypes = all.Select(e => e.InspectorType).ToHashSet();
        h.Check("discovery finds the mat inspector", inspectorTypes.Contains(typeof(SampleMatInspector)));
        h.Check("discovery finds the high-priority mat inspector", inspectorTypes.Contains(typeof(SampleMatInspectorHigh)));
        h.Check("discovery finds the multi-extension image inspector", inspectorTypes.Contains(typeof(SampleImageInspector)));

        // ── (3) Resolution: an extension resolves to ONE inspector; an unknown extension to NONE ────────────
        Type forMat = InspectorFor(all, ".mat");
        h.Check("'.mat' resolves to the high-priority inspector", forMat == typeof(SampleMatInspectorHigh),
            $"got {forMat?.Name ?? "null"}");

        Type forUnknown = InspectorFor(all, ".xyz");
        h.Check("unregistered extension resolves to NONE (header-only fallback)", forUnknown is null,
            $"got {forUnknown?.Name ?? "null"}");

        // Case-insensitive match: an upper-case ext still resolves (the panel lower-cases, but the resolver
        // must not be order-fragile on case).
        Type forMatUpper = InspectorFor(all, ".MAT");
        h.Check("resolution is case-insensitive", forMatUpper == typeof(SampleMatInspectorHigh));

        // ── (4) AllowMultiple → one entry per extension (one class covers several exts) ──────────────────────
        int imageEntries = all.Count(e => e.InspectorType == typeof(SampleImageInspector));
        h.Check("AllowMultiple → one entry per [AssetInspector]", imageEntries == 2,
            $"expected 2, got {imageEntries}");
        h.Check("multi-ext inspector resolves for '.png'", InspectorFor(all, ".png") == typeof(SampleImageInspector));
        h.Check("multi-ext inspector resolves for '.jpg'", InspectorFor(all, ".jpg") == typeof(SampleImageInspector));

        // ── (5) Same-extension tie → single deterministic winner, independent of registration order ─────────
        // Drop the high-priority one: the remaining two default-priority inspectors tie, broken by type name —
        // SampleMatInspector (S...Mat) before SampleMatInspectorSecond (S...MatS...), so the former wins.
        var noHigh = all.Where(e => e.InspectorType != typeof(SampleMatInspectorHigh)).ToList();
        h.Check("tie breaks to lowest type name", InspectorFor(noHigh, ".mat") == typeof(SampleMatInspector),
            $"got {InspectorFor(noHigh, ".mat")?.Name ?? "null"}");

        // Independent of registration order: resolving from a reversed entry list yields the SAME winner.
        var reversed = Enumerable.Reverse(all).ToList();
        h.Check("winner is independent of registration order",
            InspectorFor(reversed, ".mat") == typeof(SampleMatInspectorHigh));

        // ── (6) Hot-reload substrate: rebuild over engine-only drops the fixture inspectors ──────────────────
        // The editor registry's ReloadCaches callback re-discovers over the rebuilt TypeCache; here we prove the
        // underlying query goes empty for the (now-unscanned) fixtures, so a re-discovery would too.
        TypeCache.Build(engine);
        bool fixturesGone = TypeCache.GetTypesWithAttribute<AssetInspectorAttribute>()
            .All(t => !typeof(ISampleAssetInspector).IsAssignableFrom(t));
        h.Check("rebuild over engine-only drops fixture [AssetInspector]s", fixturesGone);

        // Restore the full build so later suites (and re-runs) see the fixtures again.
        TypeCache.Build(engine, tests);

        return h.Report("AssetInspector registry (B2)");
    }
}
