using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

public sealed class AudioImporter : IAssetImporter {
    static readonly string[] Extensions = [".wav", ".wave", ".ogg"];

    public string Name => "AudioImporter";
    public int Version => 2;
    public string ArtifactExtension => ".baud";

    public bool CanImport(string extension) => Extensions.Contains(extension);

    public static bool SupportsExtension(string extension) => Extensions.Contains(extension);

    public JsonObject CreateDefaultSettings(string assetPath) => new();

    public void Import(AssetImportContext context) {
        AudioData data = Decode(context.SourceAbsolutePath);
        if (!data.IsValid) {
            AudioArtifact.Write(context.ArtifactAbsolutePath, in data);
            return;
        }
        AudioArtifact.Write(context.ArtifactAbsolutePath, in data);
    }

    public static AudioData Decode(string sourcePath) {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        return extension switch {
            ".wav" or ".wave" => WavDecoder.Decode(sourcePath),
            ".ogg" => OggDecoder.Decode(sourcePath),
            _ => Unsupported(sourcePath, extension),
        };
    }

    static AudioData Unsupported(string path, string extension) {
        Debugging.LogWarning(
            $"Audio import: '{extension}' not decodable ('{Path.GetFileName(path)}'); supported: .wav, .ogg.");
        return default;
    }
}
