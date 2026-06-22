using BallisticEngine.AssetPipeline;
using BallisticEngine.Serialization;

namespace BallisticEngine;

public static class BlendSceneConverter {
    public static void Convert(string blendAbsolutePath, string fbxAbsolutePath, string jsonAbsolutePath,
        string outputAbsolutePath, Func<string, string> resolveAssetPath = null) {
        BlendSceneData data = BlendSceneParser.Parse(File.ReadAllText(jsonAbsolutePath));

        var doc = new SceneDocument { Name = Path.GetFileNameWithoutExtension(blendAbsolutePath) };

        AddCameras(doc, data);
        AddLights(doc, data);
        AddMesh(doc, data, fbxAbsolutePath, resolveAssetPath);

        File.WriteAllText(outputAbsolutePath, SceneYaml.Serializer.Serialize(doc));
    }

    static void AddCameras(SceneDocument doc, BlendSceneData data) {
        var index = 0;
        foreach (BlendCamera cam in data.Cameras) {
            DecomposeAim(cam.Matrix, out Vector3 position, out Quaternion rotation);

            doc.Entities.Add(new EntityDocument {
                Id = NewId(),
                Name = UniqueName(cam.Name, "Camera", data.Cameras.Count, ref index),
                Transform = new TransformDocument { Position = position, Rotation = rotation },
                Components = {
                    new ComponentDocument { Type = "HDCamera" },
                    new ComponentDocument { Type = "FreeLookCameraController" },
                },
            });
        }
    }

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
                    component = new ComponentDocument {
                        Type = "DirectionalLight",
                        Members = {
                            ["illuminance"] = Math.Clamp(light.Energy * 100000f, 0f, 150000f),
                        },
                    };
                    name = UniqueName(light.Name, "Directional Light", CountOf(data, "SUN"), ref dirIndex);
                    break;

                case "SPOT": {
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

    static void DecomposeAim(float[] m, out Vector3 position, out Quaternion rotation) {
        Vector3 up = ToYUp(Column(m, 1));
        Vector3 backward = ToYUp(Column(m, 2));
        Vector3 translation = ToYUp(Column(m, 3));

        position = translation;

        Vector3 forward = -NormalizeOr(backward, Vector3.UnitZ);
        Vector3 upN = NormalizeOr(up, Vector3.UnitY);
        rotation = LookRotation(forward, upN);
    }

    static Vector3 ToYUp(Vector3 v) => new(v.X, v.Z, -v.Y);

    static Vector3 Column(float[] m, int c) => new(m[c], m[4 + c], m[8 + c]);

    static Quaternion LookRotation(Vector3 forward, Vector3 up) {
        if (forward.LengthSquared() < 1e-12f)
            return Quaternion.Identity;

        forward = forward.Normalized();
        if (Math.Abs(Vector3.Dot(forward, up)) > 0.999f)
            up = Math.Abs(forward.Y) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;

        Vector3 right = Vector3.Cross(up, forward).Normalized();
        Vector3 trueUp = Vector3.Cross(forward, right);

        var rotationMatrix = new Matrix4(
            right.X, right.Y, right.Z, 0,
            trueUp.X, trueUp.Y, trueUp.Z, 0,
            forward.X, forward.Y, forward.Z, 0,
            0, 0, 0, 1);
        return rotationMatrix.ExtractRotation();
    }

    static Vector3 NormalizeOr(Vector3 v, Vector3 fallback) =>
        v.LengthSquared() > 1e-12f ? v.Normalized() : fallback;

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
