using System;
using System.Collections.Generic;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Dxc;

namespace BallisticEngine.DX12;

// One compiled custom-surface pipeline. IsFallback = the source failed to compile and this is the
// magenta-checker error PSO; Error carries the DXC log for the Console.
public sealed class Dx12SurfacePso {
    public ID3D12PipelineState Pso;
    public bool IsFallback;
    public string Error;
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
    public Dx12SurfacePso GetOrCompile(string surfaceBody, string key) {
        if (cache.TryGetValue(key, out var hit)) return hit;
        Dx12SurfacePso entry;
        try {
            entry = new Dx12SurfacePso { Pso = BuildPso(surfaceBody, key) };
        }
        catch (Exception e) {
            Debugging.LogError($"[surface] '{key}' compile failed; drawing magenta fallback:\n{e.Message}");
            var fb = Fallback();
            entry = new Dx12SurfacePso { Pso = fb.Pso, IsFallback = true, Error = e.Message };
        }
        cache[key] = entry;
        return entry;
    }

    // Drop a cached entry so the next GetOrCompile recompiles (hot-reload). Returns the old PSO (if any)
    // so the caller can defer-dispose it past the GPU's in-flight frames.
    public ID3D12PipelineState Invalidate(string key) {
        if (cache.Remove(key, out var old) && !old.IsFallback) return old.Pso;
        return null;
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
        Console.WriteLine("[surface] SELFTEST end");
    }

    ID3D12PipelineState BuildPso(string surfaceBody, string fileName) {
        // Inject the custom Surface() body at the __USER_SURFACE__ marker — which sits BEFORE PSMain, so
        // its call to Surface() resolves (HLSL has no forward declaration). CUSTOM_SURFACE (defined first)
        // omits the skeleton's default Standard body via its #ifndef, so the injected body is the only
        // Surface(). Appending after the skeleton instead would place the body after PSMain → "undeclared
        // identifier 'Surface'".
        const string marker = "//USER_SURFACE_MARKER";
        if (!skeleton.Contains(marker))
            throw new InvalidOperationException("SurfaceSkeleton.hlsl is missing the //USER_SURFACE_MARKER line.");
        string injected = skeleton.Replace(marker, surfaceBody);
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
