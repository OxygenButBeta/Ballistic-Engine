using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL.GpuDriven;

// Builds the bindless material table (an array of GpuMaterial) the fragment shader indexes by the
// per-draw materialId. Each material's six texture maps become resident bindless handles
// (GL_ARB_bindless_texture). Missing maps fall back to DefaultTextures.Neutral — exactly mirroring
// Material.Activate, so shading is unchanged.
//
// Handles are cached by texture UID (each GL texture yields ONE handle for its lifetime) and made
// resident once. Materials are indexed by reference in a stable order; IndexOf maps a Material to
// its row. Build returns true only when the set changed (so the SSBO re-upload is skipped on the
// common static frame).
public sealed class GpuMaterialTable : IDisposable {
    readonly Dictionary<int, ulong> handleByTexture = new();   // texture UID -> resident bindless handle
    readonly Dictionary<Material, int> indexByMaterial = new();
    readonly HashSet<ulong> resident = new();
    int lastCount = -1;

    public int IndexOf(Material m) =>
        m is not null && indexByMaterial.TryGetValue(m, out int i) ? i : 0;

    // Global debug multipliers the CPU SetMaterialUniforms folds in (metallicMul = factor*global,
    // roughnessMul = factor*global, normalStrength = matStrength*global). Stored so a change to a
    // global (e.g. the editor's debug sliders) forces a table rebuild for byte-identical shading.
    float gMetallic = float.NaN, gRoughness = float.NaN, gNormal = float.NaN;

    // Returns true (and fills `table`) when the material set OR the global multipliers changed.
    public bool Build(IReadOnlyList<Material> materials, float globalMetallic, float globalRoughness,
        float globalNormalStrength, out GpuMaterial[] table) {
        bool globalsChanged = gMetallic != globalMetallic || gRoughness != globalRoughness ||
                              gNormal != globalNormalStrength;
        gMetallic = globalMetallic;
        gRoughness = globalRoughness;
        gNormal = globalNormalStrength;
        return BuildInternal(materials, globalsChanged, out table);
    }

    bool BuildInternal(IReadOnlyList<Material> materials, bool forceRebuild, out GpuMaterial[] table) {
        // Cheap change check: same count AND every material already indexed in the same slot.
        bool unchanged = !forceRebuild && materials.Count == lastCount;
        if (unchanged) {
            for (var i = 0; i < materials.Count; i++) {
                if (!indexByMaterial.TryGetValue(materials[i], out int idx) || idx != i) {
                    unchanged = false;
                    break;
                }
            }
        }
        if (unchanged) {
            table = null;
            return false;
        }

        // The material set changed (often a hot-reload: old textures freed, new ones created).
        // Release every resident handle and clear the UID cache FIRST, so we never hand the shader
        // a bindless handle pointing at a deleted texture (a freed UID can be reused by a new
        // texture -> stale handle -> GPU garbage/crash). Re-acquire fresh below.
        foreach (ulong h in resident)
            GL.Arb.MakeTextureHandleNonResident((long)h);
        resident.Clear();
        handleByTexture.Clear();

        indexByMaterial.Clear();
        table = new GpuMaterial[materials.Count];
        for (var i = 0; i < materials.Count; i++) {
            Material m = materials[i];
            indexByMaterial[m] = i;
            table[i] = Pack(m);
        }
        lastCount = materials.Count;
        return true;
    }

    GpuMaterial Pack(Material m) {
        var flags = (uint)0;
        if (m.PackedOrm) flags |= (uint)GpuMaterialFlags.PackedOrm;
        if (m.Metallic is not null) flags |= (uint)GpuMaterialFlags.HasMetallic;
        if (m.Roughness is not null) flags |= (uint)GpuMaterialFlags.HasRoughness;
        if (m.NormalFlipY) flags |= (uint)GpuMaterialFlags.NormalFlipY;
        if (m.Emissive is not null) flags |= (uint)GpuMaterialFlags.HasEmissive;
        if (m.Transparent) flags |= (uint)GpuMaterialFlags.AlphaBlend;
        if (m.Cutout) flags |= (uint)GpuMaterialFlags.AlphaCutout;
        if (m.Diffuse is not null) flags |= (uint)GpuMaterialFlags.HasDiffuse;
        if (m.Normal is not null) flags |= (uint)GpuMaterialFlags.HasNormal;
        if (m.AO is not null) flags |= (uint)GpuMaterialFlags.HasAo;

        return new GpuMaterial {
            DiffuseHandle = Handle(m.Diffuse ?? DefaultTextures.Neutral(TextureType.Diffuse)),
            NormalHandle = Handle(m.Normal ?? DefaultTextures.Neutral(TextureType.Normal)),
            MetallicHandle = Handle(m.Metallic ?? DefaultTextures.Neutral(TextureType.Metallic)),
            RoughnessHandle = Handle(m.Roughness ?? DefaultTextures.Neutral(TextureType.Roughness)),
            AoHandle = Handle(m.AO ?? DefaultTextures.Neutral(TextureType.AO)),
            EmissiveHandle = Handle(m.Emissive ?? DefaultTextures.Neutral(TextureType.Emissive)),
            BaseColorFactor = m.BaseColorFactor,
            EmissiveFactor = new Vector4(m.EmissiveColor * m.EmissiveIntensity, 0f),
            // Fold in the renderer's global debug multipliers EXACTLY as CPU SetMaterialUniforms does:
            // the uniforms are material.X * globalX (metallic, roughness, normalStrength).
            MetallicMultiplier = m.MetallicFactor * gMetallic,
            RoughnessMultiplier = m.RoughnessFactor * gRoughness,
            NormalStrength = m.NormalStrength * gNormal,
            Opacity = m.Opacity,
            Flags = flags,
        };
    }

    // Bindless handle for a texture, cached by UID and made resident on first use.
    ulong Handle(Texture2D tex) {
        if (tex is null)
            return 0;
        int uid = tex.UID;
        if (uid == 0)
            return 0;
        if (handleByTexture.TryGetValue(uid, out ulong h))
            return h;

        long handle = GL.Arb.GetTextureHandle(uid);
        if (handle == 0)
            return 0;
        var uh = (ulong)handle;
        if (resident.Add(uh))
            GL.Arb.MakeTextureHandleResident(handle);
        handleByTexture[uid] = uh;
        return uh;
    }

    public void Dispose() {
        foreach (ulong h in resident)
            GL.Arb.MakeTextureHandleNonResident((long)h);
        resident.Clear();
        handleByTexture.Clear();
        indexByMaterial.Clear();
    }
}
