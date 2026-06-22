namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class MenuItemAttribute : Attribute {
    public string Path { get; }
    public int Order { get; }
    public MenuItemAttribute(string path, int order = 0) {
        Path = path;
        Order = order;
    }
}
