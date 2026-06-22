using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

// FAZ -1 — Render-graph v2. Per-frame virtual-resource registry.
//
// Distinguishes two resource classes (UE-RDG / Granite vocabulary):
//   • IMPORTED  — an external/persistent ID3D12Resource handed in (backbuffer, TAA/GI history,
//                 swapchain image). NEVER pooled or aliased; the graph only tracks+transitions it.
//                 Its tracked state starts at the registered current state and is restored-by-tracking.
//   • TRANSIENT — graph-owned. No GPU object at registration time; Compile() places it on the
//                 aliasing heap and Realise() fills in the live ID3D12Resource.
//
// Per-resource current D3D12 state is tracked here so the barrier deriver can compute transitions
// from "where the resource is now" to "what the next pass needs" without the pass authors writing
// any barriers (Granite invalidate/flush). State updates happen during Execute as barriers fire.

public sealed class Dx12RgResourceRegistry {
    public sealed class Entry {
        public int Id;
        public string Name;
        public bool Imported;
        public Dx12RgHandleKind Kind;

        // Transient only:
        public Dx12RgResourceDesc Desc;
        public long AllocBytes;
        public long AllocAlign;
        public int HeapCategory = -1;     // tier-1 fallback: 0 buffers / 1 non-RTDS tex / 2 RTDS tex; -1 == tier-2 single heap
        public ulong HeapOffset;
        public int RegionId = -1;         // aliasing region this transient was packed into

        // Lifetime on the linearized executed order (assigned during Compile). -1 == unused/culled.
        public int FirstPass = int.MaxValue;
        public int LastPass = -1;

        // The realised GPU resource (imported: given; transient: placed at Compile). May be null
        // for a culled transient.
        public ID3D12Resource Resource;

        // Live tracked state — drives automatic barrier derivation.
        public ResourceStates CurrentState;

        // For RT/DS/UAV transients the FIRST use after aliasing activation must be initialised
        // (clear / DiscardResource / full copy) — MS "initialize after aliasing" rule. The graph
        // sets this when it activates the region and clears it after emitting the discard.
        public bool NeedsAliasInit;

        public bool IsRtDs => !Imported && (Desc.AllowRenderTarget || Desc.AllowDepthStencil);
    }

    readonly List<Entry> entries = new();
    int generation;

    public int Generation => generation;
    public IReadOnlyList<Entry> Entries => entries;
    public int Count => entries.Count;

    // Reset for a new frame's setup. Bumps the generation so handles from a prior frame are stale.
    public void Reset() {
        entries.Clear();
        generation++;
    }

    public Entry Get(in Dx12RgHandle h) {
        if (!h.IsValid) throw new InvalidOperationException("[Dx12Rg] resolved an invalid handle.");
        if (h.Generation != generation)
            throw new InvalidOperationException(
                $"[Dx12Rg] stale handle {h} (registry gen {generation}) — handle outlived its frame's graph Reset().");
        if ((uint)h.Id >= (uint)entries.Count)
            throw new InvalidOperationException($"[Dx12Rg] handle {h} out of range ({entries.Count} resources).");
        return entries[h.Id];
    }

    Dx12RgHandle Add(Entry e, Dx12RgHandleKind kind) {
        e.Id = entries.Count;
        e.Kind = kind;
        entries.Add(e);
        return new Dx12RgHandle(e.Id, generation, kind);
    }

    // --- imports ---------------------------------------------------------------------------------

    public Dx12RgHandle ImportTexture(string name, ID3D12Resource resource, ResourceStates currentState) {
        if (resource is null) throw new ArgumentNullException(nameof(resource));
        return Add(new Entry {
            Name = name, Imported = true, Resource = resource, CurrentState = currentState,
        }, Dx12RgHandleKind.Texture);
    }

    public Dx12RgHandle ImportBuffer(string name, ID3D12Resource resource, ResourceStates currentState) {
        if (resource is null) throw new ArgumentNullException(nameof(resource));
        return Add(new Entry {
            Name = name, Imported = true, Resource = resource, CurrentState = currentState,
        }, Dx12RgHandleKind.Buffer);
    }

    // --- transients ------------------------------------------------------------------------------

    public Dx12RgHandle CreateTransient(in Dx12RgResourceDesc desc) {
        var e = new Entry {
            Name = desc.Name, Imported = false, Desc = desc,
            CurrentState = ResourceStates.Common, // placed resources are created in Common
        };
        return Add(e, desc.IsBuffer ? Dx12RgHandleKind.Buffer : Dx12RgHandleKind.Texture);
    }
}
