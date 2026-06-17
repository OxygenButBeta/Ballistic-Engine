namespace BallisticEngine.Editor.Inspector.AssetInspectors;

// The per-extension asset inspectors (editor-rework Rule 1 / Phase B2), one self-registering IAssetInspector
// each. These REPLACE the `switch (ext) { case ".mat": ...; case ".png" or ...: ...; }` body of
// InspectorPanel.DrawAssetInspector. Every class is a thin shim: [AssetInspector(".ext")] registers it for its
// extension(s), and Draw delegates straight back into the (internal) InspectorPanel section method via the
// context — so the rendered output is BYTE-IDENTICAL to the old inline case. Only the DISPATCH moved
// (switch (ext) -> AssetInspectorRegistry resolution). Mirrors B1's ComponentPreviews limb-for-limb.
//
// Discovery is by [AssetInspector] (engine attribute) via TypeCache; resolution is by extension, deterministic
// by priority then type name (DeterministicResolver). The inspectors are stateless — per-section state stays on
// InspectorPanel — so the registry keeps a single shared instance per class.

// Textures: import settings. Covers every image extension the old switch grouped into one case.
[AssetInspector(".png")]
[AssetInspector(".jpg")]
[AssetInspector(".jpeg")]
[AssetInspector(".tga")]
[AssetInspector(".bmp")]
[AssetInspector(".hdr")]
[AssetInspector(".exr")]
internal sealed class TextureAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        InspectorPanel.DrawTextureImportSettings(ctx.Path, ctx.Guid, ctx.Meta);
}

[AssetInspector(".mat")]
internal sealed class MaterialAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        ctx.Panel.DrawMaterialEditor(ctx.Path, ctx.Guid);
}

[AssetInspector(".volume")]
internal sealed class VolumeProfileAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        InspectorPanel.DrawVolumeProfileAsset(ctx.Guid);
}

[AssetInspector(".scene")]
internal sealed class SceneAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        InspectorPanel.DrawSceneAssetActions(ctx.Path);
}

[AssetInspector(".pyscene")]
internal sealed class PysceneAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        InspectorPanel.DrawPysceneHint();
}

// Native text assets — a hint + Show-in-Explorer. Covers the .shader/.glsl/.cubemap group.
[AssetInspector(".shader")]
[AssetInspector(".glsl")]
[AssetInspector(".cubemap")]
internal sealed class TextAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        InspectorPanel.DrawTextAssetHint(ctx.Path);
}

[AssetInspector(".prefab")]
internal sealed class PrefabAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        ctx.Panel.DrawPrefabInspector(ctx.Path);
}

[AssetInspector(".asset")]
internal sealed class DataAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        ctx.Panel.DrawDataAssetInspector(ctx.Path);
}

[AssetInspector(".wav")]
[AssetInspector(".wave")]
[AssetInspector(".ogg")]
internal sealed class AudioClipAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        ctx.Panel.DrawAudioClipAsset(ctx.Path);
}

[AssetInspector(".banim")]
internal sealed class AnimationClipAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        ctx.Panel.DrawAnimationClipAsset(ctx.Path);
}
