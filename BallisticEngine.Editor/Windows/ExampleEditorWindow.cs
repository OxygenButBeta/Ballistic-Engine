namespace BallisticEngine.Editor;

[EditorWindowMeta("Example Tool", "Window/Example Tool", order: 200, Icon = EditorIcons.Wrench, Width = 360, Height = 280)]
internal sealed class ExampleEditorWindow : EditorWindow {
    int counter;
    float slider = 0.5f;
    string note = "edit me";
    bool toggle;

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
