using System.Runtime.InteropServices;
using BallisticEngine.AssetPipeline;
using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine.Editor;

// Lazy thumbnail provider for the asset browser: images downscale their Library artifact,
// meshes render a small preview (MeshPreviewRenderer). At most one thumbnail is generated per
// frame so the UI never hitches, and results persist in Library/Thumbnails/<guid>.thumb so
// they are NOT regenerated every session — only when the source artifact is newer.
internal sealed class ThumbnailCache {
    const int Size = 64;
    const uint Magic = 0x31485442; // "BTH1"

    static readonly string[] MeshExtensions = [".fbx", ".obj", ".gltf", ".glb", ".dae"];

    // The ImGui texture handle: a GL texture name (GL backend) or a DX12 UiHeap GPU descriptor ptr (DX12).
    readonly Dictionary<Guid, nint> ready = new();
    // DX12-only: the backing texture per thumbnail (resource + UiHeap slot) so InvalidateAll can free both.
    readonly Dictionary<Guid, Dx12EditorPreview.Dx12EditorTexture> dx12Textures = new();
    readonly Queue<(Guid guid, string assetPath)> pending = new();
    readonly HashSet<Guid> queued = new();

    static bool IsDx12 => RenderBackendSelector.Selected == RenderBackend.Dx12;

    // Returns the ImGui texture handle, or 0 while the thumbnail is still loading (or failed).
    public nint Get(Guid guid, string assetPath) {
        // DX12: the thumbnail/material-preview GPU path (Dx12EditorPreview) hangs the GPU (DXGI_ERROR_DEVICE_HUNG)
        // under load — DISABLED until root-caused (icon-tile fallback, the committed-safe behavior). The preview/
        // upload code below stays in the tree for the fix. Re-enable by removing this guard once verified safe.
        if (IsDx12)
            return 0;

        if (ready.TryGetValue(guid, out var texture))
            return texture;

        if (queued.Add(guid))
            pending.Enqueue((guid, assetPath));
        return 0;
    }

    // Call once per frame.
    public void Pump() {
        if (pending.Count == 0)
            return;

        (Guid guid, string assetPath) = pending.Dequeue();
        try {
            ready[guid] = Load(guid, assetPath);
        }
        catch (Exception exception) {
            ready[guid] = 0; // never retried this session; tile falls back to the colored box
            Debugging.LogWarning($"Thumbnail failed for '{assetPath}': {exception.Message}");
        }
    }

    // Drops the GPU textures and re-queues; the DISK cache stays (staleness is mtime-based,
    // so reimported assets regenerate and unchanged ones reload instantly).
    public void InvalidateAll() {
        if (IsDx12) {
            foreach (var tex in dx12Textures.Values)
                tex.Dispose();
            dx12Textures.Clear();
        }
        else {
            foreach (var texture in ready.Values.Where(t => t != 0))
                GL.DeleteTexture((int)texture);
        }
        ready.Clear();
        queued.Clear();
        pending.Clear();
    }

    static string ThumbnailDirectory => Path.Combine(AssetDatabase.Project.LibraryPath, "Thumbnails");

    // Upload the generated RGBA pixels to a GPU texture and return its ImGui handle. DX12 creates a UiHeap
    // texture (tracked for disposal); GL creates a GL texture (its name is the handle).
    nint UploadHandle(Guid guid, byte[] pixels) {
        if (IsDx12) {
            var tex = Dx12EditorPreview.UploadTexture(pixels, Size);
            dx12Textures[guid] = tex;
            return tex.Handle;
        }
        return UploadTexture(pixels);
    }

    nint Load(Guid guid, string assetPath) {
        // Materials have no Library artifact (.mat is a text asset) — preview straight from the asset
        // file. Other types render from their imported artifact.
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

        // Material: render the preview sphere from the .mat (artifactPath is the asset file itself).
        if (extension == ".mat") {
            var definition = AssetPipeline.PipelineJson.Read<AssetPipeline.Loaders.MaterialDefinition>(artifactPath);
            return MaterialPreviewRenderer.Render(definition, Size);
        }

        TextureData data = TextureArtifact.Read(artifactPath);
        return Downscale(in data);
    }

    // ---- Disk format: magic | u16 size | raw RGBA --------------------------------

    static byte[] ReadThumbFile(string path) {
        try {
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new(stream);
            if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != Size)
                return null; // format changed: regenerate
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

    static int UploadTexture(byte[] pixels) {
        int texture = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, Size, Size, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
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
                        v = v / (1f + v); // Reinhard for HDR preview
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
