namespace BallisticEngine.DX12;

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
