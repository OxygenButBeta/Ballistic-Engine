namespace BallisticEngine.Tests.Reflection;

// Fixtures for the B2 asset-inspector RESOLUTION substrate (editor-rework Rule 1). The editor's
// AssetInspectorRegistry + IAssetInspector are editor-side and can't be referenced from this engine-only
// harness, so these fixtures exercise the part the registry is thin glue over: discovery via
// TypeCache.GetTypesWithAttribute<AssetInspectorAttribute>(), the by-EXTENSION match, and DeterministicResolver
// single-winner ordering. If those hold, the registry's only remaining job (Activator.CreateInstance +
// delegating into InspectorPanel section methods) is trivial — exactly the B1 ComponentPreviewFixtures shape,
// keyed on a file extension instead of a component Type (the only structural difference, see §3.2/B2).
//
// A local stand-in interface mirrors the editor's IAssetInspector so the fixtures look like real inspectors
// (the harness only needs the [AssetInspector] attribute + its normalised Extension, not a real ImGui draw).
// The set proves: (1) the attribute normalises the extension to ".ext" lower-case; (2) discovery finds them;
// (3) an extension resolves to its ONE inspector, an unregistered extension to NONE (the header-only
// fallback); (4) AllowMultiple → one entry per extension so one class covers several exts; (5) priority +
// tie-break picks a single deterministic winner when two inspectors claim the SAME extension, independent of
// registration order.

// Stand-in for the editor's IAssetInspector (unreferenceable here). Real inspectors implement the editor one;
// the resolution logic only cares about the [AssetInspector] attribute + its Extension, so this suffices.
internal interface ISampleAssetInspector { }

// One inspector for ".mat".
[AssetInspector(".mat")]
internal sealed class SampleMatInspector : ISampleAssetInspector { }

// A SECOND inspector for ".mat" at default priority — proves a same-extension tie resolves to the single
// lowest-type-name winner (S...Mat sorts before S...MatSecond, so SampleMatInspector wins the tie).
[AssetInspector(".mat")]
internal sealed class SampleMatInspectorSecond : ISampleAssetInspector { }

// A HIGH-priority inspector for ".mat" — must WIN the resolution regardless of its later type name.
[AssetInspector(".mat", priority: 10)]
internal sealed class SampleMatInspectorHigh : ISampleAssetInspector { }

// AllowMultiple: ONE inspector class registered for SEVERAL extensions (the .png/.jpg/.tga texture shape) —
// one entry per extension. Also proves extension NORMALISATION: "PNG" (no dot, upper-case) → ".png".
[AssetInspector("PNG")]
[AssetInspector(".jpg")]
internal sealed class SampleImageInspector : ISampleAssetInspector { }
