using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace BallisticEngine;

public sealed class GLTexture3D : Texture3D {
    public override int UID { get; protected set; }
    bool isUploaded;

    static readonly TextureTarget[] CubemapFaces = [
        TextureTarget.TextureCubeMapPositiveX,
        TextureTarget.TextureCubeMapNegativeX,
        TextureTarget.TextureCubeMapPositiveY,
        TextureTarget.TextureCubeMapNegativeY,
        TextureTarget.TextureCubeMapPositiveZ,
        TextureTarget.TextureCubeMapNegativeZ
    ];

    public override void Activate() {
        GL.ActiveTexture(TextureUnit.Texture0 + (int)TextureType.SkyBox);
        GL.BindTexture(TextureTarget.TextureCubeMap, UID);
    }

    public override void Deactivate() {
        GL.BindTexture(TextureTarget.TextureCubeMap, 0);
    }

    public override void Dispose() {
        if (isUploaded)
            GL.DeleteTexture(UID);
    }

    protected internal override void UploadFaces(TextureData[] faces) {
        if (faces is not { Length: 6 })
            throw new ArgumentException("Cubemap requires exactly 6 faces in the order: +X, -X, +Y, -Y, +Z, -Z");

        Type = TextureType.SkyBox;

        UID = GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, UID);

        Vector3 ambientSum = Vector3.Zero;
        long totalPixels = 0;

        for (var i = 0; i < 6; i++) {
            TextureData face = faces[i];

            for (var y = 0; y < face.Height; y++) {
                for (var x = 0; x < face.Width; x++) {
                    var index = (y * face.Width + x) * 4; // RGBA
                    var r = face.Pixels[index + 0] / 255f;
                    var g = face.Pixels[index + 1] / 255f;
                    var b = face.Pixels[index + 2] / 255f;

                    ambientSum += new Vector3(r, g, b);
                    totalPixels++;
                }
            }

            GL.TexImage2D(CubemapFaces[i], 0, PixelInternalFormat.Srgb, face.Width, face.Height,
                0, PixelFormat.Rgba, PixelType.UnsignedByte, face.Pixels);
        }

        skyAmbient = ambientSum / totalPixels;

        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR,
            (int)TextureWrapMode.ClampToEdge);

        GL.GenerateMipmap(GenerateMipmapTarget.TextureCubeMap);

        isUploaded = true;

        GL.BindTexture(TextureTarget.TextureCubeMap, 0);
    }
}
