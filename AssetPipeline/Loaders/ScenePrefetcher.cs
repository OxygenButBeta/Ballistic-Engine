using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BallisticEngine.AssetPipeline.Loaders;

// Decodes a scene's referenced mesh/texture artifacts to CPU data on worker threads, ahead of the
// main-thread scene load. Walks the scene YAML for "guid:<32hex>" refs, follows material refs to the
// textures they bind, then parallel-decodes every mesh (.bmesh) and texture (.btex) into the
// AssetDataCache. The main-thread loaders then take that warm data and only do the GL upload.
//
// Scene YAML is NOT the full picture: merged models bake per-submesh .mat refs into the .bmesh
// (the renderer auto-resolves them at load; the scene never names them). So after the first decode
// wave, the decoded meshes' SubMeshData.MaterialRefs are followed to their textures and those are
// decoded in a second wave — for heavy scenes that second wave IS the bulk of the data.
//
// Everything here is pure CPU + file I/O — no GL — so it runs safely on a background Task.
internal static partial class ScenePrefetcher {
    [GeneratedRegex(@"guid:([0-9a-fA-F]{32})")]
    private static partial Regex GuidRefRegex();

    static readonly string[] MeshSourceExtensions = [".fbx", ".obj", ".gltf", ".glb", ".dae"];
    static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".hdr", ".exr", ".dds"];

    // Cap on decoded CPU bytes held in AssetDataCache at once. Raw RGBA8 for a heavy scene can
    // exceed physical RAM (Bistro: ~8 GB decoded from ~2.7 GB of artifacts); textures past the
    // budget just decode synchronously during apply instead. A quarter of physical RAM, at most
    // 6 GB; concurrent decodes can overshoot by roughly (worker count x largest texture).
    static readonly long MemoryBudgetBytes =
        Math.Min(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4, 6L << 30);

    public static void Run(AssetImportPipeline pipeline, string sceneYaml,
        Func<Guid, bool> isAlreadyLoaded, Action<int, int> progress) {

        // 1. Collect every GUID the scene names directly.
        var referenced = new HashSet<Guid>();
        foreach (Match match in GuidRefRegex().Matches(sceneYaml)) {
            if (Guid.TryParse(match.Groups[1].Value, out Guid guid))
                referenced.Add(guid);
        }

        var meshGuids = new HashSet<Guid>();
        var textureGuids = new HashSet<Guid>();

        // 2. Classify each ref by its source extension; follow .mat refs to their texture bindings.
        foreach (Guid guid in referenced) {
            if (isAlreadyLoaded(guid))
                continue;

            var path = pipeline.GuidToPath.GetValueOrDefault(guid);
            if (path is null)
                continue;

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (MeshSourceExtensions.Contains(ext))
                meshGuids.Add(guid);
            else if (ImageExtensions.Contains(ext))
                textureGuids.Add(guid);
            else if (ext == ".mat")
                CollectMaterialTextures(pipeline, path, isAlreadyLoaded, textureGuids);
        }

        if (meshGuids.Count == 0 && textureGuids.Count == 0)
            return;

        // 3. Wave 1: parallel-decode everything the YAML names directly. Meshes and textures share
        //    one work-list so the cores stay busy regardless of the mix. Each decoded mesh also
        //    surrenders the .mat refs baked into its submeshes for wave 2.
        var jobs = new List<(Guid guid, bool isMesh)>();
        foreach (Guid g in meshGuids) jobs.Add((g, true));
        foreach (Guid g in textureGuids) jobs.Add((g, false));

        var done = 0;
        var total = jobs.Count;
        var options = new ParallelOptions {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
        };
        var bakedMaterialRefs = new ConcurrentBag<string>();
        var overBudget = 0;

        void DecodeWave(List<(Guid guid, bool isMesh)> wave) => Parallel.ForEach(wave, options, job => {
            try {
                if (job.isMesh) {
                    // Meshes always decode: wave 2's texture discovery needs their submesh refs.
                    if (MeshLoader.TryDecode(pipeline, job.guid, out MeshData mesh)) {
                        AssetDataCache.PutMesh(job.guid, in mesh);
                        foreach (SubMeshData subMesh in mesh.SubMeshes)
                            if (!string.IsNullOrEmpty(subMesh.MaterialRef))
                                bakedMaterialRefs.Add(subMesh.MaterialRef);
                    }
                }
                else if (AssetDataCache.ResidentBytes >= MemoryBudgetBytes) {
                    Interlocked.Increment(ref overBudget);
                }
                else {
                    if (TextureLoader.TryDecode(pipeline, job.guid, out TextureData texture))
                        AssetDataCache.PutTexture(job.guid, in texture);
                }
            }
            catch (Exception exception) {
                // A decode failure isn't fatal — the main-thread loader will retry synchronously and
                // log properly. Just skip the warm path for this one.
                Debugging.LogWarning($"Scene prefetch skipped {job.guid:N}: {exception.Message}");
            }
            finally {
                progress?.Invoke(Interlocked.Increment(ref done), total);
            }
        });

        DecodeWave(jobs);

        // 4. Wave 2: the baked submesh materials' textures — invisible to the YAML scan, but for
        //    merged models (Bistro et al.) this is most of the scene's data.
        var discovered = new HashSet<Guid>();
        foreach (var materialRef in bakedMaterialRefs.Distinct()) {
            var matPath = AssetRef.IsGuidRef(materialRef, out Guid matGuid)
                ? pipeline.GuidToPath.GetValueOrDefault(matGuid)
                : materialRef.Replace('\\', '/');
            if (matPath is not null)
                CollectMaterialTextures(pipeline, matPath, isAlreadyLoaded, discovered);
        }

        discovered.ExceptWith(textureGuids); // wave 1 already decoded these
        if (discovered.Count > 0) {
            total += discovered.Count;
            DecodeWave(discovered.Select(g => (g, false)).ToList());
        }

        if (overBudget > 0)
            Debugging.Log($"Scene prefetch hit its memory budget ({MemoryBudgetBytes >> 20} MB): " +
                          $"{overBudget} texture(s) will decode during the final load step instead.");
    }

    // Reads a .mat and adds the GUIDs of the textures it binds (skipping ones already loaded).
    static void CollectMaterialTextures(AssetImportPipeline pipeline, string matAssetPath,
        Func<Guid, bool> isAlreadyLoaded, HashSet<Guid> textureGuids) {
        MaterialDefinition definition;
        try {
            definition = ContentText.ReadJson<MaterialDefinition>(pipeline.Project, matAssetPath);
        }
        catch {
            return; // broken .mat — the material loader handles it later with fallbacks
        }
        if (definition is null)
            return;

        if (definition?.Textures is null)
            return;

        foreach (var reference in definition.Textures.Values) {
            if (string.IsNullOrEmpty(reference))
                continue;

            // Texture refs are either "guid:<hex>" or "Assets/...path".
            if (AssetRef.IsGuidRef(reference, out Guid guid)) {
                // already a guid
            }
            else if (!pipeline.PathToGuid.TryGetValue(reference.Replace('\\', '/'), out guid)) {
                continue;
            }

            if (!isAlreadyLoaded(guid))
                textureGuids.Add(guid);
        }
    }
}
