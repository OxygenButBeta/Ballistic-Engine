namespace BallisticEngine.Networking;

[Flags]
public enum NetworkAuthority {
    None = 0,
    State = 1 << 0,
    Input = 1 << 1,
    Both = State | Input,
}

public enum NetworkTopology {
    Offline,
    Server,
    Client,
    Host,
}
