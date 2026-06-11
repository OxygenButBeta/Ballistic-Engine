using System.Diagnostics;
using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

// Imports Blender .blend files. Assimp can't extract cameras or lights from .blend (and its mesh
// reader breaks on modern Blender versions), so this drives Blender's OWN Python headlessly to:
//   1. export the meshes to a sibling "<name>.fbx" — picked up by ModelImporter on the next pass,
//      which generates materials and the node tree exactly as for any other model; and
//   2. write a JSON sidecar of cameras + lights, which the injected Converter turns into a sibling
//      "<name>.scene" with HDCamera / DirectionalLight / PointLight / SpotLight entities plus a
//      StaticMeshRenderer pointing at the .fbx.
//
// FBX (not glTF) is the mesh carrier because AssimpNet 4.1.0's bundled native Assimp parses
// Blender's modern .glb as zero meshes but reads Blender FBX cleanly.
//
// Like the Falcor importer, this produces project ASSETS (.fbx + .scene), not a Library artifact,
// so the refresh sweeps again to register them. If Blender isn't installed it logs a one-line hint
// (set BLENDER_PATH) and skips — the .blend stays inert rather than failing the whole refresh.
//
// The actual JSON -> SceneDocument conversion lives in the Engine layer (it builds scene documents),
// so it's injected via Converter — set once at startup by EngineBootstrap, same pattern as Falcor.
public sealed class BlendImporter : IAssetImporter {
    // (blendAbsolutePath, fbxAbsolutePath, jsonAbsolutePath, outputSceneAbsolutePath) -> writes .scene.
    public static Action<string, string, string, string> Converter { get; set; }

    public string Name => "BlendImporter";
    public int Version => 1;
    public string ArtifactExtension => null;          // produces project assets, not a Library artifact
    public bool RunsWithoutArtifact => true;
    public bool GeneratesSourceAssets => true;        // writes a sibling .glb and .scene

    public bool CanImport(string extension) => extension == ".blend";

    public JsonObject CreateDefaultSettings(string assetPath) => new() {
        // Reimport whenever the importer version bumps; nothing user-tunable yet. Mesh import of
        // the generated .glb is governed by that .glb's own ModelImporter meta (splitByNodes, etc.).
        ["importScene"] = true,
    };

    public void Import(AssetImportContext context) {
        var blender = BlenderLocator.Find();
        if (blender is null) {
            Debugging.LogWarning(
                $"Blend import: Blender not found, skipping '{context.AssetPath}'. Install Blender or set " +
                "the BLENDER_PATH environment variable to blender.exe.");
            return;
        }

        var blendAbsolute = context.SourceAbsolutePath;
        var fbxAbsolute = Path.ChangeExtension(blendAbsolute, ".fbx");
        var sceneAbsolute = Path.ChangeExtension(blendAbsolute, ".scene");

        // Per-import temp files (script + JSON sidecar) keyed by the asset GUID so parallel imports
        // of different .blend files never collide on the same temp path.
        var tempScript = Path.Combine(Path.GetTempPath(), $"ballistic_blend_export_{context.Guid:N}.py");
        var tempJson = Path.Combine(Path.GetTempPath(), $"ballistic_blend_scene_{context.Guid:N}.json");

        try {
            File.WriteAllText(tempScript, BlendExportScript.Source);

            if (!RunBlender(blender, blendAbsolute, tempScript, fbxAbsolute, tempJson, context.AssetPath))
                return;

            if (!File.Exists(tempJson)) {
                Debugging.LogWarning($"Blend import: '{context.AssetPath}' produced no scene data; skipped.");
                return;
            }

            if (Converter is null) {
                Debugging.LogWarning($"Blend importer not wired; '{context.AssetPath}' mesh exported but no .scene written.");
                return;
            }

            // Converter reads the JSON + decides whether the .fbx exists; writes the .scene.
            Converter(blendAbsolute, fbxAbsolute, tempJson, sceneAbsolute);
            Debugging.Log(
                $"Blend: imported '{context.AssetPath}' -> '{Path.GetFileName(fbxAbsolute)}' + '{Path.GetFileName(sceneAbsolute)}'.");
        }
        finally {
            TryDelete(tempScript);
            TryDelete(tempJson);
        }
    }

    // Runs `blender --background <file>.blend --python <script> -- <out.fbx> <out.json>` and returns
    // whether it completed without a reported error. Blender is chatty on stdout; we only fail on a
    // non-zero exit or our script's BLEND_EXPORT_ERROR marker.
    static bool RunBlender(string blender, string blendAbsolute, string scriptPath,
        string fbxAbsolute, string jsonAbsolute, string assetPath) {
        var info = new ProcessStartInfo {
            FileName = blender,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("--background");
        info.ArgumentList.Add(blendAbsolute);
        info.ArgumentList.Add("--factory-startup");   // ignore user add-ons/prefs for a deterministic export
        info.ArgumentList.Add("--python");
        info.ArgumentList.Add(scriptPath);
        info.ArgumentList.Add("--");
        info.ArgumentList.Add(fbxAbsolute);
        info.ArgumentList.Add(jsonAbsolute);

        try {
            using var process = Process.Start(info);
            if (process is null) {
                Debugging.LogWarning($"Blend import: failed to launch Blender for '{assetPath}'.");
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 || stdout.Contains("BLEND_EXPORT_ERROR")) {
                var detail = ExtractError(stdout, stderr);
                Debugging.LogError($"Blend import: Blender failed for '{assetPath}' (exit {process.ExitCode}). {detail}");
                return false;
            }

            return true;
        }
        catch (Exception exception) {
            Debugging.LogError($"Blend import: error running Blender for '{assetPath}': {exception.Message}");
            return false;
        }
    }

    static string ExtractError(string stdout, string stderr) {
        foreach (var line in stdout.Split('\n'))
            if (line.Contains("BLEND_EXPORT_ERROR"))
                return line.Trim();
        var err = stderr.Trim();
        return err.Length > 0 ? err[..Math.Min(err.Length, 300)] : "(no diagnostic output)";
    }

    static void TryDelete(string path) {
        try {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch {
            // Temp cleanup is best-effort.
        }
    }
}
