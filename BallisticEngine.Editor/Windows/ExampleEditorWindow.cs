using System.Numerics;

namespace BallisticEngine.Editor;

// A worked EXAMPLE of the user-extensible editor-window API — the exact shape a game developer writes in
// their GameEditorScripts assembly (Assets/Editor/). It demonstrates the whole contract:
//
//   1. Derive from the public EditorWindow base.
//   2. Mark the class with [EditorWindowMeta(...)] — this alone makes it appear under the Window menu,
//      gives it a toggle/checkmark, and a floating window through the shell. No ctor, no registration.
//   3. Fill OnGui(IEditorGui) — draw with the seam ONLY. There is no `using Hexa.NET.ImGui` here, and a
//      game-editor script could not import it anyway (the player never ships ImGui).
//
// This lives in the editor assembly so it also serves as a smoke test that discovery + the Window menu +
// WindowShell standalone draw all work end-to-end. A real game window is identical but lives in
// Assets/Editor/ and is compiled into the editor-only GameEditorScripts.dll.
[EditorWindowMeta("Example Tool", "Window/Example Tool", order: 200, Icon = EditorIcons.Wrench, Width = 360, Height = 280)]
internal sealed class ExampleEditorWindow : EditorWindow {
    int counter;
    float slider = 0.5f;
    string note = "edit me";
    bool toggle;

    // BALLISTIC_EXAMPLE_WINDOW=1 opens it at startup — lets a headless run exercise the discovery + draw
    // path without clicking the Window menu (pure verification door; harmless when unset).
    public ExampleEditorWindow() =>
        Open = Environment.GetEnvironmentVariable("BALLISTIC_EXAMPLE_WINDOW") == "1";

    protected override void OnGui(IEditorGui gui) {
        gui.Text("This window was discovered from [EditorWindowMeta] —");
        gui.TextDisabled("no shell wiring, no raw ImGui, just OnGui(IEditorGui).");
        gui.Separator();
        gui.Spacing();

        if (gui.Button($"Clicked {counter} time(s)"))
            counter++;
        gui.SameLine();
        if (gui.SmallButton("Reset"))
            counter = 0;

        gui.Spacing();
        gui.SetNextItemWidth(-1);
        gui.SliderFloat("Amount", ref slider, 0f, 1f, "%.2f");

        gui.SetNextItemWidth(-1);
        gui.InputText("Note", ref note, 128);

        gui.Checkbox("A toggle", ref toggle);

        gui.Spacing();
        gui.Separator();
        gui.TextWrapped("Everything here routes through the IEditorGui seam, so the same code would run " +
                        "under any backend the editor swaps in, and a recording fake can test it headlessly.");
        gui.Dummy(new Vector2(0, 4 * gui.Scale));
        gui.TextDisabled($"counter={counter}  amount={slider:0.00}  toggle={toggle}");
    }
}
