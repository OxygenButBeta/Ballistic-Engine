namespace BallisticEngine.Editor.Inspector.AssetInspectors;

// One custom inspector body for an asset FILE EXTENSION (editor-rework Rule 1 / Phase B2). The
// `switch (ext) { case ".mat": DrawMaterialEditor(...); case ".png" or ...: DrawTextureImportSettings(...);
// case ".volume": DrawVolumeProfileAsset(...); ... }` god-switch that used to live inline in
// InspectorPanel.DrawAssetInspector each becomes an IAssetInspector that self-registers (by [AssetInspector])
// for its extension — the asset-side mirror of B1's IComponentPreview (which killed the `if (behaviour is
// Renderer/Volume/...)` chain). The panel resolves the applicable inspector from AssetInspectorRegistry by
// extension and draws it; it never switches on ext. An asset extension with no registered inspector gets
// only the file header (the byte-identical "// Everything else: just the file header — no clutter"
// fallback the switch already had — R1.9's never-blank safety net for assets, the analog of a bare
// component drawing member-only).
//
// Why a registry callback rather than relocating each body wholesale (the SAME reasoning as B1): the
// original cases lean on InspectorPanel instance helpers + per-section state (audio preview voice, the cached
// DataAsset instance, undo bookkeeping, AssetDatabase access). Each inspector's Draw therefore calls straight
// back into the (internal) InspectorPanel section method via the context — the rendering is BYTE-IDENTICAL to
// the old inline call, only the DISPATCH moved (switch (ext) -> registry resolution). (A later chunk can
// migrate the bodies themselves; B2's contract is "kill the type/extension-switch", not "relocate every
// helper".)
internal interface IAssetInspector {
    // Draw this inspector's body for the selected asset (ctx.Path / ctx.Guid / ctx.Extension). Only called
    // when the registry has already matched the asset's extension, so an implementation may assume its ext.
    void Draw(in AssetInspectorContext ctx);
}
