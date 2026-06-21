using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

// PSO CACHE — two tiers, both BYTE-NEUTRAL (a hit returns a PSO that renders identically to a fresh create):
//
//   TIER 1 — IN-MEMORY DEDUPE (always on): a Dictionary keyed by a stable hash of the PSO description
//     (root-sig pointer + shader bytecode hashes + RT/DS formats + blend/raster/depth/topology/sample state +
//     input layout). A second identical CreateGraphicsPso/CreateComputePso returns the SAME ID3D12PipelineState
//     instead of asking the driver to compile a duplicate. Zero risk: identical desc → identical PSO, and the
//     engine never mutates a PSO after create, so sharing one object is safe.
//
//   TIER 2 — DISK via ID3D12PipelineLibrary (Vortice exposes ID3D12Device1.CreatePipelineLibrary + the library's
//     LoadGraphics/LoadComputePipeline/StorePipeline/Serialize). On a cold start the library is empty → every
//     PSO is a normal Create + StorePipeline(name). On shutdown the library serialises to
//     <project>\Library\PsoCache\dx12_pso.bin. A WARM start loads that blob into the library; LoadGraphicsPipeline
//     (name, desc) then returns the driver-compiled PSO from the cache, skipping the (slow) driver back-end
//     compile — the cold-start PSO stutter that the DXIL-bytecode cache (which only skips the DXC front-end)
//     can't touch. The runtime AUTO-INVALIDATES the blob on a driver/adapter/D3D-runtime version change
//     (CreatePipelineLibrary fails E_INVALIDARG / version-mismatch → we just start a fresh empty library), so a
//     stale blob can never feed a wrong PSO. Names are caller-supplied and must be UNIQUE per distinct PSO.
//
// GATING: BALLISTIC_DX12_PSO_CACHE=0 disables the DISK tier only (TIER 1 dedupe stays — it's free + byte-neutral).
// The whole thing is keyed entirely off the description, so it is impossible for a hit to differ from a fresh PSO.
public sealed class Dx12PsoCache : IDisposable {
    readonly ID3D12Device2 device;
    readonly Dictionary<long, ID3D12PipelineState> memo = new();   // TIER 1: desc-hash → PSO (engine-owned, never mutated)
    readonly object gate = new();

    // TIER 2 (disk) state. pipeLib is null when the disk tier is off or CreatePipelineLibrary is unsupported —
    // every path then degrades to TIER 1 + a plain Create (still correct, just no warm driver-compile skip).
    readonly bool diskEnabled;
    readonly string diskPath;
    ID3D12PipelineLibrary pipeLib;
    byte[] loadedBlob;                 // kept alive: CreatePipelineLibrary does NOT copy the blob — the library
                                       // references it until Serialize, so freeing it early is a use-after-free.
    bool dirty;                        // a StorePipeline happened → the blob changed → reserialise on shutdown
    readonly HashSet<string> storedNames = new();   // guard StorePipeline duplicate-name (D3D12 errors on dupes)

    public Dx12PsoCache(ID3D12Device2 dev, string cacheDirectory) {
        device = dev;
        // Disk tier default ON; =0 bypasses (e.g. a suspect blob). Headless tools without a project pass null →
        // disk off, in-memory dedupe still active.
        diskEnabled = !string.IsNullOrEmpty(cacheDirectory)
                      && Environment.GetEnvironmentVariable("BALLISTIC_DX12_PSO_CACHE") != "0";
        if (diskEnabled) {
            diskPath = Path.Combine(cacheDirectory, "dx12_pso.bin");
            TryInitPipelineLibrary();
        }
    }

    void TryInitPipelineLibrary() {
        // ID3D12PipelineLibrary lives on ID3D12Device1 — query the facet (always present on a Device2, but guard).
        ID3D12Device1 dev1;
        try { dev1 = device.QueryInterfaceOrNull<ID3D12Device1>(); }
        catch { dev1 = null; }
        if (dev1 is null) return;   // no Device1 → disk tier silently off (in-memory dedupe unaffected)

        try {
            if (File.Exists(diskPath)) {
                // Keep the blob PINNED for the library's lifetime: CreatePipelineLibrary REFERENCES (does not copy)
                // the blob until Serialize, so freeing it early is a use-after-free.
                loadedBlob = File.ReadAllBytes(diskPath);
                // The `out` overload returns a Result instead of throwing on the DRIVER/ADAPTER/RUNTIME-version
                // mismatch CreatePipelineLibrary validates the blob against (driver update, GPU swap, corrupt file).
                // On a non-success Result we drop the stale blob and start a fresh empty library so the warm path is
                // rebuilt THIS run rather than disabled forever.
                var r = dev1.CreatePipelineLibrary(loadedBlob, out pipeLib);
                if (r.Failure) { pipeLib = null; loadedBlob = null; }
            }
            if (pipeLib is null) {
                loadedBlob = null;
                var r = dev1.CreatePipelineLibrary(Array.Empty<byte>(), out pipeLib);   // fresh empty library
                if (r.Failure) pipeLib = null;   // unsupported entirely → disk tier off
            }
        }
        catch { pipeLib = null; loadedBlob = null; }   // any unexpected failure → disk tier off, dedupe still on
        finally { dev1.Dispose(); }
    }

    // ---- TIER 1 + TIER 2 wrappers. `name` is the disk-cache key (unique per distinct PSO); empty/null name
    // ---- skips the disk tier for that PSO (in-memory dedupe still applies).
    public ID3D12PipelineState CreateGraphics(in GraphicsPipelineStateDescription desc, string name) {
        long key = HashGraphics(desc);
        lock (gate) {
            if (memo.TryGetValue(key, out var hit)) return Share(hit);   // TIER 1: identical desc already built this run
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

    // A dedupe hit hands the SAME COM object to a second caller. Each caller Disposes its own PSO field, so without
    // an extra ref the second Dispose would over-release (double-free). AddRef balances the native COM refcount:
    // N holders → N Releases. The cache itself does NOT hold a counted ref (the memo entry is a weak alias freed
    // when the last holder disposes), so Dispose() only clears the dictionary — see Dispose().
    static ID3D12PipelineState Share(ID3D12PipelineState pso) { pso.AddRef(); return pso; }

    ID3D12PipelineState MakeGraphics(in GraphicsPipelineStateDescription desc, string name) {
        if (pipeLib is not null && !string.IsNullOrEmpty(name)) {
            // WARM hit: the library has this name → the driver returns the cached compiled PSO (no back-end compile).
            try { return pipeLib.LoadGraphicsPipeline(name, desc); }
            catch { /* name not present (cold) or load failed → create + store below */ }
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
        if (!storedNames.Add(name)) return;   // already stored this name this run (StorePipeline errors on dupes)
        try { pipeLib.StorePipeline(name, pso); dirty = true; }
        catch { /* StorePipeline best-effort — the PSO is valid regardless; warm-skip just won't help next run */ }
    }

    // Serialise the accumulated library to disk so the NEXT launch warm-loads it. Called once at renderer
    // shutdown. Best-effort: a write failure only loses the warm-start optimisation, never correctness.
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
            File.Move(tmp, diskPath, overwrite: true);   // atomic-ish: a crash mid-write never leaves a torn blob
        }
        catch { /* shutdown cache write is best-effort */ }
    }

    // ---------- stable description hashing (TIER 1 key). FNV-1a 64-bit over the fields that change the PSO. ----------
    // Bytecode is hashed by CONTENT (not pointer): two passes that compile the same embedded .hlsl produce
    // distinct managed byte[]s but identical bytecode → they must collapse to one PSO.
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
        // Rasterizer / blend / depth-stencil structs are blittable value types — hash their raw bytes.
        h = HashStruct(h, d.RasterizerState);
        h = HashStruct(h, d.BlendState);
        h = HashStruct(h, d.DepthStencilState);
        // Render-target formats (the count + each format).
        var fmts = d.RenderTargetFormats;
        if (fmts is not null) {
            h = Fnv.Mix(h, (ulong)fmts.Length);
            foreach (var f in fmts) h = Fnv.Mix(h, (ulong)f);
        } else h = Fnv.Mix(h, 0);
        // Input layout: per element, the semantic name + index + format + slot + offset (everything that matters).
        // InputLayoutDescription is a CLASS — null for full-screen passes (InputLayout = null) → no elements.
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

    // The root-sig identity. Two distinct ID3D12RootSignature objects are distinct PSO inputs; the COM pointer
    // (NativePointer) is a stable per-object identity for the cache lifetime (PSOs are built once at init while
    // their root sigs are live). Good enough for the in-memory key — a freed+realloc'd root sig at the same
    // address can't collide because the engine builds all PSOs up front, before any root sig is disposed.
    static ulong RootSigId(ID3D12RootSignature rs) => rs is null ? 0 : (ulong)rs.NativePointer.ToInt64();

    static ulong HashBytecode(ulong h, ReadOnlyMemory<byte> code) {
        var span = code.Span;
        h = Fnv.Mix(h, (ulong)span.Length);
        // FNV over the bytes. Embedded shaders are small (KBs) and PSOs are built once at init, so this is not a
        // hot path — no need to cache, but the cost is trivial either way.
        for (int i = 0; i < span.Length; i++) h = Fnv.MixByte(h, span[i]);
        return h;
    }

    static unsafe ulong HashStruct<T>(ulong h, T value) where T : unmanaged {
        var p = (byte*)&value;
        for (int i = 0; i < sizeof(T); i++) h = Fnv.MixByte(h, p[i]);
        return h;
    }

    public void Dispose() {
        // The PSOs are owned BY THE PASSES that requested them (they Dispose their own fields), so the cache must
        // NOT dispose the memoised PSOs — doing so would double-free. The cache only owns the pipeline library.
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
