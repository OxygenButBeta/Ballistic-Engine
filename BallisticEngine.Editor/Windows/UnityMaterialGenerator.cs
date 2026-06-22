using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.AssetPipeline.Unity;

namespace BallisticEngine.Editor;

internal sealed class UnityMaterialGenerator(Dictionary<string, string> guidToFile, BallisticProject project) {
    readonly Dictionary<string, string> cache = new(StringComparer.OrdinalIgnoreCase);

    public string Resolve(string matGuid) {
        if (string.IsNullOrEmpty(matGuid)) return null;
        if (cache.TryGetValue(matGuid, out var c)) return c;
        var r = Generate(matGuid);
        cache[matGuid] = r;
        return r;
    }

    string Generate(string matGuid) {
        if (!guidToFile.TryGetValue(matGuid, out var matPath) || !File.Exists(matPath))
            return null;

        UnityMaterialData unity;
        try { unity = UnityMaterialParser.Parse(File.ReadAllText(matPath)); }
        catch { return null; }

        var def = new MaterialDefinition { Shader = ModelImporter.DefaultShaderRef };
        BindTexture(def, "Diffuse", unity.DiffuseGuid);
        BindTexture(def, "Normal", unity.NormalGuid);
        BindTexture(def, "AO", unity.OcclusionGuid);
        if (unity.MaskGuid is not null) {
            BindTexture(def, "Metallic", unity.MaskGuid);
            if (unity.MaskIsPacked) def.PackedOrm = true;
        }

        if (unity.BaseColor is { Length: >= 3 }) def.BaseColor = unity.BaseColor;
        if (unity.Metallic is { } m) def.Metallic = m;
        if (unity.Smoothness is { } s) def.Roughness = Math.Clamp(1f - s, 0f, 1f);
        if (unity.AlphaCutout) def.Cutout = true;

        if (def.Textures.Count == 0 && def.BaseColor is null)
            return null;

        var outPath = Path.ChangeExtension(matPath, null) + ".bal.mat";
        try {
            PipelineJson.Write(outPath, def);
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Unity import: failed to write material for '{Path.GetFileName(matPath)}': {exception.Message}");
            return null;
        }

        var full = Path.GetFullPath(outPath);
        if (!full.StartsWith(Path.GetFullPath(project.RootPath), StringComparison.OrdinalIgnoreCase))
            return null;
        return project.ToAssetPath(full);
    }

    void BindTexture(MaterialDefinition def, string slot, string textureGuid) {
        if (textureGuid is null) return;
        if (!guidToFile.TryGetValue(textureGuid, out var absolute) || !File.Exists(absolute)) return;
        var refPath = UnityImportWindow.GuidToProjectRef(textureGuid, guidToFile, project);
        if (refPath is null) return;

        EnsureTextureType(absolute, slot);
        def.Textures[slot] = refPath;
    }

    static void EnsureTextureType(string textureAbsolute, string slot) {
        var metaPath = MetaFile.PathFor(textureAbsolute);
        try {
            if (!File.Exists(metaPath)) {
                new MetaFile {
                    Guid = Guid.NewGuid(),
                    Importer = "TextureImporter",
                    Settings = new System.Text.Json.Nodes.JsonObject { ["textureType"] = slot },
                }.Save(metaPath);
                return;
            }
            MetaFile meta = MetaFile.Load(metaPath);
            var current = meta.Settings?["textureType"]?.GetValue<string>();
            if (string.Equals(current, slot, StringComparison.OrdinalIgnoreCase))
                return;
            meta.Settings ??= new System.Text.Json.Nodes.JsonObject();
            meta.Settings["textureType"] = slot;
            meta.Save(metaPath);
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Unity import: could not set texture type for '{Path.GetFileName(textureAbsolute)}': {exception.Message}");
        }
    }
}
