using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.UI;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.Preview.ComponentPreviewGuiAccess;

namespace BallisticEngine.Editor.Inspector.Preview;

[ComponentPreview(typeof(UIDocument))]
internal sealed class UIDocumentPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var doc = (UIDocument)ctx.Behaviour;
        InspectorPanel panel = ctx.Panel;
        EditorDecoration.DrawSectionHeader("Markup & Style");
        DrawPathDropField(panel, "UXML (markup)", doc.Uxml, [".uxml", ".uihtml", ".html"], p => doc.Uxml = p);
        DrawPathDropField(panel, "USS (style)", doc.Uss, [".uss", ".uicss", ".css"], p => doc.Uss = p);
        gui.TextDisabled("Drag a markup/style asset here, or type its Assets/... path.");
    }

    static void DrawPathDropField(InspectorPanel panel, string label, string current, string[] exts, Action<string> apply) {
        gui.PushId(label);
        gui.TextDisabled(label);
        var s = current ?? "";
        gui.SetNextItemWidth(-1);
        if (gui.InputText("##path", ref s, 256)) {
            EditorCommands.Structural($"Edit {label}", () => { apply(s); panel.MarkViewportDirty(); });
        }

        if (InspectorPanel.AcceptGuidDrop(out Guid guid)) {
            string path = AssetDatabase.GuidToAssetPath(guid);
            if (path is not null && exts.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) {
                EditorCommands.Structural($"Assign {label}", () => { apply(path); panel.MarkViewportDirty(); });
            }
        }
        gui.PopId();
    }
}
