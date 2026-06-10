using System.Reflection;
using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
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
        if (state.HasAssetSelection) {
            DrawAssetInspector();
            return;
        }

        Entity entity = state.Selected;
        if (entity is null) {
            ImGui.TextDisabled("Nothing selected.");
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

    // ---- Asset inspector -----------------------------------------------------

    void DrawAssetInspector() {
        var path = state.SelectedAssetPath;
        Guid guid = state.SelectedAssetGuid;
        var ext = Path.GetExtension(path).ToLowerInvariant();

        ImGui.Text(Path.GetFileName(path));
        ImGui.TextDisabled(path);
        if (AssetDatabase.TryGetMeta(guid, out MetaFile meta))
            ImGui.TextDisabled($"{meta.Importer}   guid:{guid:N}");
        ImGui.Separator();

        switch (ext) {
            case ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".hdr" or ".exr":
                DrawTextureImportSettings(path, guid, meta);
                break;
            case ".mat":
                DrawMaterialEditor(path, guid);
                break;
            case ".scene":
                if (ImGui.Button("Open Scene", new SysVec2(-1, 0)))
                    OpenScene(path);
                break;
            default:
                ImGui.TextDisabled("No editable settings for this asset type.");
                break;
        }
    }

    static void DrawTextureImportSettings(string path, Guid guid, MetaFile meta) {
        if (meta is null) {
            ImGui.TextDisabled("No import settings.");
            return;
        }

        TextureType current = TextureImporter.TypeFromSettings(meta.Settings);
        string[] names = Enum.GetNames<TextureType>();
        int index = Array.IndexOf(names, current.ToString());

        if (ImGui.Combo("Texture Type", ref index, names, names.Length)) {
            meta.Settings["textureType"] = names[index];
            meta.Save(MetaFile.PathFor(AssetDatabase.Project.ResolveAbsolute(path)));
            AssetDatabase.Refresh();          // settings hash changed -> reimports
            AssetDatabase.Invalidate(guid);   // next Load picks up the new import
        }

        ImGui.TextDisabled("Changing the type reimports the texture. Already-loaded\nmaterials keep the old instance until the scene reloads.");
    }

    // Edits the .mat definition AND the live Material instance, so changes show immediately
    // and persist. Texture slots accept drags from the asset browser.
    void DrawMaterialEditor(string path, Guid guid) {
        var absolute = AssetDatabase.Project.ResolveAbsolute(path);
        MaterialDefinition definition;
        try {
            definition = PipelineJson.Read<MaterialDefinition>(absolute);
        }
        catch (Exception exception) {
            ImGui.TextDisabled($"Unreadable material: {exception.Message}");
            return;
        }

        ImGui.TextDisabled($"Shader: {definition.Shader ?? "(none)"}");
        ImGui.Separator();

        var changed = false;
        foreach (TextureType slot in new[] {
                     TextureType.Diffuse, TextureType.Normal, TextureType.Metallic,
                     TextureType.Roughness, TextureType.AO,
                 }) {
            definition.Textures.TryGetValue(slot.ToString(), out var reference);
            var display = reference is null ? "(none)" : Path.GetFileName(ReferenceToPath(reference) ?? reference);

            ImGui.Button($"{display}##slot_{slot}", new SysVec2(ImGui.GetContentRegionAvail().X * 0.62f, 0));
            if (AcceptGuidDrop(out Guid dropped)) {
                definition.Textures[slot.ToString()] = AssetRef.FromGuid(dropped);
                changed = true;
            }
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(slot.ToString());
        }

        if (changed) {
            PipelineJson.Write(absolute, definition);
            ApplyLiveMaterial(guid, definition);
        }

        ImGui.Separator();
        ImGui.TextDisabled("Drag textures from the Assets panel onto the slots.");
    }

    static string ReferenceToPath(string reference) =>
        AssetRef.IsGuidRef(reference, out Guid g) ? AssetDatabase.GuidToAssetPath(g) : reference;

    static void ApplyLiveMaterial(Guid materialGuid, MaterialDefinition definition) {
        var material = AssetDatabase.Load<Material>(materialGuid);
        if (material is null)
            return;

        material.Diffuse = LoadSlot(definition, TextureType.Diffuse) ?? material.Diffuse;
        material.Normal = LoadSlot(definition, TextureType.Normal);
        material.Specular = LoadSlot(definition, TextureType.Metallic); // legacy naming: Specular holds the metallic map
        material.Roughness = LoadSlot(definition, TextureType.Roughness);
        material.AO = LoadSlot(definition, TextureType.AO);
    }

    static Texture2D LoadSlot(MaterialDefinition definition, TextureType slot) =>
        definition.Textures.TryGetValue(slot.ToString(), out var reference) && reference is not null
            ? AssetDatabase.LoadRef<Texture2D>(reference)
            : null;

    static unsafe bool AcceptGuidDrop(out Guid guid) {
        guid = Guid.Empty;
        if (!ImGui.BeginDragDropTarget())
            return false;

        ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(AssetBrowserPanel.DragType);
        var accepted = false;
        if (payload.NativePtr != null && payload.Data != IntPtr.Zero) {
            var text = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(payload.Data, payload.DataSize);
            accepted = Guid.TryParse(text, out guid);
        }

        ImGui.EndDragDropTarget();
        return accepted;
    }

    static void OpenScene(string assetPath) {
        if (SceneManager.IsPlaying)
            SceneManager.StopPlay();
        SceneManager.GetCurrentScene().Clear();
        BallisticEngine.Serialization.SceneSerializer.Load(AssetDatabase.Project.ResolveAbsolute(assetPath));
    }

    static SysVec3 ToSys(Vector3 v) => new(v.X, v.Y, v.Z);
    static Vector3 ToTk(SysVec3 v) => new(v.X, v.Y, v.Z);
}
