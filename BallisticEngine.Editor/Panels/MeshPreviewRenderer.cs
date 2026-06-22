namespace BallisticEngine.Editor;

internal static class MeshPreviewRenderer {
    public static byte[] Render(in MeshData data, int size) => Dx12EditorPreview.RenderMesh(in data, size);
}
