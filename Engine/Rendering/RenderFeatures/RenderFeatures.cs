namespace BallisticEngine;

public class RenderFeatures : SceneBehaviour {
    public static RenderFeatures Active { get; private set; }

    [HideInInspector]
    public List<RenderFeature> Features { get; set; } = new();

    protected internal override void OnAttach() {
        Active = this;
    }

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }
}
