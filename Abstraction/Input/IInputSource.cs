namespace BallisticEngine.InputSystem;

// The device-reading seam in OUR enums (plan §7.2 "wire the backend later"). The InputComponent
// samples through this; the default impl bridges to the existing Input facade (OpenTK today) via a
// mapping table at the Engine layer. Keeping the seam here (BCL-only, our enums) means swapping OpenTK
// for a DX12-window input later replaces ONE provider, not a single action definition.
//
// All reads must honor the existing Input.Enabled master gate (the editor flips it off outside
// play-with-Game-focused) — the bridge impl forwards that, so events never fire while input is gated
// (§7.2: a bypass here would re-introduce the editor debug-key leak the gate exists to stop).
public interface IInputSource {
    // True while the control is held this frame.
    bool IsKeyDown(Key key);
    bool IsMouseDown(MouseCtrl button);
    bool IsPadButtonDown(PadButton button, int player = 0);

    // Continuous controls.
    System.Numerics.Vector2 MouseDelta { get; }
    float ScrollY { get; }
    System.Numerics.Vector2 PadStick(PadAxis stick, int player = 0);
    float PadTrigger(PadAxis trigger, int player = 0);

    // The master gate (Input.Enabled). When false, the InputComponent treats everything as inactive so
    // no event fires.
    bool Enabled { get; }
}
