namespace BallisticEngine.AssetPipeline;

public static class ContentText {
    public static string Read(BallisticProject project, string assetPath) {
        var logical = assetPath.Replace('\\', '/');
        if (ContentMount.HasAny && ContentMount.TryReadText(logical, out var packed))
            return packed;

        var absolute = project.ResolveAbsolute(assetPath);
        return File.Exists(absolute) ? File.ReadAllText(absolute) : null;
    }

    public static byte[] ReadBytes(BallisticProject project, string assetPath) {
        var logical = assetPath.Replace('\\', '/');
        if (ContentMount.HasAny && ContentMount.TryReadBytes(logical, out var packed))
            return packed;

        var absolute = project.ResolveAbsolute(assetPath);
        return File.Exists(absolute) ? File.ReadAllBytes(absolute) : null;
    }

    public static T ReadJson<T>(BallisticProject project, string assetPath) {
        var text = Read(project, assetPath);
        return text is null ? default : System.Text.Json.JsonSerializer.Deserialize<T>(text, PipelineJson.Options);
    }
}
