using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Texture3D wrapper around a renderer-owned GL cubemap (the procedural sky bake target), so
// runtime-generated skies plug into every path an asset cubemap uses (skybox draw, IBL bake,
// sky ambient). ContentVersion bumps on every re-bake so the IBL knows to re-convolve even
// though the wrapper instance stays the same.
public sealed class GLRuntimeCubemap : Texture3D {
    public override int UID { get; protected set; }
    public int ContentVersion { get; private set; }

    public void Adopt(int cubemapTextureId, Vector3 ambient) {
        UID = cubemapTextureId;
        skyAmbient = ambient;
        Type = TextureType.SkyBox;
        ContentVersion++;
    }

    public override void Activate() {
        GL.ActiveTexture(TextureUnit.Texture0 + (int)TextureType.SkyBox);
        GL.BindTexture(TextureTarget.TextureCubeMap, UID);
    }

    public override void Deactivate() {
        GL.BindTexture(TextureTarget.TextureCubeMap, 0);
    }

    // The bake pass owns the GL texture; nothing to release here.
    public override void Dispose() {
    }

    protected internal override void UploadFaces(TextureData[] faces) =>
        throw new NotSupportedException("Runtime cubemaps are rendered by the sky pass, not uploaded.");
}
