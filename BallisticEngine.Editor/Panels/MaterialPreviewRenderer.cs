using BallisticEngine.AssetPipeline.Loaders;

namespace BallisticEngine.Editor;

// Editor-local MATERIAL thumbnail rendering (Unity's material preview sphere). DX12-only (the GL pass was
// deleted with the GL renderer): delegates to Dx12EditorPreview, which draws a UV sphere shaded with the
// material's base colour + metallic/roughness specular and its albedo/normal maps into an offscreen DX12
// target. (The DX12 preview GPU path is currently gated off upstream — ThumbnailCache.Get / InspectorPanel —
// until the device-hang is root-caused; this entry point stays so re-enabling is a one-line change there.)
internal static class MaterialPreviewRenderer {
    public static byte[] Render(MaterialDefinition material, int size) =>
        Dx12EditorPreview.RenderMaterial(material, size);
}
