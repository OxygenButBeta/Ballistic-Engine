using System.Numerics;

namespace BallisticEngine.Networking;

// The per-tick input captured AS DATA (plan §7.5 — "the one residual nuance"). Prediction needs the
// owner's input per tick to buffer + (P5b) replay; the developer never sees this struct (they only see
// the InputComponent events, §7), it is the prediction system's internal representation. Lives in
// Abstraction (BCL + System.Numerics) like the rest of the wire types, so the engine network tick and
// the source generator can both pack it.
//
// Move = the composed Axis2D (WASD / left stick). Buttons = a bitfield of pressed gameplay actions, one
// bit per action the controller declares (assigned at SetupInput time, stable within a session). Seq =
// the LocalTick this input was sampled on (the monotonic replay index, plan §8.2). Proven byte-for-byte
// in %TEMP%\bal-predict-test before this shipped (the mesh-SDF discipline).
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

    // Pack/unpack exactly as the UP-stream batches it (P5b). Move is bare-float lossless (the P5a
    // default; per-field quantization is an opt-in tuning knob, not a P5a concern). Seq is implicit in
    // the batch header in the real up-frame, but written here so a single input round-trips standalone
    // (the harness + the down-the-wire batch both rely on it).
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
