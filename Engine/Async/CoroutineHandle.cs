namespace BallisticEngine;

public readonly struct CoroutineHandle {
    public readonly ulong Id;
    public CoroutineHandle(ulong id) => Id = id;
    public bool IsValid => Id != 0;
}
