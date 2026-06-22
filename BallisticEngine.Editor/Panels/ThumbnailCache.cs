using System.Runtime.InteropServices;
using BallisticEngine.AssetPipeline;

namespace BallisticEngine.Editor;

internal sealed class ThumbnailCache {
    const int Size = 64;
    const uint Magic = 0x31485442;

    static readonly string[] MeshExtensions = [".fbx", ".obj", ".gltf", ".glb", ".dae"];

    readonly Dictionary<Guid, nint> ready = new();
    readonly Dictionary<Guid, Dx12EditorPreview.Dx12EditorTexture> dx12Textures = new();
    readonly Queue<(Guid guid, string assetPath)> pending = new();
    readonly HashSet<Guid> queued = new();

    static bool IsDx12 => RenderBackendSelector.Selected == RenderBackend.Dx12;

    public nint Get(Guid guid, string assetPath) {
        if (IsDx12)
            return 0;

        if (ready.TryGetValue(guid, out var texture))
            return texture;

        if (queued.Add(guid))
            pending.Enqueue((guid, assetPath));
        return 0;
    }

    public void Pump() {
        if (pending.Count == 0)
            return;

        (Guid guid, string assetPath) = pending.Dequeue();
        try {
            ready[guid] = Load(guid, assetPath);
        }
        catch (Exception exception) {
            ready[guid] = 0;
            Debugging.LogWarning($"Thumbnail failed for '{assetPath}': {exception.Message}");
        }
    }

    public void InvalidateAll() {
        foreach (var tex in dx12Textures.Values)
            tex.Dispose();
        dx12Textures.Clear();
        ready.Clear();
        queued.Clear();
        pending.Clear();
    }

    static string ThumbnailDirectory => Path.Combine(AssetDatabase.Project.LibraryPath, "Thumbnails");

    nint UploadHandle(Guid guid, byte[] pixels) {
        var tex = Dx12EditorPreview.UploadTexture(pixels, Size);
        dx12Textures[guid] = tex;
        return tex.Handle;
    }

    nint Load(Guid guid, string assetPath) {
        bool isMaterial = Path.GetExtension(assetPath).Equals(".mat", StringComparison.OrdinalIgnoreCase);
        string artifactPath;
        if (isMaterial) {
            artifactPath = AssetDatabase.Project.ResolveAbsolute(assetPath);
        }
        else if (!AssetDatabase.TryGetArtifactPath(guid, out artifactPath)) {
            return 0;
        }

        var thumbPath = Path.Combine(ThumbnailDirectory, $"{guid:N}.thumb");

        byte[] pixels = null;
        if (File.Exists(thumbPath) &&
            File.GetLastWriteTimeUtc(thumbPath) >= File.GetLastWriteTimeUtc(artifactPath))
            pixels = ReadThumbFile(thumbPath);

        if (pixels is null) {
            pixels = Generate(assetPath, artifactPath);
            if (pixels is null)
                return 0;
            WriteThumbFile(thumbPath, pixels);
        }

        return UploadHandle(guid, pixels);
    }

    static byte[] Generate(string assetPath, string artifactPath) {
        var extension = Path.GetExtension(assetPath).ToLowerInvariant();

        if (MeshExtensions.Contains(extension)) {
            MeshData mesh = MeshArtifact.Read(artifactPath);
            return mesh.IsValid ? MeshPreviewRenderer.Render(in mesh, Size) : null;
        }

        if (extension == ".mat") {
            var definition = AssetPipeline.PipelineJson.Read<AssetPipeline.Loaders.MaterialDefinition>(artifactPath);
            return MaterialPreviewRenderer.Render(definition, Size);
        }

        TextureData data = TextureArtifact.Read(artifactPath);
        return Downscale(in data);
    }

    static byte[] ReadThumbFile(string path) {
        try {
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new(stream);
            if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != Size)
                return null;
            var pixels = new byte[Size * Size * 4];
            stream.ReadExactly(pixels);
            return pixels;
        }
        catch {
            return null;
        }
    }

    static void WriteThumbFile(string path, byte[] pixels) {
        Directory.CreateDirectory(ThumbnailDirectory);
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write(Magic);
        writer.Write((ushort)Size);
        writer.Write(pixels);
    }

    static byte[] Downscale(in TextureData data) {
        var output = new byte[Size * Size * 4];
        var isFloat = data.Format == TextureFormat.RGBA32F;
        ReadOnlySpan<float> floats = isFloat ? MemoryMarshal.Cast<byte, float>(data.Pixels) : default;

        for (var y = 0; y < Size; y++) {
            var srcY = Math.Min(data.Height - 1, y * data.Height / Size);
            for (var x = 0; x < Size; x++) {
                var srcX = Math.Min(data.Width - 1, x * data.Width / Size);
                var src = (srcY * data.Width + srcX) * 4;
                var dst = (y * Size + x) * 4;

                if (isFloat) {
                    for (var c = 0; c < 3; c++) {
                        var v = floats[src + c];
                        v = v / (1f + v);
                        output[dst + c] = (byte)Math.Clamp(MathF.Pow(v, 1f / 2.2f) * 255f, 0f, 255f);
                    }
                    output[dst + 3] = 255;
                }
                else {
                    output[dst] = data.Pixels[src];
                    output[dst + 1] = data.Pixels[src + 1];
                    output[dst + 2] = data.Pixels[src + 2];
                    output[dst + 3] = 255;
                }
            }
        }

        return output;
    }
}
