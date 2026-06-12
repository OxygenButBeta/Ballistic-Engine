using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

// Imports audio source files (.wav for now; .ogg/.mp3 are recognized but need a Vorbis/MP3 decoder
// to be wired into AudioDecode) into a .baud artifact of interleaved 16-bit PCM. Same shape as
// TextureImporter: decode at import time, write the engine-native artifact, load reads it back fast.
public sealed class AudioImporter : IAssetImporter {
    static readonly string[] Extensions = [".wav", ".wave"];

    public string Name => "AudioImporter";
    public int Version => 1;
    public string ArtifactExtension => ".baud";

    public bool CanImport(string extension) => Extensions.Contains(extension);

    public static bool SupportsExtension(string extension) => Extensions.Contains(extension);

    public JsonObject CreateDefaultSettings(string assetPath) => new();

    public void Import(AssetImportContext context) {
        AudioData data = Decode(context.SourceAbsolutePath);
        if (!data.IsValid) {
            // Write an empty artifact so the asset still resolves (silent clip) instead of erroring
            // every load — mirrors the renderer substituting fallback textures.
            AudioArtifact.Write(context.ArtifactAbsolutePath, in data);
            return;
        }
        AudioArtifact.Write(context.ArtifactAbsolutePath, in data);
    }

    // Decodes a source audio file to canonical PCM by extension. Returns empty AudioData on an
    // unsupported/unparseable file (logged).
    public static AudioData Decode(string sourcePath) {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        return extension switch {
            ".wav" or ".wave" => WavDecoder.Decode(sourcePath),
            _ => Unsupported(sourcePath, extension),
        };
    }

    static AudioData Unsupported(string path, string extension) {
        Debugging.LogWarning(
            $"Audio import: '{extension}' not yet decodable ('{Path.GetFileName(path)}'); only .wav is supported so far.");
        return default;
    }
}
