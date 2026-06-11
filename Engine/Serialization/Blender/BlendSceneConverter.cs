using BallisticEngine.AssetPipeline;
using BallisticEngine.Serialization;
using OpenTK.Mathematics;

// Converts the JSON sidecar the Blender export script produces (cameras + lights, plus a flag for
// the sibling .fbx mesh) into a Ballistic .scene (YAML). Builds the SceneDocument directly — no live
// entities, no GL — so it runs during asset import. Mirrors FalcorSceneConverter; injected into
// BlendImporter.Converter by EngineBootstrap so the AssetPipeline layer stays free of Engine types.
//
// COORDINATE SYSTEMS. Blender is Z-up, right-handed; the engine is Y-up, right-handed. The .fbx is
// exported in Blender's native Z-up, so Assimp's FBX importer converts the mesh to Y-up. We apply
// the SAME basis change to the camera/light world matrices here so they line up with that geometry:
//   (x, y, z)_blender -> (x, z, -y)_engine.
// Camera and light objects in Blender aim down their local -Z (with local +Y up); the engine's
// forward is +Z (Transform.Forward = Rotation * UnitZ), so LookRotation orients each so its +Z
// points where the Blender object aimed.

namespace BallisticEngine;

public static class BlendSceneConverter {
    // jsonAbsolutePath: the export sidecar; fbxAbsolutePath: the exported mesh (may not exist if the
    // .blend had no meshes); outputAbsolutePath: where to write the .scene. resolveAssetPath maps an
    // absolute project file to an "Assets/..." ref (the .fbx mesh).
    public static void Convert(string blendAbsolutePath, string fbxAbsolutePath, string jsonAbsolutePath,
        string outputAbsolutePath, Func<string, string> resolveAssetPath = null) {
        BlendSceneData data = BlendSceneParser.Parse(File.ReadAllText(jsonAbsolutePath));

        var doc = new SceneDocument { Name = Path.GetFileNameWithoutExtension(blendAbsolutePath) };

        AddCameras(doc, data);
        AddLights(doc, data);
        AddMesh(doc, data, fbxAbsolutePath, resolveAssetPath);

        File.WriteAllText(outputAbsolutePath, SceneYaml.Serializer.Serialize(doc));
    }

    // ---- Cameras -------------------------------------------------------------

    static void AddCameras(SceneDocument doc, BlendSceneData data) {
        var index = 0;
        foreach (BlendCamera cam in data.Cameras) {
            DecomposeAim(cam.Matrix, out Vector3 position, out Quaternion rotation);

            doc.Entities.Add(new EntityDocument {
                Id = NewId(),
                Name = UniqueName(cam.Name, "Camera", data.Cameras.Count, ref index),
                Transform = new TransformDocument { Position = position, Rotation = rotation },
                Components = {
                    // HDCamera derives its projection from the viewport; FOV/clip are Blender-side
                    // info we don't currently push (no public members), so the camera lands posed
                    // correctly and the user tweaks lens settings in the inspector.
                    new ComponentDocument { Type = "HDCamera" },
                    new ComponentDocument { Type = "FreeLookCameraController" },
                },
            });
        }
    }

    // ---- Lights --------------------------------------------------------------

    static void AddLights(SceneDocument doc, BlendSceneData data) {
        var dirIndex = 0;
        var pointIndex = 0;
        var spotIndex = 0;

        foreach (BlendLight light in data.Lights) {
            DecomposeAim(light.Matrix, out Vector3 position, out Quaternion rotation);
            Vector3 color = new(light.Color[0], light.Color[1], light.Color[2]);

            ComponentDocument component;
            string name;

            switch (light.LightType) {
                case "SUN":
                    // Blender sun energy is irradiance in W/m^2 (default 1.0 ~ bright). Map to the
                    // engine's lux scale: a clear midday sun is ~80-120k lux, ~1 kW/m^2, so ~1e5 lux
                    // per W/m^2 lands a default sun in a believable range.
                    component = new ComponentDocument {
                        Type = "DirectionalLight",
                        Members = {
                            ["illuminance"] = Math.Clamp(light.Energy * 100000f, 0f, 150000f),
                        },
                    };
                    name = UniqueName(light.Name, "Directional Light", CountOf(data, "SUN"), ref dirIndex);
                    break;

                case "SPOT": {
                    // Blender spot_size is the FULL cone angle (radians); the engine wants half-angles
                    // in degrees. spot_blend (0..1) sets how far inside the outer angle full
                    // brightness reaches.
                    float outerDeg = MathHelper.RadiansToDegrees(light.SpotSize) * 0.5f;
                    float innerDeg = outerDeg * (1f - Math.Clamp(light.SpotBlend, 0f, 1f));
                    component = new ComponentDocument {
                        Type = "SpotLight",
                        Members = {
                            ["color"] = color,
                            ["lumens"] = WattsToLumens(light.Energy),
                            ["outerAngle"] = Math.Clamp(outerDeg, 0f, 90f),
                            ["innerAngle"] = Math.Clamp(innerDeg, 0f, 90f),
                        },
                    };
                    if (light.Range > 0f)
                        component.Members["range"] = light.Range;
                    name = UniqueName(light.Name, "Spot Light", CountOf(data, "SPOT"), ref spotIndex);
                    break;
                }

                default: {
                    // POINT (and AREA, approximated as a point — the engine has no area light).
                    component = new ComponentDocument {
                        Type = "PointLight",
                        Members = {
                            ["color"] = color,
                            ["lumens"] = WattsToLumens(light.Energy),
                        },
                    };
                    if (light.Range > 0f)
                        component.Members["range"] = light.Range;
                    name = UniqueName(light.Name, "Point Light", CountOf(data, "POINT"), ref pointIndex);
                    break;
                }
            }

            doc.Entities.Add(new EntityDocument {
                Id = NewId(),
                Name = name,
                Transform = new TransformDocument { Position = position, Rotation = rotation },
                Components = { component },
            });
        }
    }

    // ---- Mesh ----------------------------------------------------------------

    // References the exported .fbx as a single mesh entity (whole mesh, all submeshes) — the model
    // importer's splitByNodes + node tree carry the per-object structure inside the .fbx, so the
    // user can later instantiate the model for a full entity tree. Mirrors FalcorSceneConverter.
    static void AddMesh(SceneDocument doc, BlendSceneData data, string fbxAbsolutePath,
        Func<string, string> resolveAssetPath) {
        if (!data.HasMesh || !File.Exists(fbxAbsolutePath))
            return;

        var assetRef = resolveAssetPath?.Invoke(fbxAbsolutePath);
        if (assetRef is null) {
            Debugging.LogWarning(
                $"Blend import: exported mesh '{Path.GetFileName(fbxAbsolutePath)}' is outside the project; no mesh entity.");
            return;
        }

        doc.Entities.Add(new EntityDocument {
            Id = NewId(),
            Name = Path.GetFileNameWithoutExtension(fbxAbsolutePath),
            Transform = new TransformDocument(),
            Components = {
                new ComponentDocument {
                    Type = "StaticMeshRenderer",
                    Members = { ["sharedMesh"] = assetRef },
                },
            },
        });
    }

    // ---- Transform conversion ------------------------------------------------

    // Reads a Blender world matrix (row-major 16 floats m[row*4+col], Z-up) and returns the
    // engine-space position plus an orientation whose +Z (engine forward) points where the Blender
    // object aimed (its local -Z, with local +Y up — the camera/sun/spot convention).
    static void DecomposeAim(float[] m, out Vector3 position, out Quaternion rotation) {
        // The basis axes are the matrix COLUMNS (col c = m[c], m[4+c], m[8+c]); translation is the
        // last column. Apply the Z-up -> Y-up basis change (matches the FBX's Y-up export, so the
        // camera/lights line up with the exported mesh): (x, y, z)_blender -> (x, z, -y)_engine.
        Vector3 up = ToYUp(Column(m, 1));
        Vector3 backward = ToYUp(Column(m, 2)); // Blender local +Z; the object aims down -Z
        Vector3 translation = ToYUp(Column(m, 3));

        position = translation;

        // Engine forward (+Z) must align with the object's aim (-Z in Blender = -backward here).
        Vector3 forward = -NormalizeOr(backward, Vector3.UnitZ);
        Vector3 upN = NormalizeOr(up, Vector3.UnitY);
        rotation = LookRotation(forward, upN);
    }

    // (x, y, z)_blender -> (x, z, -y)_engine.
    static Vector3 ToYUp(Vector3 v) => new(v.X, v.Z, -v.Y);

    // Column c of the row-major world matrix (axes for c<3, translation for c==3).
    static Vector3 Column(float[] m, int c) => new(m[c], m[4 + c], m[8 + c]);

    // Quaternion mapping the engine basis onto (right, up, forward): rot*UnitZ == forward,
    // rot*UnitY == up. Built directly from an orthonormalized basis rather than via Matrix4.LookAt
    // (which uses OpenGL's look-down-(-Z) view convention and would invert the engine's +Z forward).
    static Quaternion LookRotation(Vector3 forward, Vector3 up) {
        if (forward.LengthSquared < 1e-12f)
            return Quaternion.Identity;

        forward = forward.Normalized();
        // Degenerate up (parallel to forward): pick any non-parallel reference.
        if (Math.Abs(Vector3.Dot(forward, up)) > 0.999f)
            up = Math.Abs(forward.Y) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;

        // Right-handed orthonormal basis. Right = up x forward, then re-derive up from forward x right
        // so the three axes are exactly orthogonal even if the input up wasn't.
        Vector3 right = Vector3.Cross(up, forward).Normalized();
        Vector3 trueUp = Vector3.Cross(forward, right);

        // Row-vector (OpenTK) convention: a rotation that maps UnitX->right, UnitY->trueUp,
        // UnitZ->forward has those vectors as its ROWS (v * M reads rows).
        var rotationMatrix = new Matrix4(
            right.X, right.Y, right.Z, 0,
            trueUp.X, trueUp.Y, trueUp.Z, 0,
            forward.X, forward.Y, forward.Z, 0,
            0, 0, 0, 1);
        return rotationMatrix.ExtractRotation();
    }

    static Vector3 NormalizeOr(Vector3 v, Vector3 fallback) =>
        v.LengthSquared > 1e-12f ? v.Normalized() : fallback;

    // ---- Light unit mapping --------------------------------------------------

    // Blender point/spot energy is radiant power in watts (default 1000 W ~ a strong bulb). The
    // engine's lights take luminous power in lumens. A rough luminous efficacy of ~120 lm/W (warm
    // white LED-ish) keeps a default 1000 W Blender lamp around a believable 1500 lm equivalent
    // after the engine's own physical scaling.
    static float WattsToLumens(float watts) => Math.Clamp(watts * 1.5f, 0f, 20000f);

    static int CountOf(BlendSceneData data, string type) => data.Lights.Count(l => l.LightType == type);

    static string UniqueName(string blenderName, string fallback, int total, ref int index) {
        index++;
        if (!string.IsNullOrWhiteSpace(blenderName))
            return blenderName;
        return total > 1 ? $"{fallback} {index}" : fallback;
    }

    static string NewId() => Guid.NewGuid().ToString("N");
}
