using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BallisticEngine.AssetPipeline.Loaders;

internal static partial class ScenePrefetcher {
    [GeneratedRegex(@"guid:([0-9a-fA-F]{32})")]
    private static partial Regex GuidRefRegex();

    static readonly string[] MeshSourceExtensions = [".fbx", ".obj", ".gltf", ".glb", ".dae"];
    static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".hdr", ".exr", ".dds"];

    static readonly long MemoryBudgetBytes =
        Math.Min(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4, 6L << 30);

    public static void Run(AssetImportPipeline pipeline, string sceneYaml,
        Func<Guid, bool> isAlreadyLoaded, Action<int, int> progress) {
        var referenced = new HashSet<Guid>();
        foreach (Match match in GuidRefRegex().Matches(sceneYaml)) {
            if (Guid.TryParse(match.Groups[1].Value, out Guid guid))
                referenced.Add(guid);
        }

        var meshGuids = new HashSet<Guid>();
        var textureGuids = new HashSet<Guid>();

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
                Debugging.LogWarning($"Scene prefetch skipped {job.guid:N}: {exception.Message}");
            }
            finally {
                progress?.Invoke(Interlocked.Increment(ref done), total);
            }
        });

        DecodeWave(jobs);

        var discovered = new HashSet<Guid>();
        foreach (var materialRef in bakedMaterialRefs.Distinct()) {
            var matPath = AssetRef.IsGuidRef(materialRef, out Guid matGuid)
                ? pipeline.GuidToPath.GetValueOrDefault(matGuid)
                : materialRef.Replace('\\', '/');
            if (matPath is not null)
                CollectMaterialTextures(pipeline, matPath, isAlreadyLoaded, discovered);
        }

        discovered.ExceptWith(textureGuids);
        if (discovered.Count > 0) {
            total += discovered.Count;
            DecodeWave(discovered.Select(g => (g, false)).ToList());
        }

        if (overBudget > 0)
            Debugging.Log($"Scene prefetch hit its memory budget ({MemoryBudgetBytes >> 20} MB): " +
                          $"{overBudget} texture(s) will decode during the final load step instead.");
    }

    static void CollectMaterialTextures(AssetImportPipeline pipeline, string matAssetPath,
        Func<Guid, bool> isAlreadyLoaded, HashSet<Guid> textureGuids) {
        MaterialDefinition definition;
        try {
            definition = ContentText.ReadJson<MaterialDefinition>(pipeline.Project, matAssetPath);
        }
        catch {
            return;
        }
        if (definition is null)
            return;

        if (definition?.Textures is null)
            return;

        foreach (var reference in definition.Textures.Values) {
            if (string.IsNullOrEmpty(reference))
                continue;

            if (AssetRef.IsGuidRef(reference, out Guid guid)) {
            }
            else if (!pipeline.PathToGuid.TryGetValue(reference.Replace('\\', '/'), out guid)) {
                continue;
            }

            if (!isAlreadyLoaded(guid))
                textureGuids.Add(guid);
        }
    }
}
