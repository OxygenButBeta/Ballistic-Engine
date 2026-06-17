namespace BallisticEngine.Editor.Inspector.Preview;

// One custom inspector section for a component type (editor-rework Rule 1 / Phase B1). The 10+ hardcoded
// `if (behaviour is Renderer/Volume/Terrain/...) DrawXxxSection(...)` branches that used to live inline in
// InspectorPanel.DrawComponent each become an IComponentPreview that self-registers (by [ComponentPreview])
// for its component type — exactly the way [MenuItem] windows (A1) and ITypeDrawer value drawers register.
// The inspector resolves the applicable previews from ComponentPreviewRegistry by type and draws them; it
// never type-switches. A component with no preview gets only the default member-by-member pipeline.
//
// Why a registry callback rather than relocating each body wholesale: the original sections lean on
// InspectorPanel instance helpers + per-section static preview state (audio voice, animator clock, undo
// bookkeeping, BeginGrid/DrawSubMeshMaterials). Each preview's Draw therefore calls straight back into the
// (internal) InspectorPanel section method via the context — the rendering is BYTE-IDENTICAL to the old
// inline call, only the DISPATCH moved from an instanceof chain to registry resolution. (A later chunk can
// migrate the bodies themselves; B1's contract is "kill the type-switch", not "relocate every helper".)
internal interface IComponentPreview {
    // Draw this preview's section for ctx.Behaviour. Only called when the registry has already matched the
    // behaviour's type (TargetType is assignable from it), so an implementation may cast unconditionally.
    void Draw(in ComponentPreviewContext ctx);
}
