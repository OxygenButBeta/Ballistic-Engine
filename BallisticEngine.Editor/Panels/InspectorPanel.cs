using System.Reflection;
using BallisticEngine.AssetPipeline;
using BallisticEngine.Serialization;
using ImGuiNET;
using OpenTK.Mathematics;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Edits the selected entity: name, active, transform, and every component's reflected members.
// Asset-reference members show as a read-only slot with a drag-drop target (guid payload).
internal sealed class InspectorPanel {
    readonly EditorState state;

    public InspectorPanel(EditorState state) => this.state = state;

    public void DrawContents() {
        Entity entity = state.Selected;
        if (entity is null) {
            ImGui.TextDisabled("No entity selected.");
            return;
        }

        DrawEntityHeader(entity);
        ImGui.Separator();
        DrawTransform(entity.transform);

        foreach (Behaviour behaviour in entity.Behaviours.ToArray())
            DrawComponent(entity, behaviour);

        ImGui.Separator();
        DrawAddComponent(entity);
    }

    static void DrawEntityHeader(Entity entity) {
        var name = entity.Name ?? "";
        if (ImGui.InputText("Name", ref name, 128))
            entity.Name = name;

        bool active = entity.IsActive;
        if (ImGui.Checkbox("Active", ref active))
            entity.SetActive(active);
    }

    static void DrawTransform(Transform transform) {
        if (!ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        SysVec3 pos = ToSys(transform.Position);
        if (ImGui.DragFloat3("Position", ref pos, 0.05f))
            transform.Position = ToTk(pos);

        SysVec3 euler = ToSys(transform.EulerAngles);
        if (ImGui.DragFloat3("Rotation", ref euler, 0.5f))
            transform.EulerAngles = ToTk(euler);

        SysVec3 scale = ToSys(transform.Scale);
        if (ImGui.DragFloat3("Scale", ref scale, 0.05f))
            transform.Scale = ToTk(scale);
    }

    void DrawComponent(Entity entity, Behaviour behaviour) {
        Type type = behaviour.GetType();
        bool open = ImGui.CollapsingHeader(type.Name, ImGuiTreeNodeFlags.DefaultOpen);

        // Right-click the header to remove the component.
        if (ImGui.BeginPopupContextItem($"ctx_{type.Name}_{behaviour.InstanceId}")) {
            if (ImGui.MenuItem("Remove Component")) {
                entity.RemoveComponent(behaviour);
                ImGui.EndPopup();
                return;
            }
            ImGui.EndPopup();
        }

        if (!open)
            return;

        ImGui.PushID(behaviour.InstanceId.GetHashCode());

        bool enabled = behaviour.IsEnabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
            behaviour.IsEnabled = enabled;

        foreach (MemberInfo member in ComponentReflection.SerializableMembers(type))
            DrawMember(member, behaviour);

        ImGui.PopID();
    }

    static void DrawMember(MemberInfo member, object target) {
        Type memberType = ComponentReflection.MemberType(member);
        object value = ComponentReflection.GetValue(member, target);
        string label = member.Name;

        if (typeof(BObject).IsAssignableFrom(memberType)) {
            DrawAssetSlot(member, target, label, value as BObject, memberType);
            return;
        }

        switch (value) {
            case float f: {
                if (ImGui.DragFloat(label, ref f, 0.05f)) ComponentReflection.SetValue(member, target, f);
                break;
            }
            case int i: {
                if (ImGui.DragInt(label, ref i)) ComponentReflection.SetValue(member, target, i);
                break;
            }
            case bool b: {
                if (ImGui.Checkbox(label, ref b)) ComponentReflection.SetValue(member, target, b);
                break;
            }
            case string s: {
                var str = s ?? "";
                if (ImGui.InputText(label, ref str, 256)) ComponentReflection.SetValue(member, target, str);
                break;
            }
            case Vector3 v3: {
                SysVec3 sv = ToSys(v3);
                if (ImGui.DragFloat3(label, ref sv, 0.05f)) ComponentReflection.SetValue(member, target, ToTk(sv));
                break;
            }
            case Vector2 v2: {
                var sv = new SysVec2(v2.X, v2.Y);
                if (ImGui.DragFloat2(label, ref sv, 0.05f))
                    ComponentReflection.SetValue(member, target, new Vector2(sv.X, sv.Y));
                break;
            }
            case Enum e: {
                DrawEnum(member, target, label, e, memberType);
                break;
            }
            default:
                ImGui.TextDisabled($"{label}: ({memberType.Name})");
                break;
        }
    }

    static void DrawEnum(MemberInfo member, object target, string label, Enum value, Type enumType) {
        string[] names = Enum.GetNames(enumType);
        int current = Array.IndexOf(names, value.ToString());
        if (ImGui.Combo(label, ref current, names, names.Length))
            ComponentReflection.SetValue(member, target, Enum.Parse(enumType, names[current]));
    }

    static void DrawAssetSlot(MemberInfo member, object target, string label, BObject asset, Type assetType) {
        string display = asset is null
            ? "(none)"
            : AssetDatabase.TryGetAssetGuid(asset, out Guid g)
                ? Path.GetFileName(AssetDatabase.GuidToAssetPath(g))
                : asset.GetType().Name;

        ImGui.Button($"{display}##{label}", new SysVec2(-1, 0));
        AcceptAssetDrop(member, target, assetType);
        ImGui.SameLine();
        ImGui.Text(label);
    }

    static unsafe void AcceptAssetDrop(MemberInfo member, object target, Type assetType) {
        if (!ImGui.BeginDragDropTarget())
            return;

        ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(AssetBrowserPanel.DragType);
        if (payload.NativePtr != null && payload.Data != IntPtr.Zero) {
            var guidText = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(payload.Data, payload.DataSize);
            if (Guid.TryParse(guidText, out Guid guid)) {
                MethodInfo load = typeof(AssetDatabase).GetMethod(nameof(AssetDatabase.Load), [typeof(Guid)])!
                    .MakeGenericMethod(assetType);
                object loaded = load.Invoke(null, [guid]);
                if (loaded is not null)
                    ComponentReflection.SetValue(member, target, loaded);
            }
        }

        ImGui.EndDragDropTarget();
    }

    static void DrawAddComponent(Entity entity) {
        if (ImGui.Button("Add Component", new SysVec2(-1, 0)))
            ImGui.OpenPopup("add_component");

        if (!ImGui.BeginPopup("add_component"))
            return;

        foreach (ComponentEntry entry in ComponentRegistry.Menu) {
            if (ImGui.MenuItem(entry.DisplayName))
                entity.AddComponent(entry.Type);
        }

        ImGui.EndPopup();
    }

    static SysVec3 ToSys(Vector3 v) => new(v.X, v.Y, v.Z);
    static Vector3 ToTk(SysVec3 v) => new(v.X, v.Y, v.Z);
}
