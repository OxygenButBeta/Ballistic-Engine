namespace BallisticEngine.DX12;

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
