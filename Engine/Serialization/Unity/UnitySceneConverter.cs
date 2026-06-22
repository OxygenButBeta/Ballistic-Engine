using BallisticEngine.AssetPipeline.Unity;
using BallisticEngine.Serialization;

namespace BallisticEngine;

public static class UnitySceneConverter {
    public sealed class Resolvers {
        public Func<string, string> MeshGuidToAssetRef;

        public Func<string, string> MaterialGuidToAssetRef;

        public Func<string, string> PrefabGuidToMeshRef;
        public Func<string, string> PrefabGuidToMaterialRef;
    }

    public sealed class Report {
        public int Entities;
        public int WithMesh;
        public int MeshesUnresolved;
        public int MaterialsUnresolved;
        public int PrefabInstances;
        public int PrefabInstancesUnresolved;
    }

    public static Report Convert(string unityFileAbsolutePath, string outputAbsolutePath, Resolvers resolvers,
        bool isPrefab = false) {
        var text = File.ReadAllText(unityFileAbsolutePath);
        UnityYamlScene unity = UnityYamlParser.Parse(text);

        var doc = new SceneDocument { Name = Path.GetFileNameWithoutExtension(unityFileAbsolutePath) };
        var report = new Report();

        var entityIdByGameObject = new Dictionary<long, string>();
        foreach (long goId in unity.GameObjects.Keys)
            entityIdByGameObject[goId] = NewId();
        var entityIdByPrefabInstance = new Dictionary<long, string>();
        foreach (long piId in unity.PrefabInstances.Keys)
            entityIdByPrefabInstance[piId] = NewId();

        var entityIdByTransform = new Dictionary<long, string>();
        foreach (UnityTransform t in unity.Transforms.Values)
            if (entityIdByGameObject.TryGetValue(t.GameObjectId, out var eid))
                entityIdByTransform[t.FileId] = eid;

        var transformByGo = IndexByGameObject(unity.Transforms.Values, t => t.GameObjectId);
        var filterByGo = IndexByGameObject(unity.MeshFilters.Values, m => m.GameObjectId);
        var rendererByGo = IndexByGameObject(unity.MeshRenderers.Values, m => m.GameObjectId);

        string ParentIdForTransform(long transformFileId) =>
            entityIdByTransform.GetValueOrDefault(transformFileId);

        foreach ((long goId, UnityGameObject go) in unity.GameObjects) {
            if (!transformByGo.TryGetValue(goId, out UnityTransform transform))
                continue;

            if (IsNonZeroLod(go.Name))
                continue;

            var entity = new EntityDocument {
                Id = entityIdByGameObject[goId],
                Name = string.IsNullOrWhiteSpace(go.Name) ? "GameObject" : go.Name,
                IsActive = go.Active && !(filterByGo.ContainsKey(goId) && IsEffectMesh(go.Name)),
                Transform = ConvertTransform(transform.LocalPosition, transform.LocalRotation, transform.LocalScale),
            };

            if (transform.FatherId != 0)
                entity.Transform.Parent = ParentIdForTransform(transform.FatherId);

            if (filterByGo.TryGetValue(goId, out UnityMeshFilter filter) && !filter.Mesh.IsNull) {
                AddMeshRenderer(entity, filter, rendererByGo.GetValueOrDefault(goId), resolvers, report,
                    unityFileAbsolutePath);
            }

            doc.Entities.Add(entity);
            report.Entities++;
        }

        foreach ((long piId, UnityPrefabInstance pi) in unity.PrefabInstances) {
            var entity = new EntityDocument {
                Id = entityIdByPrefabInstance[piId],
                Name = string.IsNullOrWhiteSpace(pi.Name) ? "Prefab" : pi.Name,
                IsActive = pi.Active && !IsEffectMesh(pi.Name),
                Transform = ConvertTransform(pi.LocalPosition, pi.LocalRotation, pi.LocalScale),
            };
            if (pi.TransformParentId != 0)
                entity.Transform.Parent = ParentIdForTransform(pi.TransformParentId);

            var meshRef = resolvers.PrefabGuidToMeshRef?.Invoke(pi.SourcePrefabGuid);
            if (meshRef is not null) {
                var smr = new ComponentDocument {
                    Type = "StaticMeshRenderer",
                    Members = { ["sharedMesh"] = meshRef },
                };
                var matRef = resolvers.PrefabGuidToMaterialRef?.Invoke(pi.SourcePrefabGuid);
                if (matRef is not null)
                    smr.Members["sharedMaterial"] = matRef;
                entity.Components.Add(smr);
                report.WithMesh++;
            }
            else {
                report.PrefabInstancesUnresolved++;
            }

            doc.Entities.Add(entity);
            report.Entities++;
            report.PrefabInstances++;
        }

        if (!isPrefab)
            EnsureViewable(doc);

        File.WriteAllText(outputAbsolutePath, SceneYaml.Serializer.Serialize(doc));
        return report;
    }

    static readonly string[] EffectNameHints =
        ["lightray", "light_ray", "lightshaft", "godray", "fog", "sm_sky", "bp_sky", "skydome",
         "sky_01", "water", "mist", "dust_", "vfx", "fx_"];

    static bool IsEffectMesh(string name) {
        if (string.IsNullOrEmpty(name))
            return false;
        var n = name.ToLowerInvariant();
        foreach (var hint in EffectNameHints)
            if (n.Contains(hint))
                return true;
        return false;
    }

    static bool IsNonZeroLod(string name) {
        if (string.IsNullOrEmpty(name))
            return false;
        var idx = name.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
        if (idx < 0 || idx + 4 >= name.Length)
            return false;
        var digits = name[(idx + 4)..];
        return digits.Length > 0 && digits.All(char.IsDigit) && digits != "0";
    }

    static void AddMeshRenderer(EntityDocument entity, UnityMeshFilter filter, UnityMeshRenderer renderer,
        Resolvers resolvers, Report report, string contextFile) {
        var mesh = new ComponentDocument { Type = "StaticMeshRenderer" };

        var meshRef = filter.Mesh.IsExternal
            ? resolvers.MeshGuidToAssetRef?.Invoke(filter.Mesh.Guid)
            : null;

        if (meshRef is not null) {
            mesh.Members["sharedMesh"] = meshRef;
            report.WithMesh++;
        }
        else {
            report.MeshesUnresolved++;
            Debugging.LogWarning(
                $"Unity import ({Path.GetFileName(contextFile)}): mesh guid '{filter.Mesh.Guid}' " +
                $"for entity '{entity.Name}' not found in project; mesh left empty.");
        }

        if (renderer is { Materials.Count: > 0 }) {
            UnityRef first = renderer.Materials[0];
            var matRef = first.IsExternal ? resolvers.MaterialGuidToAssetRef?.Invoke(first.Guid) : null;
            if (matRef is not null)
                mesh.Members["sharedMaterial"] = matRef;
            else if (first.IsExternal)
                report.MaterialsUnresolved++;
        }

        entity.Components.Add(mesh);
    }

    static TransformDocument ConvertTransform(Vector3 pos, Quaternion rot, Vector3 scale) {
        return new TransformDocument {
            Position = new Vector3(-pos.X, pos.Y, pos.Z),
            Rotation = new Quaternion(rot.X, -rot.Y, -rot.Z, rot.W),
            Scale = scale,
        };
    }

    static void EnsureViewable(SceneDocument doc) {
        var hasLight = false;
        var hasCamera = false;
        var hasSky = false;
        foreach (EntityDocument e in doc.Entities)
            foreach (ComponentDocument c in e.Components) {
                if (c.Type.Contains("Light", StringComparison.Ordinal)) hasLight = true;
                if (c.Type == "HDCamera") hasCamera = true;
            }
        foreach (ComponentDocument c in doc.SceneComponents)
            if (c.Type is "Skybox" or "ProceduralSky") hasSky = true;

        if (!hasSky) {
            doc.SceneComponents.Add(new ComponentDocument {
                Type = "ProceduralSky", Members = { ["exposure"] = 8.0f },
            });
        }

        if (!hasLight) {
            doc.Entities.Add(new EntityDocument {
                Id = NewId(),
                Name = "Directional Light",
                Transform = new TransformDocument { Rotation = LookRotation(new Vector3(-0.25f, -0.95f, -0.18f).Normalized()) },
                Components = { new ComponentDocument {
                    Type = "DirectionalLight",
                    Members = {
                        ["illuminance"] = 90000f,
                        ["colorTemperature"] = 5800f,
                        ["ambientIntensity"] = 1.0f,
                    },
                } },
            });
        }
        if (!hasCamera) {
            doc.Entities.Add(new EntityDocument {
                Id = NewId(),
                Name = "Camera",
                Transform = new TransformDocument { Position = new Vector3(0, 2, 6) },
                Components = {
                    new ComponentDocument { Type = "HDCamera" },
                    new ComponentDocument { Type = "FreeLookCameraController" },
                },
            });
        }
    }

    static Dictionary<long, T> IndexByGameObject<T>(IEnumerable<T> items, Func<T, long> goId) {
        var map = new Dictionary<long, T>();
        foreach (T item in items)
            map.TryAdd(goId(item), item);
        return map;
    }

    static Quaternion LookRotation(Vector3 forward) {
        Vector3 up = Math.Abs(Vector3.Dot(forward, Vector3.UnitY)) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        Matrix4 look = BMatrix.LookAt(Vector3.Zero, forward, up);
        look = look.Inverted();
        return look.ExtractRotation();
    }

    static string NewId() => Guid.NewGuid().ToString("N");
}
