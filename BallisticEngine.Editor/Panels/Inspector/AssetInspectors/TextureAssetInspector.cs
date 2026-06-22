using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.AssetInspectors.AssetInspectorGuiAccess;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

[AssetInspector(".png")]
[AssetInspector(".jpg")]
[AssetInspector(".jpeg")]
[AssetInspector(".tga")]
[AssetInspector(".bmp")]
[AssetInspector(".hdr")]
[AssetInspector(".exr")]
internal sealed class TextureAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawTextureImportSettings(ctx.Path, ctx.Guid, ctx.Meta);

    static void DrawTextureImportSettings(string path, Guid guid, MetaFile meta) {
        if (meta is null) {
            gui.TextDisabled("No import settings.");
            return;
        }

        if (InspectorPanel.BeginGrid("##texsettings")) {
            InspectorPanel.Row("Texture Type");
            TextureType current = TextureImporter.TypeFromSettings(meta.Settings);
            string[] names = Enum.GetNames<TextureType>();
            int index = Array.IndexOf(names, current.ToString());
            gui.SetNextItemWidth(-1);
            if (gui.Combo("##textype", ref index, names)) {
                meta.Settings["textureType"] = names[index];
                meta.Save(MetaFile.PathFor(AssetDatabase.Project.ResolveAbsolute(path)));
                Guid reimported = guid;
                AsyncAssetImport.Request("Reimporting texture...",
                    onFinished: () => AssetDatabase.Invalidate(reimported));
            }
            gui.EndTable();
        }

        gui.Spacing();
        gui.TextDisabled("Changing the type reimports. Loaded materials keep the\nold instance until the scene reloads.");
    }
}
