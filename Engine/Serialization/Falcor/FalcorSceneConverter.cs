using BallisticEngine.AssetPipeline;
using BallisticEngine.Serialization;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Converts parsed Falcor scene data into a Ballistic .scene (YAML) file. Builds the SceneDocument
// directly — no live entities, no GL — so it can run during asset import. Model references are
// resolved against the project so the produced scene loads real geometry when the file exists.
public static class FalcorSceneConverter {
    // pysceneAbsolutePath: the source .pyscene; outputAbsolutePath: where to write the .scene.
    public static void Convert(string pysceneAbsolutePath, string outputAbsolutePath,
        Func<string, string> resolveModelAssetPath = null) {
        var source = File.ReadAllText(pysceneAbsolutePath);
        FalcorSceneData data = FalcorSceneParser.Parse(source);

        var doc = new SceneDocument { Name = Path.GetFileNameWithoutExtension(pysceneAbsolutePath) };

        AddCamera(doc, data);
        AddLights(doc, data);
        AddModels(doc, data, pysceneAbsolutePath, resolveModelAssetPath);
        AddSkybox(doc, data, pysceneAbsolutePath, resolveModelAssetPath);

        File.WriteAllText(outputAbsolutePath, SceneYaml.Serializer.Serialize(doc));
    }

    static void AddCamera(SceneDocument doc, FalcorSceneData data) {
        FalcorCamera cam = data.Camera ?? new FalcorCamera();

        // Orient the camera to look from Position toward Target.
        Vector3 forward = (cam.Target - cam.Position);
        Quaternion rotation = forward.LengthSquared > 1e-6f
            ? LookRotation(forward.Normalized())
            : Quaternion.Identity;

        doc.Entities.Add(new EntityDocument {
            Id = NewId(),
            Name = "Camera",
            Transform = new TransformDocument { Position = cam.Position, Rotation = rotation },
            Components = {
                new ComponentDocument { Type = "HDCamera" },
                new ComponentDocument { Type = "FreeLookCameraController" },
            },
        });
    }

    static void AddLights(SceneDocument doc, FalcorSceneData data) {
        var index = 0;
        foreach (FalcorLight light in data.Lights) {
            Quaternion rotation = light.Direction.LengthSquared > 1e-6f
                ? LookRotation(light.Direction.Normalized())
                : Quaternion.Identity;

            doc.Entities.Add(new EntityDocument {
                Id = NewId(),
                Name = data.Lights.Count > 1 ? $"Directional Light {++index}" : "Directional Light",
                Transform = new TransformDocument { Rotation = rotation },
                Components = {
                    new ComponentDocument {
                        Type = "DirectionalLight",
                        Members = {
                            ["lightIntensity"] = light.Intensity,
                        },
                    },
                },
            });
        }

        // Falcor scenes often rely on the env map for lighting; ensure at least one light exists.
        if (data.Lights.Count == 0) {
            doc.Entities.Add(new EntityDocument {
                Id = NewId(),
                Name = "Directional Light",
                Transform = new TransformDocument { Rotation = LookRotation(new Vector3(-0.3f, -1f, -0.2f).Normalized()) },
                Components = { new ComponentDocument { Type = "DirectionalLight" } },
            });
        }
    }

    static void AddModels(SceneDocument doc, FalcorSceneData data, string pysceneAbsolutePath,
        Func<string, string> resolveModelAssetPath) {
        var baseDir = Path.GetDirectoryName(pysceneAbsolutePath)!;

        foreach (var modelPath in data.ModelPaths) {
            var name = Path.GetFileNameWithoutExtension(modelPath);
            var entity = new EntityDocument {
                Id = NewId(),
                Name = name,
                Transform = new TransformDocument(),
                Components = new List<ComponentDocument>(),
            };

            var renderer = new ComponentDocument { Type = "StaticMeshRenderer" };

            // Resolve the model to a project asset reference if it exists; otherwise leave it empty.
            var assetRef = resolveModelAssetPath?.Invoke(Path.Combine(baseDir, modelPath));
            if (assetRef is not null)
                renderer.Members["sharedMesh"] = assetRef;
            else
                Debugging.LogWarning($"Falcor import: model '{modelPath}' not found in project; entity has no mesh.");

            entity.Components.Add(renderer);
            doc.Entities.Add(entity);
        }
    }

    // Falcor scenes use an equirect env map for sky + ambient; map it to a Skybox component
    // when the referenced image resolves to a project asset (.hdr/.exr/etc).
    static void AddSkybox(SceneDocument doc, FalcorSceneData data, string pysceneAbsolutePath,
        Func<string, string> resolveAssetPath) {
        if (string.IsNullOrEmpty(data.EnvMapPath))
            return;

        var baseDir = Path.GetDirectoryName(pysceneAbsolutePath)!;
        var assetRef = resolveAssetPath?.Invoke(Path.Combine(baseDir, data.EnvMapPath));
        if (assetRef is null) {
            Debugging.LogWarning($"Falcor import: env map '{data.EnvMapPath}' not found in project; no skybox.");
            return;
        }

        // Skybox is a SceneBehaviour (scene-wide component), not an entity component.
        doc.SceneComponents.Add(new ComponentDocument {
            Type = "Skybox",
            Members = { ["cubemap"] = assetRef },
        });
    }

    // Quaternion that rotates +Z (engine forward) to the given direction.
    static Quaternion LookRotation(Vector3 forward) {
        Vector3 up = Math.Abs(Vector3.Dot(forward, Vector3.UnitY)) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        Matrix4 look = Matrix4.LookAt(Vector3.Zero, forward, up);
        // LookAt builds a view matrix (world->view); invert to get the camera's world rotation.
        look.Invert();
        return look.ExtractRotation();
    }

    static string NewId() => Guid.NewGuid().ToString("N");
}
