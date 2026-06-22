using Hexa.NET.ImGui;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine.Editor;

internal static class ImGuiInput {
    public static void Update(GameWindow window) {
        ImGuiIOPtr io = ImGui.GetIO();

        MouseState mouse = window.MouseState;
        io.AddMousePosEvent(mouse.X, mouse.Y);
        io.AddMouseButtonEvent(0, mouse[MouseButton.Left]);
        io.AddMouseButtonEvent(1, mouse[MouseButton.Right]);
        io.AddMouseButtonEvent(2, mouse[MouseButton.Middle]);
        io.AddMouseWheelEvent(mouse.ScrollDelta.X, mouse.ScrollDelta.Y);

        KeyboardState kb = window.KeyboardState;
        io.AddKeyEvent(ImGuiKey.ModCtrl, kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl));
        io.AddKeyEvent(ImGuiKey.ModShift, kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift));
        io.AddKeyEvent(ImGuiKey.ModAlt, kb.IsKeyDown(Keys.LeftAlt) || kb.IsKeyDown(Keys.RightAlt));

        foreach ((Keys key, ImGuiKey imguiKey) in KeyMap)
            io.AddKeyEvent(imguiKey, kb.IsKeyDown(key));
    }

    public static void OnTextInput(uint unicode) => ImGui.GetIO().AddInputCharacter(unicode);

    static readonly (Keys, ImGuiKey)[] KeyMap = BuildKeyMap();

    static (Keys, ImGuiKey)[] BuildKeyMap() {
        var list = new List<(Keys, ImGuiKey)> {
            (Keys.Tab, ImGuiKey.Tab),
            (Keys.Left, ImGuiKey.LeftArrow), (Keys.Right, ImGuiKey.RightArrow),
            (Keys.Up, ImGuiKey.UpArrow), (Keys.Down, ImGuiKey.DownArrow),
            (Keys.PageUp, ImGuiKey.PageUp), (Keys.PageDown, ImGuiKey.PageDown),
            (Keys.Home, ImGuiKey.Home), (Keys.End, ImGuiKey.End),
            (Keys.Insert, ImGuiKey.Insert), (Keys.Delete, ImGuiKey.Delete),
            (Keys.Backspace, ImGuiKey.Backspace), (Keys.Space, ImGuiKey.Space),
            (Keys.Enter, ImGuiKey.Enter), (Keys.Escape, ImGuiKey.Escape),
            (Keys.LeftControl, ImGuiKey.LeftCtrl), (Keys.RightControl, ImGuiKey.RightCtrl),
            (Keys.LeftShift, ImGuiKey.LeftShift), (Keys.RightShift, ImGuiKey.RightShift),
            (Keys.LeftAlt, ImGuiKey.LeftAlt), (Keys.RightAlt, ImGuiKey.RightAlt),
        };

        for (Keys k = Keys.A; k <= Keys.Z; k++)
            list.Add((k, ImGuiKey.A + (k - Keys.A)));
        for (Keys k = Keys.D0; k <= Keys.D9; k++)
            list.Add((k, ImGuiKey.Key0 + (k - Keys.D0)));

        return list.ToArray();
    }
}
