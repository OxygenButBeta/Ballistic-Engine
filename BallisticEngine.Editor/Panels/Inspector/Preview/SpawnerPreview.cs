using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.UI;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.Preview.ComponentPreviewGuiAccess;

namespace BallisticEngine.Editor.Inspector.Preview;

[ComponentPreview(typeof(Spawner))]
internal sealed class SpawnerPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var spawner = (Spawner)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("Spawner");

        if (spawner.Prefab is null) {
            gui.TextColored(EditorTheme.Warning, "Assign a Prefab to spawn.");
            return;
        }

        gui.Text($"Alive: {spawner.AliveCount} / {spawner.MaxAlive}");
        gui.SameLine();
        gui.TextDisabled($"(pooled: {spawner.PooledCount})");

        if (gui.Button($"{EditorIcons.Play}  Spawn One", new SysVec2(120, 0))) {
            spawner.Spawn();
            ctx.Panel.MarkViewportDirty();
        }
        gui.SameLine();
        if (gui.Button($"{EditorIcons.Refresh}  Clear", new SysVec2(120, 0))) {
            spawner.Clear();
            ctx.Panel.MarkViewportDirty();
        }

        if (SceneManager.IsPlaying && spawner.AliveCount > 0)
            ctx.Panel.MarkViewportDirty();
    }
}
