namespace BallisticEngine.InputSystem;

public interface IInputSource {
    bool IsKeyDown(Key key);
    bool IsMouseDown(MouseCtrl button);
    bool IsPadButtonDown(PadButton button, int player = 0);

    System.Numerics.Vector2 MouseDelta { get; }
    float ScrollY { get; }
    System.Numerics.Vector2 PadStick(PadAxis stick, int player = 0);
    float PadTrigger(PadAxis trigger, int player = 0);

    bool Enabled { get; }
}
