namespace BallisticEngine.DX12;

public readonly struct Dx12ResourceHandle {
    public readonly int Id;
    public readonly string Name;
    public readonly bool Imported;

    public Dx12ResourceHandle(int id, string name, bool imported) {
        Id = id; Name = name; Imported = imported;
    }

    public bool IsValid => Id >= 0 && Name is not null;

    public override string ToString() => $"#{Id}:{Name}{(Imported ? "(imported)" : "")}";
}
