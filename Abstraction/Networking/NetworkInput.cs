namespace BallisticEngine.Networking;

public readonly struct NetworkInput {
    public readonly uint Seq;
    public readonly float MoveX;
    public readonly float MoveY;
    public readonly uint Buttons;

    public NetworkInput(uint seq, Vector2 move, uint buttons) {
        Seq = seq; MoveX = move.X; MoveY = move.Y; Buttons = buttons;
    }

    public NetworkInput(uint seq, float moveX, float moveY, uint buttons) {
        Seq = seq; MoveX = moveX; MoveY = moveY; Buttons = buttons;
    }

    public Vector2 Move => new(MoveX, MoveY);
    public bool Button(int bit) => (Buttons & (1u << bit)) != 0;

    public bool IsEmpty => MoveX == 0f && MoveY == 0f && Buttons == 0u;

    public void Write(BitWriter writer) {
        writer.WriteUInt(Seq);
        writer.WriteFloat(MoveX);
        writer.WriteFloat(MoveY);
        writer.WriteUInt(Buttons);
    }

    public static NetworkInput Read(ref BitReader reader) {
        uint seq = reader.ReadUInt();
        float mx = reader.ReadFloat();
        float my = reader.ReadFloat();
        uint b = reader.ReadUInt();
        return new NetworkInput(seq, mx, my, b);
    }
}
