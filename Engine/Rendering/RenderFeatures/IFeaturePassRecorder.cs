namespace BallisticEngine;

public interface IFeaturePassRecorder {
    string SceneColor { get; }

    void SetRenderTarget(string handleName);

    void BlitFullscreen(string sourceHandle, string destHandle, string shaderOrMaterial = null);
}
