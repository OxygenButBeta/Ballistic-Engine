using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

public sealed class Dx12PsoCache : IDisposable {
    readonly ID3D12Device2 device;
    readonly Dictionary<long, ID3D12PipelineState> memo = new();
    readonly object gate = new();

    readonly bool diskEnabled;
    readonly string diskPath;
    ID3D12PipelineLibrary pipeLib;
    byte[] loadedBlob;

    bool dirty;
    readonly HashSet<string> storedNames = new();

    public Dx12PsoCache(ID3D12Device2 dev, string cacheDirectory) {
        device = dev;
        diskEnabled = !string.IsNullOrEmpty(cacheDirectory)
                      && Environment.GetEnvironmentVariable("BALLISTIC_DX12_PSO_CACHE") != "0";
        if (diskEnabled) {
            diskPath = Path.Combine(cacheDirectory, "dx12_pso.bin");
            TryInitPipelineLibrary();
        }
    }

    void TryInitPipelineLibrary() {
        ID3D12Device1 dev1;
        try { dev1 = device.QueryInterfaceOrNull<ID3D12Device1>(); }
        catch { dev1 = null; }
        if (dev1 is null) return;

        try {
            if (File.Exists(diskPath)) {
                loadedBlob = File.ReadAllBytes(diskPath);
                var r = dev1.CreatePipelineLibrary(loadedBlob, out pipeLib);
                if (r.Failure) { pipeLib = null; loadedBlob = null; }
            }
            if (pipeLib is null) {
                loadedBlob = null;
                var r = dev1.CreatePipelineLibrary(Array.Empty<byte>(), out pipeLib);
                if (r.Failure) pipeLib = null;
            }
        }
        catch { pipeLib = null; loadedBlob = null; }
        finally { dev1.Dispose(); }
    }

    public ID3D12PipelineState CreateGraphics(in GraphicsPipelineStateDescription desc, string name) {
        long key = HashGraphics(desc);
        lock (gate) {
            if (memo.TryGetValue(key, out var hit)) return Share(hit);
            ID3D12PipelineState pso = MakeGraphics(desc, name);
            memo[key] = pso;
            return pso;
        }
    }

    public ID3D12PipelineState CreateCompute(in ComputePipelineStateDescription desc, string name) {
        long key = HashCompute(desc);
        lock (gate) {
            if (memo.TryGetValue(key, out var hit)) return Share(hit);
            ID3D12PipelineState pso = MakeCompute(desc, name);
            memo[key] = pso;
            return pso;
        }
    }

    static ID3D12PipelineState Share(ID3D12PipelineState pso) { pso.AddRef(); return pso; }

    ID3D12PipelineState MakeGraphics(in GraphicsPipelineStateDescription desc, string name) {
        if (pipeLib is not null && !string.IsNullOrEmpty(name)) {
            try { return pipeLib.LoadGraphicsPipeline(name, desc); }
            catch {
            }
            var pso = device.CreateGraphicsPipelineState(desc);
            StoreInLibrary(name, pso);
            return pso;
        }
        return device.CreateGraphicsPipelineState(desc);
    }

    ID3D12PipelineState MakeCompute(in ComputePipelineStateDescription desc, string name) {
        if (pipeLib is not null && !string.IsNullOrEmpty(name)) {
            try { return pipeLib.LoadComputePipeline(name, desc); }
            catch { }
            var pso = device.CreateComputePipelineState(desc);
            StoreInLibrary(name, pso);
            return pso;
        }
        return device.CreateComputePipelineState(desc);
    }

    void StoreInLibrary(string name, ID3D12PipelineState pso) {
        if (!storedNames.Add(name)) return;
        try { pipeLib.StorePipeline(name, pso); dirty = true; }
        catch {
        }
    }

    public unsafe void SaveToDisk() {
        if (pipeLib is null || !dirty || string.IsNullOrEmpty(diskPath)) return;
        try {
            nuint size = pipeLib.SerializedSize;
            if (size == 0) return;
            byte[] buf = new byte[(int)size];
            fixed (byte* p = buf)
                pipeLib.Serialize((IntPtr)p, size);
            Directory.CreateDirectory(Path.GetDirectoryName(diskPath));
            string tmp = diskPath + ".tmp";
            File.WriteAllBytes(tmp, buf);
            File.Move(tmp, diskPath, overwrite: true);
        }
        catch {
        }
    }

    static long HashGraphics(in GraphicsPipelineStateDescription d) {
        ulong h = Fnv.Init;
        h = Fnv.Mix(h, RootSigId(d.RootSignature));
        h = HashBytecode(h, d.VertexShader);
        h = HashBytecode(h, d.PixelShader);
        h = HashBytecode(h, d.DomainShader);
        h = HashBytecode(h, d.HullShader);
        h = HashBytecode(h, d.GeometryShader);
        h = Fnv.Mix(h, (ulong)d.PrimitiveTopologyType);
        h = Fnv.Mix(h, d.SampleMask);
        h = Fnv.Mix(h, (ulong)d.SampleDescription.Count);
        h = Fnv.Mix(h, (ulong)d.SampleDescription.Quality);
        h = Fnv.Mix(h, (ulong)d.DepthStencilFormat);
        h = HashStruct(h, d.RasterizerState);
        h = HashStruct(h, d.BlendState);
        h = HashStruct(h, d.DepthStencilState);
        var fmts = d.RenderTargetFormats;
        if (fmts is not null) {
            h = Fnv.Mix(h, (ulong)fmts.Length);
            foreach (var f in fmts) h = Fnv.Mix(h, (ulong)f);
        } else h = Fnv.Mix(h, 0);

        var il = d.InputLayout?.Elements;
        if (il is not null)
            foreach (var e in il) {
                h = Fnv.Mix(h, (ulong)(e.SemanticName?.GetHashCode() ?? 0));
                h = Fnv.Mix(h, (ulong)e.SemanticIndex);
                h = Fnv.Mix(h, (ulong)e.Format);
                h = Fnv.Mix(h, (ulong)e.Slot);
                h = Fnv.Mix(h, (ulong)e.AlignedByteOffset);
                h = Fnv.Mix(h, (ulong)e.Classification);
                h = Fnv.Mix(h, (ulong)e.InstanceDataStepRate);
            }
        return unchecked((long)h);
    }

    static long HashCompute(in ComputePipelineStateDescription d) {
        ulong h = Fnv.Init;
        h = Fnv.Mix(h, RootSigId(d.RootSignature));
        h = HashBytecode(h, d.ComputeShader);
        return unchecked((long)h);
    }

    static ulong RootSigId(ID3D12RootSignature rs) => rs is null ? 0 : (ulong)rs.NativePointer.ToInt64();

    static ulong HashBytecode(ulong h, ReadOnlyMemory<byte> code) {
        var span = code.Span;
        h = Fnv.Mix(h, (ulong)span.Length);
        for (int i = 0; i < span.Length; i++) h = Fnv.MixByte(h, span[i]);
        return h;
    }

    static unsafe ulong HashStruct<T>(ulong h, T value) where T : unmanaged {
        var p = (byte*)&value;
        for (int i = 0; i < sizeof(T); i++) h = Fnv.MixByte(h, p[i]);
        return h;
    }

    public void Dispose() {
        lock (gate) {
            memo.Clear();
            pipeLib?.Dispose();
            pipeLib = null;
            loadedBlob = null;
        }
    }

    static class Fnv {
        public const ulong Init = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;
        public static ulong Mix(ulong h, ulong v) {
            for (int i = 0; i < 8; i++) { h ^= v & 0xFF; h *= Prime; v >>= 8; }
            return h;
        }
        public static ulong MixByte(ulong h, byte b) { h ^= b; h *= Prime; return h; }
    }
}
