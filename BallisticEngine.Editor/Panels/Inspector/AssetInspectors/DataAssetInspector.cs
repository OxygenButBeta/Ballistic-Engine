using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.AssetInspectors.AssetInspectorGuiAccess;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

[AssetInspector(".asset")]
internal sealed class DataAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawDataAssetInspector(ctx.Panel, ctx.Path);

    string dataAssetPath;
    object dataAssetInstance;

    void DrawDataAssetInspector(InspectorPanel panel, string path) {
        if (dataAssetPath != path || dataAssetInstance is null) {
            dataAssetPath = path;
            dataAssetInstance = LoadDataAsset(path);
        }
        if (dataAssetInstance is not DataAsset asset) {
            gui.TextDisabled("Could not load data asset (unknown or renamed type?).");
            return;
        }

        string before = DataAssetSerializer.Serialize(asset);
        panel.DrawMemberList(asset.GetType(), asset);
        string after = DataAssetSerializer.Serialize(asset);
        if (before != after)
            SaveDataAsset(path, asset);
    }

    static object LoadDataAsset(string path) {
        try { return AssetDatabase.Load<DataAsset>(path); }
        catch { return null; }
    }

    static void SaveDataAsset(string path, DataAsset instance) {
        try {
            File.WriteAllText(AssetDatabase.Project.ResolveAbsolute(path),
                DataAssetSerializer.Serialize(instance));
        }
        catch (Exception e) {
            Debugging.LogError($"Could not save data asset: {e.Message}");
        }
    }
}
