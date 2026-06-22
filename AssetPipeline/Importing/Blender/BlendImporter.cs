using System.Diagnostics;
using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

public sealed class BlendImporter : IAssetImporter {
    public static Action<string, string, string, string> Converter { get; set; }

    public string Name => "BlendImporter";
    public int Version => 1;
    public string ArtifactExtension => null;
    public bool RunsWithoutArtifact => true;
    public bool GeneratesSourceAssets => true;

    public bool CanImport(string extension) => extension == ".blend";

    public JsonObject CreateDefaultSettings(string assetPath) => new() {
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

            Converter(blendAbsolute, fbxAbsolute, tempJson, sceneAbsolute);
            Debugging.Log(
                $"Blend: imported '{context.AssetPath}' -> '{Path.GetFileName(fbxAbsolute)}' + '{Path.GetFileName(sceneAbsolute)}'.");
        }
        finally {
            TryDelete(tempScript);
            TryDelete(tempJson);
        }
    }

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
        info.ArgumentList.Add("--factory-startup");
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
        }
    }
}
