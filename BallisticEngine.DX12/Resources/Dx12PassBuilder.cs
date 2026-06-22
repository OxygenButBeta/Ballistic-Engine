namespace BallisticEngine.DX12;

public sealed class Dx12PassBuilder {
    public Dx12GraphResources Resources { get; }

    internal Dx12PassDeclaration Current { get; private set; }

    internal Dx12PassBuilder(Dx12GraphResources resources) {
        Resources = resources;
    }

    internal void Begin(Dx12PassDeclaration decl) => Current = decl;

    public Dx12ResourceHandle Resource(string name, bool imported = true) => Resources.GetOrAdd(name, imported);

    public void Read(Dx12ResourceHandle handle) => Current.Reads.Add(handle.Id);
    public void Write(Dx12ResourceHandle handle) => Current.Writes.Add(handle.Id);

    public void ReadWrite(Dx12ResourceHandle handle) { Read(handle); Write(handle); }

    public void Read(string name, bool imported = true) => Read(Resource(name, imported));
    public void Write(string name, bool imported = true) => Write(Resource(name, imported));
    public void ReadWrite(string name, bool imported = true) => ReadWrite(Resource(name, imported));

    public void Touch(string sharedStateKey) {
        Current.SharedState.Add(sharedStateKey);
        Current.AllowCulling = false;
    }

    public void AllowCulling() => Current.AllowCulling = true;

    public void DeriveBarriers() => Current.BarriersDerived = true;

    public void Use(Dx12ResourceUsage usage) => Current.Usages.Add(usage);
}
