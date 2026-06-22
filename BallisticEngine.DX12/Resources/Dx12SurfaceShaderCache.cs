using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Dxc;

namespace BallisticEngine.DX12;

public sealed class Dx12SurfacePso {
    public ID3D12PipelineState Pso;
    public bool IsFallback;
    public string Error;
    public string SourcePath;
    public string Source;
    public BallisticEngine.ShaderProperties Props;
}

public sealed class Dx12SurfaceShaderCache : IDisposable {
    readonly ID3D12Device dev;
    readonly ID3D12RootSignature rootSig;
    readonly InputLayoutDescription layout;
    readonly byte[] vsBytecode;
    readonly string skeleton;

    readonly Dictionary<string, Dx12SurfacePso> cache = new();
    Dx12SurfacePso fallback;

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

    public Dx12SurfacePso GetOrCompile(string surfaceBody, string key, string sourcePath = null,
        BallisticEngine.ShaderProperties props = null) {
        if (cache.TryGetValue(key, out var hit)) return hit;
        var entry = Compile(surfaceBody, key, sourcePath, props);
        cache[key] = entry;
        return entry;
    }

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

    public int Reload(string changedPath, string newSource, long currentFrame) {
        int n = 0;
        foreach (var k in new List<string>(cache.Keys)) {
            var old = cache[k];
            if (!string.Equals(old.SourcePath, changedPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (old.Source == newSource) continue;
            var fresh = Compile(newSource, k, changedPath, old.Props);
            cache[k] = fresh;
            if (!old.IsFallback && old.Pso is not null)
                deferredDispose.Add((old.Pso, currentFrame + FramesInFlight));
            n++;
            Debugging.Log($"[surface] hot-reloaded '{changedPath}' -> {(fresh.IsFallback ? "FALLBACK (compile error)" : "OK")}");
        }
        return n;
    }

    public void DrainDeferred(long currentFrame) {
        for (int i = deferredDispose.Count - 1; i >= 0; i--) {
            if (deferredDispose[i].safeAfterFrame <= currentFrame) {
                deferredDispose[i].pso?.Dispose();
                deferredDispose.RemoveAt(i);
            }
        }
    }

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

    const string CustomDeclMarker = "//CUSTOM_DECLS_MARKER";
    const string SurfaceMarker = "//USER_SURFACE_MARKER";

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
                default:
                    cb.Append($"    float {p.Name}; float3 _cpad{padIdx++};\n"); cbMembers++;
                    break;
            }
        }
        cb.Append("};\n");
        if (cbMembers == 0 && texSlot == 6) return "";
        return (cbMembers > 0 ? cb.ToString() : "") + texDecls.ToString();
    }

    public const int MaxCustomTexHlsl = 4;

    ID3D12PipelineState BuildPso(string surfaceBody, string fileName, BallisticEngine.ShaderProperties props = null) {
        if (!skeleton.Contains(SurfaceMarker))
            throw new InvalidOperationException("SurfaceSkeleton.hlsl is missing the //USER_SURFACE_MARKER line.");
        string injected = skeleton.Replace(SurfaceMarker, surfaceBody);
        injected = injected.Replace(CustomDeclMarker, GenerateCustomDecls(props));
        string source = "#define CUSTOM_SURFACE 1\n" + injected;
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, source, "PSMain", fileName);
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
