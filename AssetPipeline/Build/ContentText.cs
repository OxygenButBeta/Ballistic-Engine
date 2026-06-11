namespace BallisticEngine.AssetPipeline;

// Reads a TEXT/data asset (.scene, .mat, .volume, .shader, .glsl, .cubemap) for the loaders and the
// scene loader, pack-aware: from a mounted content pack (shipped player) if present, else the loose
// file under the project. The logical pack path of a text asset IS its project-relative asset path
// ("Assets/Levels/Main.scene"), which is how the build packs them.
//
// Centralizing the read here means the loaders don't each need to know about packs — they call
// ContentText.Read(project, assetPath) instead of File.ReadAllText(project.ResolveAbsolute(...)).
public static class ContentText {
    // Returns the asset's text, or null if neither a mounted pack nor the filesystem has it.
    public static string Read(BallisticProject project, string assetPath) {
        var logical = assetPath.Replace('\\', '/');
        if (ContentMount.HasAny && ContentMount.TryReadText(logical, out var packed))
            return packed;

        var absolute = project.ResolveAbsolute(assetPath);
        return File.Exists(absolute) ? File.ReadAllText(absolute) : null;
    }

    // Deserializes a JSON text asset via PipelineJson, pack-aware. Returns default(T) when missing.
    public static T ReadJson<T>(BallisticProject project, string assetPath) {
        var text = Read(project, assetPath);
        return text is null ? default : System.Text.Json.JsonSerializer.Deserialize<T>(text, PipelineJson.Options);
    }
}
