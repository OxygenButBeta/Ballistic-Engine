namespace BallisticEngine.Editor;

// Editor-local mesh thumbnail rendering. DX12-only (the GL pass was deleted with the GL renderer):
// delegates to Dx12EditorPreview, which renders a mesh artifact's geometry with simple lambert shading
// from a 3/4 view into an offscreen DX12 target and reads it back. (The DX12 preview GPU path is
// currently gated off upstream in ThumbnailCache.Get — thumbnails fall back to icons — until the
// device-hang is root-caused; this entry point stays so re-enabling is a one-line change there.)
internal static class MeshPreviewRenderer {
    public static byte[] Render(in MeshData data, int size) => Dx12EditorPreview.RenderMesh(in data, size);
}
