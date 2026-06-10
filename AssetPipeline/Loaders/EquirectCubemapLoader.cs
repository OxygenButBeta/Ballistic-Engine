namespace BallisticEngine.AssetPipeline.Loaders;

// Builds a cubemap directly from an equirectangular image asset (.hdr/.exr panorama).
// Used when an image asset is requested AS a Texture3D — e.g. dragging an EXR onto a
// Skybox component's Cubemap slot.
public static class EquirectCubemapLoader {
    public const int DefaultFaceSize = 512;

    public static Texture3D Load(AssetImportPipeline pipeline, Guid guid, string assetPath) {
        if (!pipeline.TryGetArtifactPath(guid, out var artifactPath)) {
            Debugging.LogError($"'{assetPath}' has no Library artifact (import failed or pending?).");
            return null;
        }

        TextureData panorama = TextureArtifact.Read(artifactPath);
        TextureData[] faces = EquirectToCubemap.Convert(in panorama, DefaultFaceSize);
        return GraphicAPI.CreateCubemap(faces);
    }
}
