using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Unity;
using BallisticEngine.Serialization;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Converts a parsed Unity scene/prefab into a Ballistic .scene (YAML), building the SceneDocument
// directly — no live entities, no GL — so it runs as a one-shot import action. Walks Unity's
// transform hierarchy into entity documents with parent wiring, mapping MeshFilter+MeshRenderer to
// StaticMeshRenderer (mesh + per-submesh sharedMaterial) and resolving Unity's {guid} asset
// references to project asset refs via the injected resolvers.
//
// COORDINATE SYSTEMS. Unity is Y-up, LEFT-handed. The engine is Y-up, RIGHT-handed, and its meshes
// are imported through Assimp (which converts FBX to RH). To keep the dressed layout matching the
// RH geometry we mirror the X axis: position.x negated, and the rotation quaternion's Y/Z negated
// (the standard LH->RH conversion when flipping X). Scale is unaffected.
public static class UnitySceneConverter {
    // guid (32-hex, Unity meta) -> project asset path ("Assets/..."), or null if not in the project.
    // Two resolvers because a Unity MeshFilter points at the MODEL asset (an .fbx) while a
    // MeshRenderer points at a Unity .mat — which we map to the engine .mat generated beside the model.
    public sealed class Resolvers {
        public Func<string, string> MeshGuidToAssetRef;        // Unity mesh guid -> engine model ref
        public Func<string, string> MaterialGuidToAssetRef;    // Unity .mat guid -> engine .mat ref
        // A nested-prefab instance's source-prefab guid -> the engine MESH ref to render for it.
        // Resolved by the caller (it reads the referenced .prefab and pulls its LOD0 mesh guid), so the
        // dressed scene's PrefabInstances become real meshes at their overridden transforms.
        public Func<string, string> PrefabGuidToMeshRef;
        // Same, for the prefab's LOD0 material -> engine .mat ref (so instances carry real materials).
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

    // Converts one Unity scene/prefab file to a .scene written at outputAbsolutePath.
    // isPrefab: the source is a .prefab (a single instantiable tree) rather than a .unity scene — we
    // then DON'T inject a fallback camera/light (those belong in a scene, not a reusable prefab).
    public static Report Convert(string unityFileAbsolutePath, string outputAbsolutePath, Resolvers resolvers,
        bool isPrefab = false) {
        var text = File.ReadAllText(unityFileAbsolutePath);
        UnityYamlScene unity = UnityYamlParser.Parse(text);

        var doc = new SceneDocument { Name = Path.GetFileNameWithoutExtension(unityFileAbsolutePath) };
        var report = new Report();

        // A transform fileID -> the entity id we'll wire children to. Both GameObjects AND prefab
        // instances can be transform parents, so the map spans both. (For a prefab instance, its
        // m_TransformParent points at a Transform fileID; for a GameObject child, m_Father does too.)
        var entityIdByGameObject = new Dictionary<long, string>();
        foreach (long goId in unity.GameObjects.Keys)
            entityIdByGameObject[goId] = NewId();
        var entityIdByPrefabInstance = new Dictionary<long, string>();
        foreach (long piId in unity.PrefabInstances.Keys)
            entityIdByPrefabInstance[piId] = NewId();

        // transform fileID -> owning entity id, for parent wiring across both kinds.
        var entityIdByTransform = new Dictionary<long, string>();
        foreach (UnityTransform t in unity.Transforms.Values)
            if (entityIdByGameObject.TryGetValue(t.GameObjectId, out var eid))
                entityIdByTransform[t.FileId] = eid;

        // Index transform/filter/renderer by their owning GameObject for O(1) lookup per entity.
        var transformByGo = IndexByGameObject(unity.Transforms.Values, t => t.GameObjectId);
        var filterByGo = IndexByGameObject(unity.MeshFilters.Values, m => m.GameObjectId);
        var rendererByGo = IndexByGameObject(unity.MeshRenderers.Values, m => m.GameObjectId);

        string ParentIdForTransform(long transformFileId) =>
            entityIdByTransform.GetValueOrDefault(transformFileId);

        foreach ((long goId, UnityGameObject go) in unity.GameObjects) {
            // Skip GameObjects with no transform (shouldn't happen in a valid scene).
            if (!transformByGo.TryGetValue(goId, out UnityTransform transform))
                continue;

            // LOD1+ children of a LODGroup render the SAME prop at lower detail — emitting them stacks
            // multiple meshes on the same spot. Keep only LOD0 (and non-LOD objects).
            if (IsNonZeroLod(go.Name))
                continue;

            var entity = new EntityDocument {
                Id = entityIdByGameObject[goId],
                Name = string.IsNullOrWhiteSpace(go.Name) ? "GameObject" : go.Name,
                // Effect meshes (light shafts/fog/sky/water) import disabled — see IsEffectMesh.
                IsActive = go.Active && !(filterByGo.ContainsKey(goId) && IsEffectMesh(go.Name)),
                Transform = ConvertTransform(transform.LocalPosition, transform.LocalRotation, transform.LocalScale),
            };

            // Wire parent by file-local id (the engine resolves it on load).
            if (transform.FatherId != 0)
                entity.Transform.Parent = ParentIdForTransform(transform.FatherId);

            // Mesh: a MeshFilter (geometry) + MeshRenderer (materials) become a StaticMeshRenderer.
            if (filterByGo.TryGetValue(goId, out UnityMeshFilter filter) && !filter.Mesh.IsNull) {
                AddMeshRenderer(entity, filter, rendererByGo.GetValueOrDefault(goId), resolvers, report,
                    unityFileAbsolutePath);
            }

            doc.Entities.Add(entity);
            report.Entities++;
        }

        // Nested-prefab instances ARE the set dressing of a dressed scene (1000+ of them in a Quixel
        // demo scene). Each becomes one entity at its overridden transform with a StaticMeshRenderer
        // pulled from the source prefab's mesh.
        foreach ((long piId, UnityPrefabInstance pi) in unity.PrefabInstances) {
            var entity = new EntityDocument {
                Id = entityIdByPrefabInstance[piId],
                Name = string.IsNullOrWhiteSpace(pi.Name) ? "Prefab" : pi.Name,
                // Effect meshes (light shafts/fog/sky/water) import disabled — see IsEffectMesh.
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

        // A scene gets a fallback camera/light so it opens to something visible; a prefab does not
        // (it's a reusable tree, not a standalone scene).
        if (!isPrefab)
            EnsureViewable(doc);

        File.WriteAllText(outputAbsolutePath, SceneYaml.Serializer.Serialize(doc));
        return report;
    }

    // Shader-driven EFFECT meshes (light-shaft quads, fog cards, sky domes, water planes). Their Unity
    // materials are shadergraphs (transparent/additive/scrolling) that don't map to the engine's
    // Standard shader, so they'd render as giant OPAQUE grey walls dominating the scene. Imported but
    // DISABLED — re-enable per entity after assigning a suitable engine material.
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

    // Unity LODGroup children are named "<prop>_LOD0".."<prop>_LOD3"; keep LOD0, skip the rest.
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

        // First material -> the renderer's sharedMaterial (the engine binds per-submesh from the
        // model's baked refs; a single override material covers the common single-material prop).
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
        // LH (Unity) -> RH (engine): mirror X. Position.x negated; rotation Y/Z negated. Scale stays.
        return new TransformDocument {
            Position = new Vector3(-pos.X, pos.Y, pos.Z),
            Rotation = new Quaternion(rot.X, -rot.Y, -rot.Z, rot.W),
            Scale = scale,
        };
    }

    // Ensures the converted scene has a sky (for IBL ambient — without it everything is a black
    // silhouette since Unity's lighting/skybox don't come across), plus a light and a camera.
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

        // A ProceduralSky is a SceneBehaviour (scene-wide) — it provides the sky dome AND the IBL
        // ambient that PBR materials need to be visible. Without it the import opens pitch black.
        if (!hasSky) {
            doc.SceneComponents.Add(new ComponentDocument {
                Type = "ProceduralSky",
                // Strong sky exposure = strong IBL ambient, so geometry is well-lit even when it sits
                // below the sky's ground plane (imported scenes often do) and the sun grazes it. The
                // engine's bright sample scenes drive IBL similarly (a high-exposure skybox).
                Members = { ["exposure"] = 8.0f },
            });
        }

        if (!hasLight) {
            doc.Entities.Add(new EntityDocument {
                Id = NewId(),
                Name = "Directional Light",
                // STEEP midday sun — matches the orientation of the engine's known-good sample scenes
                // (forward points up-ish; the renderer shines light along -Forward, so this lights the
                // ground steeply). Imported ground-laid scenes need a steep sun or they read near-black.
                Transform = new TransformDocument { Rotation = LookRotation(new Vector3(-0.25f, -0.95f, -0.18f).Normalized()) },
                Components = { new ComponentDocument {
                    Type = "DirectionalLight",
                    Members = {
                        ["illuminance"] = 90000f,    // clear midday sun (lux)
                        ["colorTemperature"] = 5800f,
                        ["ambientIntensity"] = 1.0f, // strong sky-ambient fill so nothing is pitch black
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
            map.TryAdd(goId(item), item); // first wins if a GO somehow has two of a kind
        return map;
    }

    static Quaternion LookRotation(Vector3 forward) {
        Vector3 up = Math.Abs(Vector3.Dot(forward, Vector3.UnitY)) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        Matrix4 look = Matrix4.LookAt(Vector3.Zero, forward, up);
        look.Invert();
        return look.ExtractRotation();
    }

    static string NewId() => Guid.NewGuid().ToString("N");
}
