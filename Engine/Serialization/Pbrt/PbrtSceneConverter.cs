using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Converts a parsed pbrt scene into a Ballistic .scene (YAML) plus a sibling "<Scene>_Materials/"
// folder of generated .mat files. Builds the SceneDocument directly (no live entities, no GL) so it
// runs during asset import. Mirrors FalcorSceneConverter/BlendSceneConverter; injected into
// PbrtSceneImporter.Converter by EngineBootstrap so AssetPipeline/ stays free of Engine types.
//
// COORDINATE SYSTEMS. pbrt is LEFT-HANDED (view direction down +z). The engine is right-handed, +y
// up. Most heavy pbrt scenes (San Miguel, Killeroo) are +z-up; some (Bistro) are +y-up. We can't
// know world-up universally, so we INFER it from the camera's LookAt up vector and build a single
// basis-change matrix B applied to every transform (mesh, camera, light). The handedness flip is a
// reflection; the engine renders the resulting right-handed geometry directly (Assimp imports the
// .ply meshes in pbrt's own space, then B reorients the instance). Calibrated on Killeroo first.
public static class PbrtSceneConverter {
    public static void Convert(string pbrtAbsolutePath, string outputAbsolutePath,
        Func<string, string> resolveAssetPath = null) {
        PbrtSceneData data = PbrtSceneParser.Parse(pbrtAbsolutePath);

        var sceneName = Path.GetFileNameWithoutExtension(pbrtAbsolutePath);
        var doc = new SceneDocument { Name = sceneName };

        // Basis change pbrt-space -> engine-space, inferred from the scene's up axis.
        Matrix4 basis = InferBasis(data);

        var matCtx = new MaterialContext(pbrtAbsolutePath, sceneName, resolveAssetPath);

        AddCamera(doc, data, basis);
        AddMeshes(doc, data, basis, matCtx);
        AddLights(doc, data, basis);
        AddSkybox(doc, data, resolveAssetPath);

        if (doc.Entities.All(e => !HasLight(e)) && data.EnvMapPath == null)
            AddDefaultLight(doc);

        File.WriteAllText(outputAbsolutePath, SceneYaml.Serializer.Serialize(doc));
        matCtx.Flush();
    }

    // ---- coordinate basis -----------------------------------------------------

    // pbrt -> engine basis. MUST be winding-preserving (determinant +1): we reference .ply meshes in
    // place and can't flip their triangle indices, and the renderer back-face-culls. A handedness
    // FLIP (negative scale, det -1) reverses every triangle's winding so the whole scene culls to the
    // front face -> invisible viewport. So we DON'T flip handedness; we only re-orient the up axis
    // with a pure rotation. pbrt is left-handed and the engine right-handed, so the result is
    // mirrored left<->right vs a pbrt reference render — visually fine for a scene importer, and far
    // better than an empty viewport. Up axis is inferred from the camera's world up.
    //   +y-up scene  -> identity (pbrt and engine both look down +z, up +y).
    //   +z-up scene  -> rotate -90 deg about X so +z maps to +y: (x,y,z) -> (x,z,-y). det +1.
    static Matrix4 InferBasis(PbrtSceneData data) {
        return IsZUp(data) ? Matrix4.CreateRotationX(-MathHelper.PiOver2) : Matrix4.Identity;
    }

    static bool IsZUp(PbrtSceneData data) {
        if (data.Camera == null) return true; // default assumption for headless geometry dumps
        // The camera's world up is its local +Y row of cameraToWorld.
        Vector3 up = new Vector3(
            data.Camera.CameraToWorld.Row1.X,
            data.Camera.CameraToWorld.Row1.Y,
            data.Camera.CameraToWorld.Row1.Z);
        return Math.Abs(up.Z) > Math.Abs(up.Y);
    }

    // ---- camera ---------------------------------------------------------------

    static void AddCamera(SceneDocument doc, PbrtSceneData data, Matrix4 basis) {
        PbrtCamera cam = data.Camera ?? new PbrtCamera();
        Matrix4 world = cam.CameraToWorld * basis;
        Decompose(world, out Vector3 position, out Quaternion rotation, out _);

        doc.Entities.Add(new EntityDocument {
            Id = NewId(),
            Name = "Camera",
            Transform = new TransformDocument { Position = position, Rotation = rotation },
            Components = {
                new ComponentDocument { Type = "HDCamera" },
                new ComponentDocument { Type = "FreeLookCameraController" },
            },
        });
    }

    // ---- meshes ---------------------------------------------------------------

    static void AddMeshes(SceneDocument doc, PbrtSceneData data, Matrix4 basis, MaterialContext matCtx) {
        int inlineIndex = 0;
        int missing = 0;
        int areaLights = 0;

        foreach (PbrtMesh mesh in data.Meshes) {
            string plyAbsolute = mesh.PlyFile;
            if (plyAbsolute == null && mesh.Positions != null) {
                // Inline trianglemesh -> write a sibling .ply so it imports via the same Assimp path.
                plyAbsolute = matCtx.WriteInlinePly(mesh, inlineIndex++);
            }
            if (plyAbsolute == null) continue;

            string meshRef = matCtx.Resolve(plyAbsolute);
            if (meshRef == null) { missing++; continue; }

            string matRef = matCtx.MaterialRefFor(mesh, data);

            Matrix4 world = mesh.ObjectToWorld * basis;
            Decompose(world, out Vector3 pos, out Quaternion rot, out Vector3 scale);

            var renderer = new ComponentDocument {
                Type = "StaticMeshRenderer",
                Members = { ["sharedMesh"] = meshRef },
            };
            if (matRef != null) renderer.Members["sharedMaterial"] = matRef;

            doc.Entities.Add(new EntityDocument {
                Id = NewId(),
                Name = Path.GetFileNameWithoutExtension(plyAbsolute),
                Transform = new TransformDocument { Position = pos, Rotation = rot, Scale = scale },
                Components = { renderer },
            });

            // pbrt area lights are emissive shapes (window panels, lamp panels). The engine has no
            // area-light primitive, so approximate each as a PointLight at the emitter's centroid —
            // without this the room is lit only by IBL and dark PBR surfaces read as chrome.
            if (mesh.IsEmissive && TryEmitterPointLight(mesh, world, out EntityDocument light)) {
                light.Name = $"Area Light {++areaLights}";
                doc.Entities.Add(light);
            }
        }

        if (missing > 0)
            Debugging.LogWarning($"pbrt: {missing} mesh file(s) were outside the project or missing; those entities were skipped.");
        if (areaLights > 0)
            Debugging.Log($"pbrt: converted {areaLights} area-light emitter(s) to point lights.");
    }

    // Builds a PointLight at an emissive mesh's world-space centroid. Returns false if the mesh has
    // no usable geometry (external .ply: we don't have its verts here, so skip — rare for area lights,
    // which pbrt scenes author as inline trianglemesh quads).
    static bool TryEmitterPointLight(PbrtMesh mesh, Matrix4 world, out EntityDocument light) {
        light = null;
        if (mesh.Positions == null || mesh.Positions.Count < 3) return false;

        // Object-space centroid, then to world.
        var centroid = Vector3.Zero;
        int n = mesh.Positions.Count / 3;
        for (int i = 0; i < n; i++)
            centroid += new Vector3(mesh.Positions[i * 3], mesh.Positions[i * 3 + 1], mesh.Positions[i * 3 + 2]);
        centroid /= n;
        Vector3 worldCentroid = (new Vector4(centroid, 1f) * world).Xyz;

        // L is radiance; scale to a believable lumen range. Area lights span a wide L (here ~1..18),
        // so map proportionally and clamp.
        float L = MathF.Max(mesh.EmissiveRadiance.X, MathF.Max(mesh.EmissiveRadiance.Y, mesh.EmissiveRadiance.Z));
        Vector3 colour = L > 1e-6f ? mesh.EmissiveRadiance / L : Vector3.One;
        float lumens = Math.Clamp(L * 800f, 200f, 40000f);

        light = new EntityDocument {
            Id = NewId(),
            Transform = new TransformDocument { Position = worldCentroid },
            Components = {
                new ComponentDocument {
                    Type = "PointLight",
                    Members = { ["color"] = colour, ["lumens"] = lumens },
                },
            },
        };
        return true;
    }

    // ---- lights ---------------------------------------------------------------

    static void AddLights(SceneDocument doc, PbrtSceneData data, Matrix4 basis) {
        int dir = 0, point = 0, spot = 0;
        foreach (PbrtLight light in data.Lights) {
            switch (light.Type) {
                case "distant": {
                    Vector3 d = TransformDir((light.To - light.From), light.LightToWorld, basis);
                    doc.Entities.Add(LightEntity(NextName("Directional Light", data, "distant", ref dir),
                        Vector3.Zero, LookRotation(SafeDir(d), Vector3.UnitY),
                        new ComponentDocument {
                            Type = "DirectionalLight",
                            Members = { ["color"] = light.Color, ["illuminance"] = Math.Clamp(light.Intensity * 50000f, 0f, 150000f) },
                        }));
                    break;
                }
                case "point": {
                    Vector3 p = TransformPoint(light.From, light.LightToWorld, basis);
                    doc.Entities.Add(LightEntity(NextName("Point Light", data, "point", ref point),
                        p, Quaternion.Identity,
                        new ComponentDocument {
                            Type = "PointLight",
                            Members = { ["color"] = light.Color, ["lumens"] = Math.Clamp(light.Intensity * 1000f, 0f, 50000f) },
                        }));
                    break;
                }
                case "spot": {
                    Vector3 p = TransformPoint(light.From, light.LightToWorld, basis);
                    Vector3 d = TransformDir((light.To - light.From), light.LightToWorld, basis);
                    float outer = Math.Clamp(light.ConeAngleDegrees, 1f, 89f);
                    float inner = Math.Clamp(light.ConeAngleDegrees - light.ConeDeltaDegrees, 0f, outer);
                    doc.Entities.Add(LightEntity(NextName("Spot Light", data, "spot", ref spot),
                        p, LookRotation(SafeDir(d), Vector3.UnitY),
                        new ComponentDocument {
                            Type = "SpotLight",
                            Members = { ["color"] = light.Color, ["lumens"] = Math.Clamp(light.Intensity * 1000f, 0f, 50000f),
                                        ["outerAngle"] = outer, ["innerAngle"] = inner },
                        }));
                    break;
                }
            }
        }
    }

    static void AddDefaultLight(SceneDocument doc) {
        doc.Entities.Add(LightEntity("Directional Light", Vector3.Zero,
            LookRotation(new Vector3(-0.3f, -1f, -0.2f).Normalized(), Vector3.UnitY),
            new ComponentDocument { Type = "DirectionalLight" }));
    }

    static EntityDocument LightEntity(string name, Vector3 pos, Quaternion rot, ComponentDocument comp) =>
        new() {
            Id = NewId(), Name = name,
            Transform = new TransformDocument { Position = pos, Rotation = rot },
            Components = { comp },
        };

    static void AddSkybox(SceneDocument doc, PbrtSceneData data, Func<string, string> resolve) {
        if (string.IsNullOrEmpty(data.EnvMapPath)) return;
        var assetRef = resolve?.Invoke(data.EnvMapPath);
        if (assetRef == null) {
            Debugging.LogWarning($"pbrt: env map '{Path.GetFileName(data.EnvMapPath)}' not found in project; no skybox.");
            return;
        }
        doc.SceneComponents.Add(new ComponentDocument {
            Type = "Skybox",
            Members = { ["cubemap"] = assetRef },
        });
    }

    static bool HasLight(EntityDocument e) =>
        e.Components.Any(c => c.Type is "DirectionalLight" or "PointLight" or "SpotLight");

    // ---- transform helpers ----------------------------------------------------

    static Vector3 TransformPoint(Vector3 p, Matrix4 lightToWorld, Matrix4 basis) {
        Vector4 v = new Vector4(p, 1f) * (lightToWorld * basis);
        return v.Xyz;
    }

    static Vector3 TransformDir(Vector3 d, Matrix4 lightToWorld, Matrix4 basis) {
        Vector4 v = new Vector4(d, 0f) * (lightToWorld * basis);
        return v.Xyz;
    }

    static Vector3 SafeDir(Vector3 d) => d.LengthSquared > 1e-12f ? d.Normalized() : Vector3.UnitZ;

    static void Decompose(Matrix4 m, out Vector3 position, out Quaternion rotation, out Vector3 scale) {
        position = m.ExtractTranslation();
        scale = m.ExtractScale();
        rotation = m.ExtractRotation();
    }

    // Quaternion mapping engine forward (+Z) onto `forward`, with `up` as the reference up. Same
    // construction as BlendSceneConverter.LookRotation (OpenTK row-vector convention).
    static Quaternion LookRotation(Vector3 forward, Vector3 up) {
        if (forward.LengthSquared < 1e-12f) return Quaternion.Identity;
        forward = forward.Normalized();
        if (Math.Abs(Vector3.Dot(forward, up)) > 0.999f)
            up = Math.Abs(forward.Y) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 right = Vector3.Cross(up, forward).Normalized();
        Vector3 trueUp = Vector3.Cross(forward, right);
        var rot = new Matrix4(
            right.X, right.Y, right.Z, 0,
            trueUp.X, trueUp.Y, trueUp.Z, 0,
            forward.X, forward.Y, forward.Z, 0,
            0, 0, 0, 1);
        return rot.ExtractRotation();
    }

    static string NextName(string fallback, PbrtSceneData data, string type, ref int index) {
        int total = data.Lights.Count(l => l.Type == type);
        index++;
        return total > 1 ? $"{fallback} {index}" : fallback;
    }

    static string NewId() => Guid.NewGuid().ToString("N");
}
