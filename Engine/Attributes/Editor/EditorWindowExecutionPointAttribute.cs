namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class EditorWindowExecutionPointAttribute : Attribute {
    public string Title { get; }
    public EditorWindowExecutionPointAttribute(string title = null) => Title = title;
}
