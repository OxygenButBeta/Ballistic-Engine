using ImGuiNET;
using OpenTK.Windowing.Desktop;

namespace BallisticEngine.Editor;

// Owns the ImGui context and device resources; call Update() before building UI and Render()
// after. Bridges OpenTK input. (Docking is not enabled: the stock ImGui.NET cimgui binary is
// built without it; panels are free-floating windows for now.)
internal sealed class ImGuiController : IDisposable {
    readonly GameWindow window;
    readonly ImGuiGLRenderer renderer = new();
    readonly IntPtr context;
    bool frameBegun;

    public ImGuiController(GameWindow window) {
        this.window = window;

        context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);

        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.Fonts.AddFontDefault();

        renderer.CreateDeviceResources();

        window.TextInput += e => ImGuiInput.OnTextInput((uint)e.Unicode);

        SetDarkStyle();
    }

    public void WindowResized(int width, int height) {
        ImGui.GetIO().DisplaySize = new System.Numerics.Vector2(width, height);
    }

    public void Update(float deltaSeconds) {
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new System.Numerics.Vector2(window.ClientSize.X, window.ClientSize.Y);
        io.DisplayFramebufferScale = System.Numerics.Vector2.One;
        io.DeltaTime = deltaSeconds > 0 ? deltaSeconds : 1f / 60f;

        ImGuiInput.Update(window);

        frameBegun = true;
        ImGui.NewFrame();
    }

    public void Render() {
        if (!frameBegun)
            return;

        frameBegun = false;
        ImGui.Render();
        renderer.Render(ImGui.GetDrawData());
    }

    public bool WantCaptureMouse => ImGui.GetIO().WantCaptureMouse;
    public bool WantCaptureKeyboard => ImGui.GetIO().WantCaptureKeyboard;

    static void SetDarkStyle() {
        ImGui.StyleColorsDark();
        ImGuiStylePtr style = ImGui.GetStyle();
        style.WindowRounding = 4f;
        style.FrameRounding = 3f;
        style.GrabRounding = 3f;
    }

    public void Dispose() {
        renderer.Dispose();
        ImGui.DestroyContext(context);
    }
}
