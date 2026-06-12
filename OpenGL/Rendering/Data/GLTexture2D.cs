using OpenTK.Graphics.OpenGL;

namespace BallisticEngine;

public sealed class GLTexture2D : Texture2D {
    public override int UID { get; protected set; }
    bool isUploaded;

    public override void Activate() {
        GL.ActiveTexture(TextureUnit.Texture0 + (int)Type);
        GL.BindTexture(TextureTarget.Texture2D, UID);
    }

    public override void Deactivate() {
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    public override void Dispose() {
        if (isUploaded)
            GL.DeleteTexture(UID);
    }

    protected internal override void Upload(in TextureData data, TextureType type) {
        Type = type;

        UID = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture0 + (int)Type);
        GL.BindTexture(TextureTarget.Texture2D, UID);

        // All material maps repeat: tiled UVs are the norm, and clamping only the albedo while
        // the normal map repeated produced streaked walls on anything tiled.
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

        if (data.Format.IsBlockCompressed()) {
            UploadCompressed(in data);
        }
        else if (data.Format == TextureFormat.RGBA32F) {
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, data.Width, data.Height,
                0, PixelFormat.Rgba, PixelType.Float, data.Pixels);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        }
        else {
            // Color-data maps (albedo, emissive) are authored in sRGB and must be linearized by
            // the sampler; data maps (normal/metallic/roughness/AO) are linear already.
            PixelInternalFormat internalFormat = Type is TextureType.Diffuse or TextureType.Emissive
                ? PixelInternalFormat.SrgbAlpha
                : PixelInternalFormat.Rgba;

            if (Type is TextureType.Roughness or TextureType.AO) {
                internalFormat = PixelInternalFormat.R8;
            }
            else if (Type == TextureType.Metallic) {
                // Metallic maps are often ORM-packed (occlusion, roughness, metallic in RGB);
                // a single-channel format would silently destroy the G/B channels.
                internalFormat = PixelInternalFormat.Rgb8;
            }
            else if (Type == TextureType.Normal) {
                internalFormat = PixelInternalFormat.Rgb8;
            }

            GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, data.Width, data.Height,
                0, PixelFormat.Rgba, PixelType.UnsignedByte, data.Pixels);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        }

        isUploaded = true;

        GL.GetFloat((GetPName)All.MaxTextureMaxAnisotropyExt, out float maxAniso);
        GL.TexParameter(TextureTarget.Texture2D, (TextureParameterName)All.TextureMaxAnisotropyExt, maxAniso);

        Deactivate();
    }

    // Uploads a block-compressed mip chain straight to the GPU — no decode, no GenerateMipmap (you
    // can't gen mips on a compressed top level; the import baked the full chain). The internal format
    // mirrors the uncompressed path's sRGB choice: color maps (Diffuse/Emissive) decode through the
    // sRGB sampler, everything else is linear.
    void UploadCompressed(in TextureData data) {
        InternalFormat format = data.Format switch {
            TextureFormat.BC1 => Type is TextureType.Diffuse or TextureType.Emissive
                ? InternalFormat.CompressedSrgbAlphaS3tcDxt1Ext
                : InternalFormat.CompressedRgbaS3tcDxt1Ext,
            TextureFormat.BC3 => Type is TextureType.Diffuse or TextureType.Emissive
                ? InternalFormat.CompressedSrgbAlphaS3tcDxt5Ext
                : InternalFormat.CompressedRgbaS3tcDxt5Ext,
            TextureFormat.BC5 => InternalFormat.CompressedRgRgtc2, // normal maps: linear two-channel
            _ => InternalFormat.CompressedRgbaS3tcDxt5Ext,
        };

        // Tell GL exactly how many levels exist so sampling never reads an undefined mip.
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, data.MipCount - 1);

        for (int level = 0; level < data.MipCount; level++) {
            var (w, h) = TextureMipLayout.LevelSize(data.Width, data.Height, level);
            long offset = TextureMipLayout.LevelOffset(data.Width, data.Height, level, data.Format);
            int size = (int)TextureMipLayout.LevelBytes(data.Width, data.Height, level, data.Format);

            GL.CompressedTexImage2D(TextureTarget.Texture2D, level, format, w, h, 0,
                size, ref data.Pixels[offset]);
        }
    }
}
