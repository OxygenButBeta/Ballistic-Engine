using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.UI;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.Preview.ComponentPreviewGuiAccess;

namespace BallisticEngine.Editor.Inspector.Preview;

[ComponentPreview(typeof(Health))]
internal sealed class HealthPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var health = (Health)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("Health");

        float frac = health.HealthFraction;
        IEditorDrawList draw = gui.WindowDrawList;
        SysVec2 p = gui.CursorScreenPos;
        float w = MathF.Max(gui.ContentRegionAvail.X, 60f);
        const float h = 18f;
        draw.AddRectFilled(p, p + new SysVec2(w, h), 0xFF202428, 3f);
        var barCol = gui.ColorU32(new SysVec4(1f - frac, frac, 0.12f, 1f));
        if (frac > 0f)
            draw.AddRectFilled(p, p + new SysVec2(w * frac, h), barCol, 3f);
        draw.AddRect(p, p + new SysVec2(w, h), 0xFF000000, 3f);
        string label = health.IsDead ? "DEAD" : $"{health.CurrentHealth:0} / {health.MaxHealth:0}";
        SysVec2 ts = gui.CalcTextSize(label);
        draw.AddText(p + new SysVec2((w - ts.X) * 0.5f, (h - ts.Y) * 0.5f), 0xFFFFFFFF, label);
        gui.Dummy(new SysVec2(w, h));

        if (gui.Button("Damage 10", new SysVec2(90, 0))) { health.TakeDamage(10f); ctx.Panel.MarkViewportDirty(); }
        gui.SameLine();
        if (gui.Button("Heal 10", new SysVec2(90, 0))) { health.Heal(10f); ctx.Panel.MarkViewportDirty(); }
        gui.SameLine();
        if (gui.Button("Kill", new SysVec2(70, 0))) { health.Kill(); ctx.Panel.MarkViewportDirty(); }
        gui.SameLine();
        if (gui.Button("Revive", new SysVec2(70, 0))) { health.Revive(); ctx.Panel.MarkViewportDirty(); }

        if (!SceneManager.IsPlaying)
            gui.TextDisabled("Edit-mode tests don't fire DestroyOnDeath (play only).");
    }
}
