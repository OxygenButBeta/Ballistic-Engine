using BallisticEngine.AssetPipeline;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

// What an asset inspector (B2) gets to draw with: the selected asset's path / guid / extension / meta and
// the owning InspectorPanel (for the section methods that still live there). Passed by `in` so the per-draw
// dispatch allocates nothing. The inspectors are thin shims that delegate back into the panel's internal
// DrawXxx methods, so the rendering stays byte-identical to the pre-B2 inline switch — the context is just the
// plumbing that lets a registry-resolved inspector reach those instance helpers, exactly like B1's
// ComponentPreviewContext does for component preview sections.
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
    public string Extension { get; }   // lower-case with the leading dot (".mat"), matches the [AssetInspector] key
    public MetaFile Meta { get; }
}
