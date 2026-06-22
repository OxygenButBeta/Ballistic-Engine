namespace BallisticEngine;

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
