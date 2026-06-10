namespace BallisticEngine.AssetPipeline.Loaders;

// .cubemap asset, two forms:
//   { "version": 1, "faces": { "right": "<ref>", "left": ..., "top": ..., "bottom": ..., "front": ..., "back": ... } }
//   { "version": 1, "equirect": "<ref to .hdr/.exr/image>", "faceSize": 512 }
// Face order fed to the GPU: +X (right), -X (left), +Y (top), -Y (bottom), +Z (front), -Z (back).
public sealed class CubemapDefinition {
    public int Version { get; set; } = 1;
    public Dictionary<string, string> Faces { get; set; } = new();
    public string Equirect { get; set; }
    public int FaceSize { get; set; } = 512;
}

public static class CubemapLoader {
    static readonly string[] FaceOrder = ["right", "left", "top", "bottom", "front", "back"];

    public static Texture3D Load(BallisticProject project, AssetImportPipeline pipeline, string assetPath) {
        var definition = PipelineJson.Read<CubemapDefinition>(project.ResolveAbsolute(assetPath));

        if (!string.IsNullOrEmpty(definition.Equirect))
            return LoadEquirect(definition, pipeline, assetPath);

        var faces = new TextureData[6];
        for (var i = 0; i < FaceOrder.Length; i++) {
            if (definition.Faces is null || !definition.Faces.TryGetValue(FaceOrder[i], out var reference)) {
                Debugging.LogError($"'{assetPath}': missing '{FaceOrder[i]}' face.");
                return null;
            }

            Guid guid;
            if (!AssetRef.IsGuidRef(reference, out guid) && !AssetDatabase.TryGetGuid(reference, out guid)) {
                Debugging.LogError($"'{assetPath}': face '{FaceOrder[i]}' reference '{reference}' does not resolve.");
                return null;
            }

            if (!pipeline.TryGetArtifactPath(guid, out var artifactPath)) {
                Debugging.LogError($"'{assetPath}': face '{reference}' has no Library artifact.");
                return null;
            }

            faces[i] = TextureArtifact.Read(artifactPath);
        }

        return GraphicAPI.CreateCubemap(faces);
    }

    static Texture3D LoadEquirect(CubemapDefinition definition, AssetImportPipeline pipeline, string assetPath) {
        Guid guid;
        if (!AssetRef.IsGuidRef(definition.Equirect, out guid) &&
            !AssetDatabase.TryGetGuid(definition.Equirect, out guid)) {
            Debugging.LogError($"'{assetPath}': equirect reference '{definition.Equirect}' does not resolve.");
            return null;
        }

        if (!pipeline.TryGetArtifactPath(guid, out var artifactPath)) {
            Debugging.LogError($"'{assetPath}': equirect '{definition.Equirect}' has no Library artifact.");
            return null;
        }

        TextureData panorama = TextureArtifact.Read(artifactPath);
        TextureData[] faces = EquirectToCubemap.Convert(in panorama, definition.FaceSize);
        return GraphicAPI.CreateCubemap(faces);
    }
}
