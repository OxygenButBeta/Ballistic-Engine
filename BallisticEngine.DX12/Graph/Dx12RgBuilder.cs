namespace BallisticEngine.DX12;

// FAZ -1 — Render-graph v2. Setup-phase builder a pass uses to declare itself.
//
// Handed to a pass's setup lambda. The pass declares the resources it touches and the state it
// wants them in; it may also create transients and import externals here. Read/Write append to the
// current pass's access lists; CreateTransient/Import mint handles on the shared registry. Mirrors
// UE-RDG's FParameters population and the old Dx12PassBuilder.Read/Write/CreateTransient surface so
// migration is a near drop-in.

public sealed class Dx12RgBuilder {
    readonly Dx12RgResourceRegistry registry;
    readonly Dx12RgPass pass;

    internal Dx12RgBuilder(Dx12RgResourceRegistry registry, Dx12RgPass pass) {
        this.registry = registry;
        this.pass = pass;
    }

    // --- access declarations --------------------------------------------------------------------

    public Dx12RgHandle Read(Dx12RgHandle handle, Dx12RgResourceState state = Dx12RgResourceState.ShaderRead) {
        Validate(handle);
        pass.DeclareRead(handle, state);
        return handle;
    }

    public Dx12RgHandle Write(Dx12RgHandle handle, Dx12RgResourceState state) {
        Validate(handle);
        pass.DeclareWrite(handle, state);
        return handle;
    }

    // ReadWrite (UAV ping in-place): declared as BOTH a read and a write of the same handle so the
    // DAG keeps prior producers AND the resource stays live; the state is the same for both halves
    // (typically UnorderedAccess). Emits a UAV barrier path in the deriver between consecutive RWs.
    public Dx12RgHandle ReadWrite(Dx12RgHandle handle, Dx12RgResourceState state = Dx12RgResourceState.UnorderedAccess) {
        Validate(handle);
        pass.DeclareRead(handle, state);
        pass.DeclareWrite(handle, state);
        return handle;
    }

    // --- resource creation / import -------------------------------------------------------------

    public Dx12RgHandle CreateTransient(in Dx12RgResourceDesc desc) {
        var h = registry.CreateTransient(desc);
        pass.Created.Add(h);
        return h;
    }

    public Dx12RgHandle ImportTexture(string name, Vortice.Direct3D12.ID3D12Resource res, Vortice.Direct3D12.ResourceStates state)
        => registry.ImportTexture(name, res, state);

    public Dx12RgHandle ImportBuffer(string name, Vortice.Direct3D12.ID3D12Resource res, Vortice.Direct3D12.ResourceStates state)
        => registry.ImportBuffer(name, res, state);

    // --- pass attributes ------------------------------------------------------------------------

    public Dx12RgBuilder NeverCull() { pass.NeverCull = true; return this; }

    void Validate(in Dx12RgHandle h) {
        if (!h.IsValid) throw new InvalidOperationException($"[Dx12Rg] pass '{pass.Name}' declared an invalid handle.");
        // Get() throws on stale/out-of-range — fail loud at setup, not at execute.
        registry.Get(h);
    }
}
