namespace BallisticEngine;

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
