
namespace BallisticEngine;

public interface IRibbonSource {
    bool IsActive { get; }
    bool RibbonRenderable { get; }
    RibbonBlendMode BlendMode { get; }
    Texture2D RibbonTexture { get; }

    int BuildRibbon(Vector3 cameraPos, out RibbonVertex[] vertices);
}
