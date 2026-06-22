using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.UI;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.Preview.ComponentPreviewGuiAccess;

namespace BallisticEngine.Editor.Inspector.Preview;

[ComponentPreview(typeof(Volume))]
internal sealed class VolumePreview : IComponentPreview {
    static object volumeUndoBefore;
    static object volumeUndoLastClean;

    public void Draw(in ComponentPreviewContext ctx) {
        var entity = ctx.Entity;
        var volume = (Volume)ctx.Behaviour;
        InspectorPanel panel = ctx.Panel;
        gui.Spacing();

        if (volume.Profile is null) {
            if (gui.Button($"{EditorIcons.Add}  New Profile", new SysVec2(-1, 0)))
                CreateProfileAsset(entity, volume);
            gui.TextDisabled("Creates a .volume asset and assigns it.");
            return;
        }

        EditorDecoration.DrawSectionHeader("Overrides");
        object beforeSnap = VolumeProfileEditor.Snapshot(volume.Profile);
        if (VolumeProfileEditor.Draw(volume.Profile)) {
            VolumeProfileEditor.SaveToAsset(volume.Profile);
            panel.MarkViewportDirty();

            VolumeProfile prof = volume.Profile;
            volumeUndoBefore ??= volumeUndoLastClean;
            volumeUndoBefore ??= beforeSnap;

            if (!gui.IsAnyItemActive()) {
                object before = volumeUndoBefore;
                object after = VolumeProfileEditor.Snapshot(prof);
                EditorCommands.EditAsset("Edit Volume Override",
                    applyOld: () => { VolumeProfileEditor.Restore(prof, before); VolumeProfileEditor.SaveToAsset(prof); panel.MarkViewportDirty(); },
                    applyNew: () => { VolumeProfileEditor.Restore(prof, after); VolumeProfileEditor.SaveToAsset(prof); panel.MarkViewportDirty(); },
                    mutate: () => { });
                volumeUndoBefore = null;
            }
        }
        else if (!gui.IsAnyItemActive()) {
            volumeUndoLastClean = beforeSnap;
            volumeUndoBefore = null;
        }
    }

    static void CreateProfileAsset(Entity entity, Volume volume) {
        var baseName = entity.Name is { Length: > 0 } entityName ? entityName : "Volume";
        string assetPath = null;
        for (var i = 0; i < 100; i++) {
            var candidate = $"Assets/{baseName} Profile{(i == 0 ? "" : $" {i}")}.volume";
            if (!File.Exists(AssetDatabase.Project.ResolveAbsolute(candidate))) {
                assetPath = candidate;
                break;
            }
        }
        if (assetPath is null)
            return;

        VolumeProfileLoader.Save(new VolumeProfile(), AssetDatabase.Project.ResolveAbsolute(assetPath));

        AsyncAssetImport.Request("Importing profile...", onFinished: () => {
            EditorCommands.EditEntity(entity, "Assign Profile",
                () => volume.Profile = AssetDatabase.Load<VolumeProfile>(assetPath));
        });
    }
}
