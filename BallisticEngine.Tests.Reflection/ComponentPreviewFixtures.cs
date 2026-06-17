namespace BallisticEngine.Tests.Reflection;

// Fixtures for the B1 component-preview RESOLUTION substrate (editor-rework Rule 1). The editor's
// ComponentPreviewRegistry + IComponentPreview are editor-side and can't be referenced from this engine-only
// harness, so these fixtures exercise the part the registry is thin glue over: discovery via
// TypeCache.GetTypesWithAttribute<ComponentPreviewAttribute>(), the assignable-by-TargetType match, and
// DeterministicResolver ordering. If those hold, the registry's only remaining job (Activator.CreateInstance
// + delegating into InspectorPanel section methods) is trivial.
//
// A local stand-in interface mirrors the editor's IComponentPreview so the fixtures look like real previews
// (the harness only needs the [ComponentPreview] attribute + the assignability of the target, not a real
// ImGui draw). The set is laid out to prove: (1) membership, (2) base-type-covers-subclass, (3) priority +
// tie-break ordering when several previews target ONE component type, (4) AllowMultiple → one entry per
// target, (5) a component with NO preview resolves to empty.

// Stand-in for the editor's IComponentPreview (unreferenceable here). Real previews implement the editor one;
// the resolution logic only cares about the [ComponentPreview] attribute + TargetType, so this suffices.
internal interface ISamplePreview { }

// ── Fixture component types (real engine Behaviours, so TargetType assignability is genuine) ──────────────
public class SamplePreviewComponent : Behaviour { }
public sealed class SamplePreviewSubComponent : SamplePreviewComponent { }   // covered by a base-typed preview
public sealed class SamplePreviewOtherComponent : Behaviour { }
public sealed class SamplePreviewBareComponent : Behaviour { }              // intentionally has NO preview

// ── Fixture previews ──────────────────────────────────────────────────────────────────────────────────────
// One preview for SamplePreviewComponent (also covers its subclass via assignability).
[ComponentPreview(typeof(SamplePreviewComponent))]
internal sealed class SampleComponentPreview : ISamplePreview { }

// A SECOND preview for the SAME component type — proves multiple previews compose + order deterministically.
// Default priority 0; ties break on type full name, so SampleComponentPreview (S...C) sorts before
// SampleComponentPreviewSecond (S...S) — locked by the ordering test.
[ComponentPreview(typeof(SamplePreviewComponent))]
internal sealed class SampleComponentPreviewSecond : ISamplePreview { }

// A HIGH-priority preview for the same type — must sort FIRST regardless of its later type name.
[ComponentPreview(typeof(SamplePreviewComponent), priority: 10)]
internal sealed class SampleComponentPreviewHigh : ISamplePreview { }

// AllowMultiple: ONE preview class registered for TWO component types — one entry each.
[ComponentPreview(typeof(SamplePreviewOtherComponent))]
[ComponentPreview(typeof(SamplePreviewSubComponent))]
internal sealed class SampleMultiTargetPreview : ISamplePreview { }
