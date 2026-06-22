using BallisticEngine.AssetPipeline.Loaders;

namespace BallisticEngine.Editor;

internal static class MaterialPreviewRenderer {
    public static byte[] Render(MaterialDefinition material, int size) =>
        Dx12EditorPreview.RenderMaterial(material, size);
}
