using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.AssetPipeline.Unity;

namespace BallisticEngine.Editor;

internal sealed class UnityImportWindow : EditorWindow {
    public static readonly UnityImportWindow Instance = new();

    public UnityImportWindow() {
        DockKey = "win.unityimport";
        Title = "Import Unity Package";
        Icon = EditorIcons.Package;
        NoCollapse = true;
        DesiredSize = new Vector2(560, 340);
    }

    static string lastLog = "";
    static string sourcePath = "";
    static string subfolder = "Imported";
    static bool running;

    static volatile string progressStatus = "";
    static volatile float progressFraction;

    public static bool IsOpen => Instance.Open;
    public static void Show() => Instance.Open = true;

    public static void ImportPackage(string packageOrFolderPath, string destSubfolder = "Imported") {
        if (running)
            return;
        sourcePath = packageOrFolderPath ?? "";
        subfolder = string.IsNullOrWhiteSpace(destSubfolder) ? "Imported" : destSubfolder;
        var isPackage = sourcePath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase);
        StartImport(isPackage);
    }

    public static string LastResult => lastLog;

    public static bool IsBusy => running;
    public static string BusyStatus => progressStatus;
    public static float BusyFraction => progressFraction;

    static void SetProgress(string status, float fraction) {
        progressStatus = status;
        progressFraction = Math.Clamp(fraction, 0f, 1f);
    }

    protected override void OnGui(IEditorGui gui) {
        float scale = gui.Scale;
        gui.TextWrapped(
            "Import a Unity asset (.unitypackage) or an unpacked Unity \"Assets\" folder. Meshes, " +
            "textures and materials come across; any Unity scenes/prefabs become openable .scene files.");
        gui.Separator();

        gui.TextDisabled("Source");
        gui.SetNextItemWidth(-110 * scale);
        gui.InputText("##src", ref sourcePath, 1024);
        gui.SameLine();
        if (gui.Button(".unitypackage", new Vector2(100 * scale, 0))) {
            var picked = NativeDialogs.PickFile("Select Unity Package", "Unity Package", [".unitypackage"]);
            if (picked is not null) sourcePath = picked;
        }
        gui.SameLine();
        if (gui.Button("Folder...", new Vector2(-1, 0))) {
            var picked = NativeDialogs.PickFolder("Select Unpacked Unity Assets Folder");
            if (picked is not null) sourcePath = picked;
        }

        var isPackage = sourcePath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase);
        if (isPackage) {
            gui.Dummy(new Vector2(0, 4));
            gui.TextDisabled("Destination subfolder (under Assets/)");
            gui.SetNextItemWidth(-1);
            gui.InputText("##sub", ref subfolder, 256);
        }

        gui.Dummy(new Vector2(0, 8));
        bool canImport = !running && sourcePath.Length > 0 &&
                         (File.Exists(sourcePath) || Directory.Exists(sourcePath));
        gui.BeginDisabled(!canImport);
        if (gui.Button(running ? "Importing..." : $"{EditorIcons.Package}  Import", new Vector2(160 * scale, 0)))
            StartImport(isPackage);
        gui.EndDisabled();

        if (!string.IsNullOrEmpty(lastLog)) {
            gui.Dummy(new Vector2(0, 8));
            gui.Separator();
            gui.TextDisabled("Result");
            gui.BeginChild("##unityimportlog", new Vector2(0, 0), border: true);
            gui.TextWrapped(lastLog);
            gui.EndChild();
        }
    }

    static void StartImport(bool isPackage) {
        running = true;
        lastLog = "Working...";

        BallisticProject project = AssetDatabase.Project;
        if (project is null) {
            Finish("No project open.");
            return;
        }
        var src = sourcePath;
        var sub = Sanitize(subfolder);

        SetProgress("Extracting Unity package...", 0f);
        Debugging.Log($"Unity import: starting '{Path.GetFileName(src)}' -> Assets/{sub}");
        Task.Run(() => {
            try {
                string[] extractRoots;
                List<string> scenes = new();
                List<string> prefabs = new();
                string summary;

                if (isPackage) {
                    var dest = Path.Combine(project.AssetsPath, sub);
                    UnityPackageReader.Result extracted = UnityPackageReader.Extract(src, dest);
                    scenes.AddRange(extracted.Scenes);
                    prefabs.AddRange(extracted.Prefabs);
                    extractRoots = [dest];
                    summary = $"Extracted {extracted.ExtractedFiles.Count} files into Assets/{sub}.";
                }
                else {
                    extractRoots = [src];
                    scenes.AddRange(Directory.EnumerateFiles(src, "*.unity", SearchOption.AllDirectories));
                    prefabs.AddRange(Directory.EnumerateFiles(src, "*.prefab", SearchOption.AllDirectories));
                    summary = $"Scanning '{Path.GetFileName(src)}'.";
                }

                SetProgress("Mapping Unity GUIDs...", 0.15f);
                Dictionary<string, string> guidToFile = UnityMetaGuidMap.Build(extractRoots);

                int converted = ConvertAll(scenes, prefabs, guidToFile, project);
                summary += $"  Converted {converted} scene(s)/prefab(s).";

                SetProgress("Importing meshes & registering scenes...", 0.85f);
                AsyncAssetImport.Request("Importing Unity assets...", onFinished: () =>
                    Finish(summary + " Open the converted scenes from the Asset Browser."));
            }
            catch (Exception exception) {
                Debugging.LogError($"Unity import failed: {exception}");
                Finish($"Import failed: {exception.Message}");
            }
        });
    }

    static int ConvertAll(List<string> scenes, List<string> prefabs,
        Dictionary<string, string> guidToFile, BallisticProject project) {
        var materialGen = new UnityMaterialGenerator(guidToFile, project);
        var prefabMesh = new PrefabMeshResolver(guidToFile, project, materialGen);

        var resolvers = new UnitySceneConverter.Resolvers {
            MeshGuidToAssetRef = guid => GuidToProjectRef(guid, guidToFile, project),
            MaterialGuidToAssetRef = guid => materialGen.Resolve(guid),
            PrefabGuidToMeshRef = guid => prefabMesh.Resolve(guid),
            PrefabGuidToMaterialRef = guid => prefabMesh.ResolveMaterial(guid),
        };

        var count = 0;
        var total = scenes.Count + prefabs.Count;
        var done = 0;

        void ConvertOne(string unityFile, bool isPrefab) {
            try {
                var output = Path.ChangeExtension(unityFile, ".scene");
                UnitySceneConverter.Report r = UnitySceneConverter.Convert(unityFile, output, resolvers, isPrefab);
                count++;
                Debugging.Log(
                    $"Unity import: '{Path.GetFileName(unityFile)}' -> {r.Entities} entities " +
                    $"({r.WithMesh} mesh, {r.PrefabInstances} prefab-instances, " +
                    $"{r.PrefabInstancesUnresolved} unresolved).");
            }
            catch (Exception exception) {
                Debugging.LogWarning($"Unity import: failed to convert '{unityFile}': {exception.Message}");
            }
            done++;
            if (total > 0)
                SetProgress($"Converting scenes... ({done}/{total})", 0.15f + 0.70f * done / total);
        }

        foreach (var f in scenes) ConvertOne(f, isPrefab: false);
        foreach (var f in prefabs) ConvertOne(f, isPrefab: true);
        return count;
    }

    internal static string GuidToProjectRef(string guid, Dictionary<string, string> guidToFile, BallisticProject project) {
        if (guid is null || !guidToFile.TryGetValue(guid, out var absolute))
            return null;
        if (!File.Exists(absolute))
            return null;
        var full = Path.GetFullPath(absolute);
        if (!full.StartsWith(Path.GetFullPath(project.RootPath), StringComparison.OrdinalIgnoreCase))
            return null;
        return project.ToAssetPath(full);
    }

    static void Finish(string message) {
        lastLog = message;
        running = false;
        SetProgress("", 0f);
    }

    static string Sanitize(string name) {
        if (string.IsNullOrWhiteSpace(name))
            return "Imported";
        char[] invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().Trim('/', '\\');
        return clean.Length > 0 ? clean : "Imported";
    }
}

internal sealed class PrefabMeshResolver(
    Dictionary<string, string> guidToFile, BallisticProject project, UnityMaterialGenerator materials) {
    readonly Dictionary<string, string> meshCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> matCache = new(StringComparer.OrdinalIgnoreCase);

    public string Resolve(string prefabGuid) => Cached(meshCache, prefabGuid, ResolveMeshUncached);
    public string ResolveMaterial(string prefabGuid) => Cached(matCache, prefabGuid, ResolveMaterialUncached);

    static string Cached(Dictionary<string, string> cache, string key, Func<string, string> fn) {
        if (string.IsNullOrEmpty(key)) return null;
        if (cache.TryGetValue(key, out var c)) return c;
        var r = fn(key);
        cache[key] = r;
        return r;
    }

    string ResolveMeshUncached(string prefabGuid) {
        UnityRef mesh = Lod0MeshFilter(prefabGuid)?.Mesh ?? default;
        if (mesh.IsNull || !mesh.IsExternal) return null;
        return UnityImportWindow.GuidToProjectRef(mesh.Guid, guidToFile, project);
    }

    string ResolveMaterialUncached(string prefabGuid) {
        UnityYamlScene prefab = LoadPrefab(prefabGuid);
        if (prefab is null) return null;
        var goName = ToGameObjectName(prefab);
        UnityRef best = default;
        foreach (UnityMeshRenderer mr in prefab.MeshRenderers.Values) {
            if (mr.Materials.Count == 0 || !mr.Materials[0].IsExternal) continue;
            var name = goName.GetValueOrDefault(mr.GameObjectId, "");
            if (name.EndsWith("_LOD0", StringComparison.OrdinalIgnoreCase)) { best = mr.Materials[0]; break; }
            if (best.IsNull) best = mr.Materials[0];
        }
        return best.IsNull ? null : materials.Resolve(best.Guid);
    }

    UnityMeshFilter Lod0MeshFilter(string prefabGuid) {
        UnityYamlScene prefab = LoadPrefab(prefabGuid);
        if (prefab is null) return null;
        var goName = ToGameObjectName(prefab);
        UnityMeshFilter fallback = null;
        foreach (UnityMeshFilter mf in prefab.MeshFilters.Values) {
            if (!mf.Mesh.IsExternal || mf.Mesh.Guid is null) continue;
            var name = goName.GetValueOrDefault(mf.GameObjectId, "");
            if (name.EndsWith("_LOD0", StringComparison.OrdinalIgnoreCase)) return mf;
            fallback ??= mf;
        }
        return fallback;
    }

    readonly Dictionary<string, UnityYamlScene> prefabCache = new(StringComparer.OrdinalIgnoreCase);
    UnityYamlScene LoadPrefab(string prefabGuid) {
        if (prefabCache.TryGetValue(prefabGuid, out var cached)) return cached;
        UnityYamlScene prefab = null;
        if (guidToFile.TryGetValue(prefabGuid, out var path) && File.Exists(path)) {
            try { prefab = UnityYamlParser.Parse(File.ReadAllText(path)); }
            catch { prefab = null; }
        }
        prefabCache[prefabGuid] = prefab;
        return prefab;
    }

    static Dictionary<long, string> ToGameObjectName(UnityYamlScene s) {
        var map = new Dictionary<long, string>();
        foreach (UnityGameObject go in s.GameObjects.Values)
            map[go.FileId] = go.Name ?? "";
        return map;
    }
}

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
