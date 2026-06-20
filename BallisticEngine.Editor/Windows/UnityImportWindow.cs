using System.Numerics;
using System.Threading.Tasks;
using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.AssetPipeline.Unity;

namespace BallisticEngine.Editor;

// "Import Unity Package" tool (Assets menu). Lets you pull a Unity asset into the project in one
// action: pick a .unitypackage (or an already-unpacked Unity "Assets" folder), and it
//   1. extracts the package into Assets/<subfolder> (rebuilding the original path tree),
//   2. refreshes so the engine imports the meshes/textures (filename-convention material auto-bind
//      from ModelImporter v7 textures the otherwise-empty Megascans/PBR materials),
//   3. converts every Unity .unity/.prefab inside into a Ballistic .scene (transform hierarchy +
//      StaticMeshRenderers, LH->RH coordinate fix), resolving Unity's GUID refs to project assets,
//   4. refreshes again so the new .scene assets register and you can open them directly.
//
// This is the answer to "FBX-only packs have no layout": grab the Unity version of an asset and its
// dressed scene/prefab comes across as an openable Ballistic scene. The conversion runs on the
// background import thread; progress shows in the same busy overlay as a normal refresh.
// Phase-6/8 EditorWindow: the WINDOW is an EditorWindow instance drawn through WindowShell + IEditorGui
// (zero raw ImGui in the body). The SERVICE half stays static — ImportPackage (remote pipe), the
// BusyOverlay status fields, and the worker-touched state are shared across threads and not tied to the
// window instance. A static facade (Open / IsOpen) targets the single shared Instance. The helper classes
// below (PrefabMeshResolver / UnityMaterialGenerator) and the GuidToProjectRef they call stay as-is.
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
    static string sourcePath = "";            // the picked .unitypackage or Unity Assets folder
    static string subfolder = "Imported";     // Assets/<subfolder> destination for a package
    static bool running;

    // Progress published from the worker thread, read by the window + BusyOverlay (volatile, no lock).
    static volatile string progressStatus = "";
    static volatile float progressFraction;

    public static bool IsOpen => Instance.Open;
    // Named Show, not Open (EditorWindow.Open is the instance show-state field).
    public static void Show() => Instance.Open = true;

    // Headless/remote entrypoint (the editor command pipe): kick off an import without the GUI dialog.
    // Returns immediately; progress shows in the BusyOverlay and the result lands in lastLog.
    public static void ImportPackage(string packageOrFolderPath, string destSubfolder = "Imported") {
        if (running)
            return;
        sourcePath = packageOrFolderPath ?? "";
        subfolder = string.IsNullOrWhiteSpace(destSubfolder) ? "Imported" : destSubfolder;
        var isPackage = sourcePath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase);
        StartImport(isPackage);
    }

    public static string LastResult => lastLog;

    // Surfaced so the editor's BusyOverlay can show our long extract/convert phase (the asset Refresh
    // it already covers). True only while our own worker is busy, before it hands off to the refresh.
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
        // (folder button on its own line below to keep the row from overflowing)
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

        // EVERYTHING heavy (extract a multi-thousand-file package + parse/convert hundreds of Unity
        // scenes) runs on a worker so the editor doesn't freeze. The BusyOverlay shows progress via
        // the static status fields below; the only main-thread step is the asset Refresh, requested at
        // the end through AsyncAssetImport (itself a worker). Two phases:
        //   1. extract + convert -> writes files (worker)
        //   2. AsyncAssetImport.Request -> imports meshes/textures + registers the .scenes (worker)
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

                // Hand back to the refresh worker to import meshes/textures and register the .scenes.
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

    // Converts every scene (.unity) and prefab (.prefab). Prefabs pass isPrefab:true so they don't get
    // a fallback camera/light. A scene's nested-prefab instances resolve their mesh via the source
    // prefab guid -> that prefab's LOD0 mesh (PrefabMeshResolver, cached).
    static int ConvertAll(List<string> scenes, List<string> prefabs,
        Dictionary<string, string> guidToFile, BallisticProject project) {
        var materialGen = new UnityMaterialGenerator(guidToFile, project);
        var prefabMesh = new PrefabMeshResolver(guidToFile, project, materialGen);

        var resolvers = new UnitySceneConverter.Resolvers {
            MeshGuidToAssetRef = guid => GuidToProjectRef(guid, guidToFile, project),
            // Unity .mat guid -> a freshly generated engine .mat (real texture bindings parsed from
            // the Unity material — handles any texture naming, unlike filename convention).
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

    // Unity guid -> project-relative asset ref ("Assets/..."), via the on-disk file the meta names.
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

// Resolves a Unity nested-prefab's source-prefab GUID to the engine MESH + MATERIAL refs to render
// for it. A dressed Quixel scene places ~1000 prefab instances; each references a .prefab whose LOD0
// MeshFilter names the FBX mesh and whose LOD0 MeshRenderer names the Unity .mat. We open the .prefab
// once per guid (cached), pull both, and map them to engine refs (the .mat via UnityMaterialGenerator).
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
        // The LOD0 renderer's first material; fall back to any renderer's first external material.
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

// Generates an engine .mat from a Unity .mat (parsed via UnityMaterialParser), mapping the Unity
// texture slots (any naming — Albedo/BaseColorMap/MainTex, Normal/BumpMap, MaskMap/_DR packed ORM,
// OcclusionMap) to engine slots and resolving each texture guid to a project ref. Writes
// "<UnityMat>.bal.mat" beside the source once per guid (cached) and returns its ref. This is the
// robust path for materials that filename-convention binding misses.
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
            if (unity.MaskIsPacked) def.PackedOrm = true;   // ORD/ORM packed map, not plain metallic
        }

        if (unity.BaseColor is { Length: >= 3 }) def.BaseColor = unity.BaseColor;
        if (unity.Metallic is { } m) def.Metallic = m;
        if (unity.Smoothness is { } s) def.Roughness = Math.Clamp(1f - s, 0f, 1f); // Unity smoothness -> roughness
        if (unity.AlphaCutout) def.Cutout = true; // foliage cards: alpha-clip + double-sided

        // Nothing resolved -> let the model's own generated materials handle it (return null).
        if (def.Textures.Count == 0 && def.BaseColor is null)
            return null;

        // Write "<name>.bal.mat" beside the Unity .mat so it imports as a sibling engine material.
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

        // CRITICAL: a normal/packed map imported as the default Diffuse type binds to the wrong sampler
        // and gets sRGB-decoded (normals/ORM must be linear). Set the texture's .meta textureType to
        // match the slot so the importer treats it correctly — same heal ModelImporter does.
        EnsureTextureType(absolute, slot);
        def.Textures[slot] = refPath;
    }

    // Sets/corrects a texture .meta's textureType to the engine slot name. Creates the meta if absent.
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
