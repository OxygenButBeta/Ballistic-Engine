namespace BallisticEngine;

// Scene-wide container for the authored render-feature list (phase 3 / design §5 D2). A SceneBehaviour,
// NOT an entity component — render features are a renderer/scene-wide concern (like Skybox /
// SceneLighting), authored once per scene and read per frame by the backend. Lives in the editor's
// "Scene" hierarchy; the renderer reads the active instance's ordered feature list every frame, exactly
// as it reads Skybox.Active.
//
// The list is an ORDERED LIST, not a set (design §5 D1): URP lets the same feature type be added
// multiple times (e.g. two blur passes at different events), so duplicates are allowed and registration
// order is the stable tiebreak among same-event features. Serialization of the list (chunk 21) and the
// reorderable editor widget (chunk 22) come later; this chunk only establishes the carrier so the seam
// + discovery exist.
//
// PIXEL-NEUTRAL DEFAULT: a scene with NO RenderFeatures behaviour (or an empty list) is byte-identical to
// today — RenderFeatureManager early-outs on empty, so the golden scenes (which carry none) are untouched.
public class RenderFeatures : SceneBehaviour {
    public static RenderFeatures Active { get; private set; }

    // The authored features, in injection/registration order. Plain RenderFeature instances — they
    // serialize through ComponentReflection / the scene YAML by type-name + members (chunk 21), the same
    // path a Behaviour list uses. Public for the editor list widget (chunk 22); never null.
    //
    // [HideInInspector] hides it from the GENERIC reflected member list (a List<abstractType> has no
    // sensible default drawer) — the editor renders it with the dedicated reorderable feature-list widget
    // (chunk 22) instead. Serialization is UNAFFECTED: the scene serializer drives off SerializableMembers
    // (which ignores [HideInInspector]) + the IsRenderFeatureList element-type path (chunk 21).
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
