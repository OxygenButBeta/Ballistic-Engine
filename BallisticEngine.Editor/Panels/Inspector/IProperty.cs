namespace BallisticEngine.Editor.Inspector;

public interface IProperty {
    string Name { get; }
    string Label { get; }
    string Tooltip { get; }
    Type ValueType { get; }
    object Owner { get; }

    object Get();
    void Set(object value);

    MemberAttributes Attributes { get; }

    (float min, float max)? Range { get; }
    bool IsColor { get; }
    bool Hdr { get; }

    bool HasOverrideToggle { get; }
    bool Overridden { get; set; }

    bool TryGetSiblingValue(string memberName, out object value);
}
