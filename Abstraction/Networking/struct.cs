namespace BallisticEngine.Networking;

public readonly record struct Connection(int Id) {
    public static readonly Connection Local = new(0);
    public static readonly Connection None = new(-1);
    public bool IsValid => Id >= 0;
    public bool IsLocal => Id == 0;
    public override string ToString() => IsLocal ? "Connection(local)" : $"Connection({Id})";
}
