using BallisticEngine.AssetPipeline;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

internal readonly struct AssetInspectorContext {
    public AssetInspectorContext(InspectorPanel panel, string path, System.Guid guid, string extension,
        MetaFile meta) {
        Panel = panel;
        Path = path;
        Guid = guid;
        Extension = extension;
        Meta = meta;
    }

    public InspectorPanel Panel { get; }
    public string Path { get; }
    public System.Guid Guid { get; }
    public string Extension { get; }
    public MetaFile Meta { get; }
}
