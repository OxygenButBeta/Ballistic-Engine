namespace BallisticEngine.Editor.Inspector;

public sealed class CollectionElementProperty : IProperty {
    readonly Func<object> get;
    readonly Action<object> assign;

    public CollectionElementProperty(string name, Type elementType, Func<object> get, Action<object> assign) {
        Name = name;
        ValueType = elementType;
        this.get = get;
        this.assign = assign;
    }

    public string Name { get; }
    public string Label => Name;
    public string Tooltip => null;
    public Type ValueType { get; }
    public object Owner => null;

    public object Get() => get();
    public void Set(object value) => assign(value);

    public MemberAttributes Attributes => MemberAttributes.None;
    public (float min, float max)? Range => null;
    public bool IsColor => false;
    public bool Hdr => false;
    public bool HasOverrideToggle => false;
    public bool Overridden { get => false; set { } }
    public bool TryGetSiblingValue(string memberName, out object value) { value = null; return false; }
}
