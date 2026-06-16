using Hexa.NET.ImGui;

namespace BallisticEngine.Editor;

// Backend-neutral seam for the ImGui device renderer. ImGuiController owns one of these, chosen by the
// active render backend (GL or DX12), and drives it identically: create resources once, rebuild the font
// atlas on DPI change, render the draw data each frame. The DX12 implementation records into the editor
// swapchain's open UI command list (see Dx12BallisticEngineWindow / Dx12SwapChain).
internal interface IImGuiRenderer : IDisposable {
    void CreateDeviceResources();
    void RecreateFontTexture();
    void Render(ImDrawDataPtr drawData);
}
