namespace BallisticEngine;

public abstract class RenderFeature {
    public bool Active { get; set; } = true;

    public virtual RenderPassEvent Event => RenderPassEvent.PostProcess;

    public virtual void Declare(IFeatureIOBuilder io) {
    }

    public abstract void Record(IFeaturePassRecorder recorder);
}
