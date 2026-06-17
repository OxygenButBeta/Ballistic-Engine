namespace BallisticEngine.Editor.Inspector.Preview;

// What a component preview (B1) gets to draw with: the entity + behaviour being inspected and the owning
// InspectorPanel (for the section methods that still live there + its EditorState). Passed by `in` so the
// per-frame dispatch loop allocates nothing. The previews are thin shims that delegate back into the panel's
// internal DrawXxxSection methods, so the rendering stays byte-identical to the pre-B1 inline chain — the
// context is just the plumbing that lets a registry-resolved preview reach those instance helpers.
internal readonly struct ComponentPreviewContext {
    public ComponentPreviewContext(InspectorPanel panel, Entity entity, Behaviour behaviour) {
        Panel = panel;
        Entity = entity;
        Behaviour = behaviour;
    }

    public InspectorPanel Panel { get; }
    public Entity Entity { get; }
    public Behaviour Behaviour { get; }
}
