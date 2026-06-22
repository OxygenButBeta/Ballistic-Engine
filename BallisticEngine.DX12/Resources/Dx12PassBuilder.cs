namespace BallisticEngine.DX12;

public enum Dx12ResourceUsage {
    None = 0,
    GBufferShaderRead,
    GBufferDepthShaderRead,
    GBufferDepthReadOnly,
    SceneColorShaderRead,
}

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

public sealed class Dx12PassDeclaration {
    public readonly HashSet<int> Reads = new();
    public readonly HashSet<int> Writes = new();
    public readonly HashSet<string> SharedState = new();
    public bool AllowCulling;
    public bool Declared;

    public readonly List<Dx12ResourceUsage> Usages = new();
    public bool BarriersDerived;

    public bool IsOpaque => !Declared;
}

public sealed class Dx12GraphResources {
    readonly Dictionary<string, Dx12ResourceHandle> byName = new();
    readonly List<Dx12ResourceHandle> all = new();

    public Dx12ResourceHandle GetOrAdd(string name, bool imported) {
        if (byName.TryGetValue(name, out var existing)) return existing;
        var h = new Dx12ResourceHandle(all.Count, name, imported);
        byName[name] = h;
        all.Add(h);
        return h;
    }

    public IReadOnlyList<Dx12ResourceHandle> All => all;
    public int Count => all.Count;
    public Dx12ResourceHandle ById(int id) => all[id];
}
