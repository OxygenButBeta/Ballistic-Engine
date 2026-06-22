namespace BallisticEngine.Editor;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EditorWindowMetaAttribute : Attribute {
    public EditorWindowMetaAttribute(string title, string menuPath = null, int order = 100) {
        Title = title;
        MenuPath = menuPath ?? $"Window/{title}";
        Order = order;
    }

    public string Title { get; }

    public string MenuPath { get; }

    public int Order { get; }

    public string Icon { get; set; }

    public float Width { get; set; } = 420f;
    public float Height { get; set; } = 540f;
}
