namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class RangeAttribute : Attribute {
    public float Min { get; }
    public float Max { get; }
    public RangeAttribute(float min, float max) {
        Min = min;
        Max = max;
    }
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class HeaderAttribute : Attribute {
    public string Text { get; }
    public HeaderAttribute(string text) => Text = text;
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class TooltipAttribute : Attribute {
    public string Text { get; }
    public TooltipAttribute(string text) => Text = text;
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class SpaceAttribute : Attribute {
    public float Height { get; }
    public SpaceAttribute(float height = 8f) => Height = height;
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class HideInInspectorAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class ReadOnlyAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class ColorUsageAttribute : Attribute {
    public bool Hdr { get; }
    public ColorUsageAttribute(bool hdr = false) => Hdr = hdr;
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class NotSerializedAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class SerializeFieldAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class ButtonAttribute : Attribute {
    public string Label { get; }
    public ButtonAttribute(string label = null) => Label = label;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ContextMenuAttribute : Attribute {
    public string Label { get; }
    public ContextMenuAttribute(string label = null) => Label = label;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class EditorWindowExecutionPointAttribute : Attribute {
    public string Title { get; }
    public EditorWindowExecutionPointAttribute(string title = null) => Title = title;
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class FoldoutGroupAttribute : Attribute {
    public string Name { get; }
    public bool DefaultOpen { get; }
    public FoldoutGroupAttribute(string name, bool defaultOpen = true) {
        Name = name;
        DefaultOpen = defaultOpen;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class MenuItemAttribute : Attribute {
    public string Path { get; }
    public int Order { get; }
    public MenuItemAttribute(string path, int order = 0) {
        Path = path;
        Order = order;
    }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ComponentPreviewAttribute : Attribute {
    public Type TargetType { get; }
    public int Priority { get; }
    public ComponentPreviewAttribute(Type targetType, int priority = 0) {
        TargetType = targetType;
        Priority = priority;
    }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class AssetInspectorAttribute : Attribute {
    public string Extension { get; }
    public int Priority { get; }
    public AssetInspectorAttribute(string extension, int priority = 0) {
        extension = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (extension.Length > 0 && extension[0] != '.')
            extension = "." + extension;
        Extension = extension;
        Priority = priority;
    }
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CreateDataAssetAttribute : Attribute {
    public string Menu { get; }
    public string FileName { get; }
    public string DisplayName { get; }
    public CreateDataAssetAttribute(string menu = "", string fileName = "New Data Asset",
        string displayName = null) {
        Menu = menu;
        FileName = fileName;
        DisplayName = displayName;
    }
}
