using Hexa.NET.ImGui;

namespace BallisticEngine.Editor;

internal interface IImGuiRenderer : IDisposable {
    void CreateDeviceResources();
    void RecreateFontTexture();
    void Render(ImDrawDataPtr drawData);
}
