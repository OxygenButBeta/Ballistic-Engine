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
        var faceAvg = new Vector3[6]; // +X, -X, +Y, -Y, +Z, -Z
        long totalPixels = 0;

        for (var i = 0; i < 6; i++) {
            TextureData face = faces[i];
            var isFloat = face.Format == TextureFormat.RGBA32F;
            Vector3 faceSum = Vector3.Zero;
            long facePixels = 0;

            if (isFloat) {
                ReadOnlySpan<float> data = System.Runtime.InteropServices.MemoryMarshal
                    .Cast<byte, float>(face.Pixels);
                for (var p = 0; p < data.Length; p += 4) {
                    faceSum += new Vector3(data[p], data[p + 1], data[p + 2]);
                    facePixels++;
                }

                GL.TexImage2D(CubemapFaces[i], 0, PixelInternalFormat.Rgba16f, face.Width, face.Height,
                    0, PixelFormat.Rgba, PixelType.Float, face.Pixels);
            }
            else {
                for (var y = 0; y < face.Height; y++) {
                    for (var x = 0; x < face.Width; x++) {
                        var index = (y * face.Width + x) * 4; // RGBA
                        var r = face.Pixels[index + 0] / 255f;
                        var g = face.Pixels[index + 1] / 255f;
                        var b = face.Pixels[index + 2] / 255f;

                        // The GPU samples this face through an sRGB view (linearized); average
                        // in linear too or the CPU-side ambient is brighter than the sky.
                        faceSum += new Vector3(MathF.Pow(r, 2.2f), MathF.Pow(g, 2.2f), MathF.Pow(b, 2.2f));
                        facePixels++;
                    }
                }

                GL.TexImage2D(CubemapFaces[i], 0, PixelInternalFormat.Srgb, face.Width, face.Height,
                    0, PixelFormat.Rgba, PixelType.UnsignedByte, face.Pixels);
            }

            faceAvg[i] = facePixels > 0 ? faceSum / facePixels : Vector3.Zero;
            ambientSum += faceSum;
            totalPixels += facePixels;
        }

        skyAmbient = ambientSum / totalPixels;
        // Upper-hemisphere weighting (top face + the sky half of the sides): the hue fog
        // airlight veils toward - the full-sphere average browns it with the ground.
        skyAirlight = (faceAvg[2] + 0.5f * (faceAvg[0] + faceAvg[1] + faceAvg[4] + faceAvg[5])) / 3f;

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
