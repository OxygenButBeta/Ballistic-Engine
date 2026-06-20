using System;
using System.Collections.Generic;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Dxc;

namespace BallisticEngine.DX12;

// One compiled custom-surface pipeline. IsFallback = the source failed to compile and this is the
// magenta-checker error PSO; Error carries the DXC log for the Console. SourcePath/Source track what
// was compiled so hot-reload can recompile the same cache entry in place.
public sealed class Dx12SurfacePso {
    public ID3D12PipelineState Pso;
    public bool IsFallback;
    public string Error;
    public string SourcePath;   // project-relative source asset path (for hot-reload watch)
    public string Source;       // the surface body that produced Pso
    public BallisticEngine.ShaderProperties Props;   // declared props (for the custom cbuffer/texture layout)
}

// Compiles user Surface() bodies into G-buffer PSOs that are drop-in for the Standard opaque PSO:
// same root signature, same VS (the engine's VSMain → z-prepass position invariance preserved), same
// MRT/depth/raster state — only the pixel shader's surface math differs. A compile failure yields the
// magenta-checker FALLBACK PSO (visible error, never a crash). Caches by source key; the renderer owns
// the lifetime and drives recompiles (Stage D hot-reload).
//
// The skeleton has NO #include support, so a custom body is produced by STRING CONCATENATION:
//   SurfaceSkeleton.hlsl  +  "\n#define CUSTOM_SURFACE\n"  +  <user Surface() body>
// CUSTOM_SURFACE omits the skeleton's default Standard body so the user's Surface() is the only one.
public sealed class Dx12SurfaceShaderCache : IDisposable {
    readonly ID3D12Device dev;
    readonly ID3D12RootSignature rootSig;
    readonly InputLayoutDescription layout;
    readonly byte[] vsBytecode;                 // the engine VSMain — shared by every surface PSO
    readonly string skeleton;                   // SurfaceSkeleton.hlsl source, read once

    readonly Dictionary<string, Dx12SurfacePso> cache = new();
    Dx12SurfacePso fallback;                    // compiled once, reused for every failed material

    // Hot-reload: PSOs replaced by a reload can't be freed immediately (the GPU may still reference them
    // for FramesInFlight frames). They're parked here with the frame index after which they're safe to
    // dispose; DrainDeferred(currentFrame) frees the matured ones. Set FramesInFlight from the renderer.
    readonly List<(ID3D12PipelineState pso, long safeAfterFrame)> deferredDispose = new();
    public int FramesInFlight = 3;

    public Dx12SurfaceShaderCache(ID3D12Device device, ID3D12RootSignature gbufferRootSig,
        InputLayoutDescription gbufferLayout, byte[] gbufferVs) {
        dev = device;
        rootSig = gbufferRootSig;
        layout = gbufferLayout;
        vsBytecode = gbufferVs;
        skeleton = EmbeddedShaderSource.ReadHlsl("SurfaceSkeleton.hlsl");
    }

    // The magenta-checker PSO, compiled on first use from the embedded fallback body. If even THAT
    // fails (should never happen — it's engine-authored), returns null and the caller draws Standard.
    public Dx12SurfacePso Fallback() {
        if (fallback is not null) return fallback;
        string body = EmbeddedShaderSource.ReadHlsl("SurfaceFallback.hlsl");
        try {
            fallback = new Dx12SurfacePso { Pso = BuildPso(body, "SurfaceFallback.hlsl"), IsFallback = true };
        }
        catch (Exception e) {
            Debugging.LogError($"[surface] FALLBACK shader failed to compile (engine bug): {e.Message}");
            fallback = new Dx12SurfacePso { Pso = null, IsFallback = true, Error = e.Message };
        }
        return fallback;
    }

    // Get (or compile) the PSO for a custom surface body. `key` is a stable identity (shader GUID +
    // content hash) — same key returns the cached PSO. Compile failure caches+returns the fallback so
    // a broken material doesn't recompile every frame.
    public Dx12SurfacePso GetOrCompile(string surfaceBody, string key, string sourcePath = null,
        BallisticEngine.ShaderProperties props = null) {
        if (cache.TryGetValue(key, out var hit)) return hit;
        var entry = Compile(surfaceBody, key, sourcePath, props);
        cache[key] = entry;
        return entry;
    }

    // Build one cache entry (compile or fall back). Records SourcePath/Source/Props so hot-reload can match
    // a changed file to its entries and recompile in place with the same declared property layout.
    Dx12SurfacePso Compile(string surfaceBody, string key, string sourcePath, BallisticEngine.ShaderProperties props) {
        try {
            return new Dx12SurfacePso { Pso = BuildPso(surfaceBody, key, props), SourcePath = sourcePath,
                Source = surfaceBody, Props = props };
        }
        catch (Exception e) {
            Debugging.LogError($"[surface] '{key}' compile failed; drawing magenta fallback:\n{e.Message}");
            var fb = Fallback();
            return new Dx12SurfacePso { Pso = fb.Pso, IsFallback = true, Error = e.Message,
                SourcePath = sourcePath, Source = surfaceBody, Props = props };
        }
    }

    // HOT-RELOAD: recompile every cache entry whose source asset is `changedPath`, with `newSource`. The
    // old (non-fallback) PSO is DEFERRED-disposed past FramesInFlight frames — freeing it now would be a
    // use-after-free while the GPU is still drawing the in-flight frame. Returns the number of entries
    // reloaded (0 = the changed file wasn't a live surface source). MUST run between frames (the renderer
    // calls this before BeginRender), never inside a draw list. `currentFrame` is a monotonically
    // increasing frame counter.
    public int Reload(string changedPath, string newSource, long currentFrame) {
        int n = 0;
        foreach (var k in new List<string>(cache.Keys)) {
            var old = cache[k];
            if (!string.Equals(old.SourcePath, changedPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (old.Source == newSource) continue; // unchanged content (editor saved without edits)
            var fresh = Compile(newSource, k, changedPath, old.Props);
            cache[k] = fresh;
            if (!old.IsFallback && old.Pso is not null)
                deferredDispose.Add((old.Pso, currentFrame + FramesInFlight));
            n++;
            Debugging.Log($"[surface] hot-reloaded '{changedPath}' -> {(fresh.IsFallback ? "FALLBACK (compile error)" : "OK")}");
        }
        return n;
    }

    // Free PSOs whose deferred-dispose deadline has passed (the GPU is no longer reading them). Called
    // once per frame by the renderer with the current frame counter.
    public void DrainDeferred(long currentFrame) {
        for (int i = deferredDispose.Count - 1; i >= 0; i--) {
            if (deferredDispose[i].safeAfterFrame <= currentFrame) {
                deferredDispose[i].pso?.Dispose();
                deferredDispose.RemoveAt(i);
            }
        }
    }

    // Compiles the fallback + a trivial custom body + a deliberately-broken body, logging each result.
    // Proves the skeleton-concat path + DXC + the fallback all work, without needing an authored .shader.
    // Gated by BALLISTIC_DX12_SURFACE_SELFTEST=1, called once at renderer init.
    public void SelfTest() {
        Console.WriteLine("[surface] SELFTEST begin");
        var fb = Fallback();
        Console.WriteLine($"[surface] selftest: fallback PSO {(fb.Pso is not null ? "OK" : "FAILED: " + fb.Error)}");

        const string ok = "SurfaceOutput Surface(SurfaceInput i){ SurfaceOutput s; s.Albedo=float3(1,0,0);" +
            " s.Normal=normalize(i.NormalW); s.Metallic=0; s.Roughness=0.5; s.AO=1; s.Emissive=0..xxx; s.Alpha=1; return s; }";
        var good = GetOrCompile(ok, "__selftest_ok__");
        Console.WriteLine($"[surface] selftest: trivial custom body {(good.IsFallback ? "FELL BACK (unexpected): " + good.Error : "OK")}");

        const string broken = "SurfaceOutput Surface(SurfaceInput i){ this is not valid hlsl }";
        var bad = GetOrCompile(broken, "__selftest_broken__");
        Console.WriteLine($"[surface] selftest: broken body -> {(bad.IsFallback ? "fallback (correct)" : "compiled (WRONG)")}");

        // Custom-property body: declares _RimPower (Float) + _RimColor (Color) + _RimMask (Texture2D) and
        // reads all three — proves GenerateCustomDecls emits a compilable cbuffer b2 + texture t6.
        var props = new BallisticEngine.ShaderProperties(new[] {
            BallisticEngine.ShaderProperty.FloatProp("_RimPower", "Rim Power", BallisticEngine.MaterialSemantic.None, 2f),
            BallisticEngine.ShaderProperty.ColorProp("_RimColor", "Rim Color", BallisticEngine.MaterialSemantic.None, new System.Numerics.Vector4(1,0,1,1)),
            BallisticEngine.ShaderProperty.Texture("_RimMask", "Rim Mask", BallisticEngine.MaterialSemantic.None),
        });
        const string custom = "SurfaceOutput Surface(SurfaceInput i){ SurfaceOutput s;" +
            " float m = _RimMask.Sample(LinearWrap, i.Uv).r;" +
            " s.Albedo = _RimColor.rgb * pow(m, _RimPower); s.Emissive = s.Albedo;" +
            " s.Normal = normalize(i.NormalW); s.Metallic=0; s.Roughness=0.5; s.AO=1; s.Alpha=1; return s; }";
        var c2 = GetOrCompile(custom, "__selftest_custom__", null, props);
        Console.WriteLine($"[surface] selftest: custom-prop body (b2 cbuffer + t6) -> {(c2.IsFallback ? "FELL BACK (WRONG): " + c2.Error : "OK")}");
        Console.WriteLine("[surface] SELFTEST end");
    }

    // The skeleton marker the cache replaces with custom decls + the user Surface() body.
    const string CustomDeclMarker = "//CUSTOM_DECLS_MARKER";
    const string SurfaceMarker = "//USER_SURFACE_MARKER";

    // Generate the HLSL custom-property declarations (cbuffer CustomProps b2 + custom Texture2D t6..) from
    // the shader's declared semantic-None properties, in declared order. STRADDLE-SAFE layout: every scalar
    // property occupies its OWN 16-byte cbuffer slot (a float + float3 pad), so the C# packing offset is
    // exactly 16*index and a vector can never cross a 16-byte boundary. The body reads each by its declared
    // name (e.g. _RimPower, _RimColor). Returns "" when the shader has no custom props (Standard / no-None →
    // byte-identical to today's emitted source).
    public static string GenerateCustomDecls(BallisticEngine.ShaderProperties props) {
        if (props is null) return "";
        var cb = new System.Text.StringBuilder();
        var texDecls = new System.Text.StringBuilder();
        int cbMembers = 0, texSlot = 6, padIdx = 0;
        cb.Append("cbuffer CustomProps : register(b2) {\n");
        foreach (var p in props) {
            if (p.Semantic != BallisticEngine.MaterialSemantic.None) continue;
            switch (p.Type) {
                case BallisticEngine.ShaderPropertyType.Texture2D:
                    if (texSlot < 6 + MaxCustomTexHlsl)
                        texDecls.Append($"Texture2D {p.Name} : register(t{texSlot++});\n");
                    break;
                case BallisticEngine.ShaderPropertyType.Color:
                case BallisticEngine.ShaderPropertyType.Vector:
                    cb.Append($"    float4 {p.Name};\n"); cbMembers++;
                    break;
                default: // Float / Range — own 16-byte slot (float + float3 pad), no straddle
                    cb.Append($"    float {p.Name}; float3 _cpad{padIdx++};\n"); cbMembers++;
                    break;
            }
        }
        cb.Append("};\n");
        // No custom cbuffer members AND no textures → emit nothing (byte-identical to the pre-custom source).
        if (cbMembers == 0 && texSlot == 6) return "";
        // An empty cbuffer is illegal in HLSL; only emit the cbuffer block when it has members.
        return (cbMembers > 0 ? cb.ToString() : "") + texDecls.ToString();
    }

    // Mirror of MaxCustomTex on the renderer (kept local so the cache has no renderer dependency).
    public const int MaxCustomTexHlsl = 4;

    ID3D12PipelineState BuildPso(string surfaceBody, string fileName, BallisticEngine.ShaderProperties props = null) {
        // Inject the custom Surface() body at the marker — which sits BEFORE PSMain, so its call to Surface()
        // resolves (HLSL has no forward declaration). CUSTOM_SURFACE (defined first) omits the skeleton's
        // default Standard body. The custom-property decls (cbuffer b2 + custom textures) go at the
        // CUSTOM_DECLS_MARKER (after the Standard cbuffers/textures, before any function uses them).
        if (!skeleton.Contains(SurfaceMarker))
            throw new InvalidOperationException("SurfaceSkeleton.hlsl is missing the //USER_SURFACE_MARKER line.");
        string injected = skeleton.Replace(SurfaceMarker, surfaceBody);
        injected = injected.Replace(CustomDeclMarker, GenerateCustomDecls(props));
        string source = "#define CUSTOM_SURFACE 1\n" + injected;
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, source, "PSMain", fileName);
        // VS is ALWAYS the engine's prebuilt VSMain bytecode — never recompiled, so prepass depth is
        // bit-identical no matter what the surface body does.
        return dev.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = rootSig, VertexShader = vsBytecode, PixelShader = ps, InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullClockwise,
            BlendState = BlendDescription.Opaque, DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = Dx12GBuffer.ColorFormats,
            DepthStencilFormat = Dx12GBuffer.DepthFormat, SampleDescription = new SampleDescription(1, 0),
        });
    }

    public void Dispose() {
        foreach (var e in cache.Values)
            if (!e.IsFallback) e.Pso?.Dispose();
        cache.Clear();
        fallback?.Pso?.Dispose();
        fallback = null;
    }
}
