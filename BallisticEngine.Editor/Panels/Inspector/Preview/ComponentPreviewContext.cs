namespace BallisticEngine.Editor.Inspector.Preview;

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
