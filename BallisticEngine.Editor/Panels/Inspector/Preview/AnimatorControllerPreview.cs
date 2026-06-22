using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.UI;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.Preview.ComponentPreviewGuiAccess;

namespace BallisticEngine.Editor.Inspector.Preview;

[ComponentPreview(typeof(AnimatorController))]
internal sealed class AnimatorControllerPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var controller = (AnimatorController)ctx.Behaviour;
        EditorDecoration.DrawSectionHeader("State Machine");

        if (controller.StateCount == 0) {
            gui.TextDisabled("No states. Build the graph in a script's OnBegin:");
            gui.TextDisabled("  AddState(name, clip); state.To(target, param, Compare, ...)");
            return;
        }

        if (!SceneManager.IsPlaying)
            gui.TextDisabled("Enter play mode to drive the graph.");

        string cur = controller.CurrentStateName ?? "(none)";
        gui.Text("Current: ");
        gui.SameLine();
        gui.TextColored(EditorTheme.Info, cur);

        gui.Spacing();
        gui.TextDisabled($"States ({controller.StateCount})");
        foreach (AnimatorController.State s in controller.States) {
            bool isCurrent = s.Name == controller.CurrentStateName;
            string label = $"{(isCurrent ? EditorIcons.Play + " " : "   ")}{s.Name}";
            string clipName = s.Clip is not null ? s.Clip.Name : "(no clip)";
            if (isCurrent)
                gui.TextColored(EditorTheme.Info, $"{label}  ->  {clipName}");
            else
                gui.TextDisabled($"{label}  ->  {clipName}");
            if (SceneManager.IsPlaying && gui.IsItemClicked())
                controller.Play(s.Name);
        }

        var prms = controller.Parameters;
        if (prms.Count > 0) {
            EditorDecoration.DrawSectionHeader("Parameters");
            foreach (var kv in prms) {
                string name = kv.Key;
                switch (kv.Value) {
                    case AnimatorController.ParamKind.Bool: {
                        bool b = controller.GetBool(name);
                        if (gui.Checkbox(name, ref b)) controller.SetBool(name, b);
                        break;
                    }
                    case AnimatorController.ParamKind.Trigger: {
                        if (gui.Button($"{EditorIcons.Play} {name}", new SysVec2(140, 0)))
                            controller.SetTrigger(name);
                        gui.SameLine();
                        gui.TextDisabled(controller.GetTrigger(name) ? "(set)" : "");
                        break;
                    }
                    case AnimatorController.ParamKind.Int: {
                        int iv = controller.GetInt(name);
                        if (gui.DragInt(name, ref iv)) controller.SetInt(name, iv);
                        break;
                    }
                    default: {
                        float fv = controller.GetFloat(name);
                        if (gui.DragFloat(name, ref fv, 0.05f)) controller.SetFloat(name, fv);
                        break;
                    }
                }
            }
        }

        if (SceneManager.IsPlaying)
            ctx.Panel.MarkViewportDirty();
    }
}
