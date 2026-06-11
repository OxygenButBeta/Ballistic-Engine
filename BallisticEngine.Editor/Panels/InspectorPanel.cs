using System.Reflection;
using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;
using ImGuiNET;
using OpenTK.Mathematics;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Inspector: entity editing (transform + reflected component members in a clean two-column
// layout) or asset editing (import settings / material editor) depending on the selection.
// Asset slots support BOTH drag-drop from the Assets panel and a click-to-open picker popup.
// Every interaction pushes an undo snapshot when it starts.
internal sealed class InspectorPanel {
    readonly EditorState state;

    // Pending asset-picker request (opened from an asset slot).
    MemberInfo pickerMember;
    object pickerTarget;
    Type pickerType;
    string pickerSearch = "";
    bool openPicker;

    public InspectorPanel(EditorState state) => this.state = state;

    public void DrawContents() {
        // Denser rows than the global style so more fits on screen.
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new SysVec2(8, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new SysVec2(8, 4));

        if (state.HasAssetSelection) {
            DrawAssetInspector();
        }
        else if (state.SelectedSceneBehaviour is not null) {
            DrawSceneBehaviourInspector(state.SelectedSceneBehaviour);
        }
        else if (state.Selected is not null) {
            DrawEntityInspector(state.Selected);
        }
        else {
            ImGui.TextDisabled("Nothing selected.");
        }

        if (openPicker) {
            openPicker = false;
            pickerSearch = "";
            ImGui.OpenPopup("##assetpicker");
        }
        DrawAssetPickerPopup();

        ImGui.PopStyleVar(2);
    }

    // ---- Scene behaviour inspector --------------------------------------------

    void DrawSceneBehaviourInspector(SceneBehaviour behaviour) {
        Type type = behaviour.GetType();
        ImGui.Text(type.Name);
        ImGui.TextDisabled("Scene component");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.PushID(behaviour.InstanceId.GetHashCode());

        if (BeginGrid("##sbmembers")) {
            bool enabled = behaviour.IsEnabled;
            Row("Enabled");
            if (ImGui.Checkbox("##enabled", ref enabled)) { }
            if (ImGui.IsItemActivated()) EditorUndo.Push();
            if (enabled != behaviour.IsEnabled) behaviour.IsEnabled = enabled;

            foreach (MemberInfo member in ComponentReflection.SerializableMembers(type))
                DrawMember(member, behaviour);

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Remove", new SysVec2(-1, 0))) {
            EditorUndo.Push();
            SceneManager.GetCurrentScene().RemoveSceneBehaviour(behaviour);
            state.SelectSceneBehaviour(null);
        }

        ImGui.PopID();
    }

    // ---- Entity inspector ----------------------------------------------------

    void DrawEntityInspector(Entity entity) {
        var name = entity.Name ?? "";
        bool active = entity.IsActive;

        if (ImGui.Checkbox("##active", ref active)) { }
        if (ImGui.IsItemActivated()) EditorUndo.Push();
        if (active != entity.IsActive) entity.SetActive(active);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        var renamed = ImGui.InputText("##name", ref name, 128);
        if (ImGui.IsItemActivated()) EditorUndo.Push();
        if (renamed) entity.Name = name;

        ImGui.Spacing();

        DrawTransform(entity.transform);

        foreach (Behaviour behaviour in entity.Behaviours.ToArray())
            DrawComponent(entity, behaviour);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawAddComponent(entity);
    }

    static void DrawTransform(Transform transform) {
        if (!ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (BeginGrid("##transform")) {
            SysVec3Row("Position", transform.Position, v => transform.Position = v, 0.05f);
            SysVec3Row("Rotation", transform.EulerAngles, v => transform.EulerAngles = v, 0.5f);
            SysVec3Row("Scale", transform.Scale, v => transform.Scale = v, 0.05f);
            ImGui.EndTable();
        }

        ImGui.Spacing();
    }

    void DrawComponent(Entity entity, Behaviour behaviour) {
        Type type = behaviour.GetType();
        ImGui.PushID(behaviour.InstanceId.GetHashCode());

        bool open = ImGui.CollapsingHeader($"{type.Name}##hdr", ImGuiTreeNodeFlags.DefaultOpen);

        if (ImGui.BeginPopupContextItem("##componentctx")) {
            if (ImGui.MenuItem("Remove Component")) {
                EditorUndo.Push();
                entity.RemoveComponent(behaviour);
                ImGui.EndPopup();
                ImGui.PopID();
                return;
            }
            ImGui.EndPopup();
        }

        if (open) {
            if (BeginGrid("##members")) {
                // Enabled toggle as the first row.
                bool enabled = behaviour.IsEnabled;
                Row("Enabled");
                if (ImGui.Checkbox("##enabled", ref enabled)) { }
                if (ImGui.IsItemActivated()) EditorUndo.Push();
                if (enabled != behaviour.IsEnabled) behaviour.IsEnabled = enabled;

                foreach (MemberInfo member in ComponentReflection.SerializableMembers(type))
                    DrawMember(member, behaviour);

                if (behaviour is Renderer renderer)
                    DrawSubMeshMaterials(renderer);

                ImGui.EndTable();
            }
            ImGui.Spacing();
        }

        ImGui.PopID();
    }

    void DrawMember(MemberInfo member, object target) {
        Type memberType = ComponentReflection.MemberType(member);
        object value = ComponentReflection.GetValue(member, target);

        Row(Prettify(member.Name));
        ImGui.PushID(member.Name);
        ImGui.SetNextItemWidth(-1);

        if (typeof(BObject).IsAssignableFrom(memberType)) {
            DrawAssetSlot(member, target, value as BObject, memberType);
        }
        else {
            switch (value) {
                case float f: {
                    var changed = ImGui.DragFloat("##v", ref f, 0.05f);
                    if (ImGui.IsItemActivated()) EditorUndo.Push();
                    if (changed) ComponentReflection.SetValue(member, target, f);
                    break;
                }
                case int i: {
                    var changed = ImGui.DragInt("##v", ref i);
                    if (ImGui.IsItemActivated()) EditorUndo.Push();
                    if (changed) ComponentReflection.SetValue(member, target, i);
                    break;
                }
                case bool b: {
                    var changed = ImGui.Checkbox("##v", ref b);
                    if (ImGui.IsItemActivated()) EditorUndo.Push();
                    if (changed) ComponentReflection.SetValue(member, target, b);
                    break;
                }
                case string s: {
                    var str = s ?? "";
                    var changed = ImGui.InputText("##v", ref str, 256);
                    if (ImGui.IsItemActivated()) EditorUndo.Push();
                    if (changed) ComponentReflection.SetValue(member, target, str);
                    break;
                }
                case Vector3 v3: {
                    var sv = new System.Numerics.Vector3(v3.X, v3.Y, v3.Z);
                    var changed = ImGui.DragFloat3("##v", ref sv, 0.05f);
                    if (ImGui.IsItemActivated()) EditorUndo.Push();
                    if (changed) ComponentReflection.SetValue(member, target, new Vector3(sv.X, sv.Y, sv.Z));
                    break;
                }
                case Vector2 v2: {
                    var sv = new SysVec2(v2.X, v2.Y);
                    var changed = ImGui.DragFloat2("##v", ref sv, 0.05f);
                    if (ImGui.IsItemActivated()) EditorUndo.Push();
                    if (changed) ComponentReflection.SetValue(member, target, new Vector2(sv.X, sv.Y));
                    break;
                }
                case Enum e: {
                    string[] names = Enum.GetNames(memberType);
                    int current = Array.IndexOf(names, e.ToString());
                    var changed = ImGui.Combo("##v", ref current, names, names.Length);
                    if (ImGui.IsItemActivated()) EditorUndo.Push();
                    if (changed) ComponentReflection.SetValue(member, target, Enum.Parse(memberType, names[current]));
                    break;
                }
                default:
                    ImGui.TextDisabled($"({memberType.Name})");
                    break;
            }
        }

        ImGui.PopID();
    }

    // Multi-material meshes resolve their materials from refs baked into the mesh at import;
    // list them read-only so an empty SharedMaterial slot isn't mistaken for "no materials".
    // (SharedMaterial only overrides slots that have no baked ref.)
    static void DrawSubMeshMaterials(Renderer renderer) {
        Mesh mesh = renderer.SharedMesh;
        if (mesh?.SubMeshes is not { Length: > 1 } subMeshes)
            return;

        for (var i = 0; i < subMeshes.Length; i++) {
            Row(i == 0 ? $"Materials ({subMeshes.Length})" : "");

            SubMeshData sub = subMeshes[i];
            var label = string.IsNullOrEmpty(sub.Name) ? $"Submesh {i}" : sub.Name;
            Material material = renderer.MaterialFor(i);

            if (material is null) {
                ImGui.TextDisabled($"{label} — none");
                continue;
            }

            var reference = sub.MaterialRef;
            if (reference is null && AssetDatabase.TryGetAssetGuid(material, out Guid guid))
                reference = AssetDatabase.GuidToAssetPath(guid);

            ImGui.TextUnformatted(Path.GetFileNameWithoutExtension(reference ?? label));
            if (reference is not null && ImGui.IsItemHovered())
                ImGui.SetTooltip($"{label}\n{reference}");
        }
    }

    // Asset slot. Assigned: clicking the name PINS the asset in the Inspector (shows its
    // asset view), the ▾ button opens the picker. Unassigned: click opens the picker.
    // Either way the slot is a drag-drop target for browser tiles.
    void DrawAssetSlot(MemberInfo member, object target, BObject asset, Type assetType) {
        Guid guid = default;
        var hasGuid = asset is not null && AssetDatabase.TryGetAssetGuid(asset, out guid);

        if (asset is null) {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            if (ImGui.Button("None  ▾", new SysVec2(-1, 0)))
                OpenPickerFor(member, target, assetType);
            ImGui.PopStyleColor();
            if (AcceptGuidDrop(out Guid d0))
                AssignAsset(member, target, assetType, d0);
            return;
        }

        var path = hasGuid ? AssetDatabase.GuidToAssetPath(guid) : null;
        var display = path is not null ? Path.GetFileName(path) : asset.GetType().Name;

        float pickerW = ImGui.GetFrameHeight() + 6;
        if (ImGui.Button(display, new SysVec2(-pickerW - 4, 0)) && path is not null)
            state.SelectAsset(path, guid); // pin the referenced asset in the Inspector
        if (AcceptGuidDrop(out Guid d1))
            AssignAsset(member, target, assetType, d1);
        if (ImGui.IsItemHovered() && path is not null)
            ImGui.SetTooltip(path);

        ImGui.SameLine();
        if (ImGui.Button("▾", new SysVec2(pickerW, 0)))
            OpenPickerFor(member, target, assetType);
        if (AcceptGuidDrop(out Guid d2))
            AssignAsset(member, target, assetType, d2);
    }

    void OpenPickerFor(MemberInfo member, object target, Type assetType) {
        pickerMember = member;
        pickerTarget = target;
        pickerType = assetType;
        openPicker = true;
    }

    void AssignAsset(MemberInfo member, object target, Type assetType, Guid guid) {
        EditorUndo.Push();
        MethodInfo load = typeof(AssetDatabase).GetMethod(nameof(AssetDatabase.Load), [typeof(Guid)])!
            .MakeGenericMethod(assetType);
        object loaded = load.Invoke(null, [guid]);
        if (loaded is not null)
            ComponentReflection.SetValue(member, target, loaded);
    }

    // Mini asset-picker window: search + every compatible asset; click to assign.
    void DrawAssetPickerPopup() {
        ImGui.SetNextWindowSize(new SysVec2(380, 420), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("##assetpicker"))
            return;

        ImGui.TextDisabled($"Select {pickerType?.Name ?? "asset"}");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search...", ref pickerSearch, 128);
        ImGui.Separator();

        ImGui.BeginChild("##list");

        if (ImGui.Selectable("(None)")) {
            EditorUndo.Push();
            ComponentReflection.SetValue(pickerMember, pickerTarget, null);
            ImGui.CloseCurrentPopup();
        }

        string[] extensions = CompatibleExtensions(pickerType);
        foreach ((string path, Guid guid) in AssetDatabase.EnumerateAssets()
                     .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (extensions.Length > 0 && !extensions.Contains(ext))
                continue;
            if (pickerSearch.Length > 0 && !path.Contains(pickerSearch, StringComparison.OrdinalIgnoreCase))
                continue;

            if (ImGui.Selectable($"{Path.GetFileName(path)}##{guid}")) {
                AssignAsset(pickerMember, pickerTarget, pickerType, guid);
                ImGui.CloseCurrentPopup();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(path);
        }

        ImGui.EndChild();
        ImGui.EndPopup();
    }

    static string[] CompatibleExtensions(Type assetType) {
        if (assetType is null) return [];
        if (typeof(Texture3D).IsAssignableFrom(assetType))
            return [".cubemap", ".hdr", ".exr", ".png", ".jpg", ".jpeg"];
        if (typeof(Texture2D).IsAssignableFrom(assetType))
            return [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".hdr", ".exr"];
        if (typeof(Mesh).IsAssignableFrom(assetType))
            return [".fbx", ".obj"];
        if (typeof(Material).IsAssignableFrom(assetType))
            return [".mat"];
        if (typeof(Shader).IsAssignableFrom(assetType))
            return [".shader"];
        return [];
    }

    void DrawAddComponent(Entity entity) {
        if (ImGui.Button("Add Component", new SysVec2(-1, 0)))
            ImGui.OpenPopup("##addcomponent");

        if (!ImGui.BeginPopup("##addcomponent"))
            return;

        foreach (ComponentEntry entry in ComponentRegistry.Menu) {
            if (ImGui.MenuItem(entry.DisplayName)) {
                EditorUndo.Push();
                entity.AddComponent(entry.Type);
            }
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
            ImGui.TextDisabled(meta.Importer);
        ImGui.Separator();
        ImGui.Spacing();

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

        if (BeginGrid("##texsettings")) {
            Row("Texture Type");
            TextureType current = TextureImporter.TypeFromSettings(meta.Settings);
            string[] names = Enum.GetNames<TextureType>();
            int index = Array.IndexOf(names, current.ToString());
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##textype", ref index, names, names.Length)) {
                meta.Settings["textureType"] = names[index];
                meta.Save(MetaFile.PathFor(AssetDatabase.Project.ResolveAbsolute(path)));
                AssetDatabase.Refresh();
                AssetDatabase.Invalidate(guid);
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Changing the type reimports. Loaded materials keep the\nold instance until the scene reloads.");
    }

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
        ImGui.Spacing();

        var changed = false;
        if (BeginGrid("##matslots")) {
            foreach (TextureType slot in new[] {
                         TextureType.Diffuse, TextureType.Normal, TextureType.Metallic,
                         TextureType.Roughness, TextureType.AO, TextureType.Emissive,
                     }) {
                definition.Textures.TryGetValue(slot.ToString(), out var reference);
                var display = reference is null
                    ? "None"
                    : Path.GetFileName(ReferenceToPath(reference) ?? reference);

                Row(slot.ToString());
                ImGui.PushID((int)slot);
                ImGui.Button(display, new SysVec2(-1, 0));
                if (AcceptGuidDrop(out Guid dropped)) {
                    definition.Textures[slot.ToString()] = AssetRef.FromGuid(dropped);
                    changed = true;
                }
                ImGui.PopID();
            }

            // Scalar material properties (stored in the .mat next to the texture refs).
            Row("Transparent");
            var transparent = definition.Transparent;
            if (ImGui.Checkbox("##mattransparent", ref transparent)) {
                definition.Transparent = transparent;
                changed = true;
            }

            if (definition.Transparent) {
                Row("Opacity");
                var opacity = definition.Opacity;
                if (ImGui.SliderFloat("##matopacity", ref opacity, 0f, 1f)) {
                    definition.Opacity = opacity;
                    changed = true;
                }
            }

            Row("Emissive Color");
            var emissive = definition.EmissiveColor is { Length: >= 3 } c
                ? new System.Numerics.Vector3(c[0], c[1], c[2])
                : System.Numerics.Vector3.One;
            if (ImGui.ColorEdit3("##matemissivecolor", ref emissive)) {
                definition.EmissiveColor = [emissive.X, emissive.Y, emissive.Z];
                changed = true;
            }

            Row("Emissive Intensity");
            var emissiveIntensity = definition.EmissiveIntensity;
            if (ImGui.DragFloat("##matemissiveintensity", ref emissiveIntensity, 0.05f, 0f, 100f)) {
                definition.EmissiveIntensity = emissiveIntensity;
                changed = true;
            }

            ImGui.EndTable();
        }

        if (changed) {
            PipelineJson.Write(absolute, definition);
            ApplyLiveMaterial(guid, definition);
        }

        ImGui.Spacing();
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
        material.Metallic = LoadSlot(definition, TextureType.Metallic);
        material.Roughness = LoadSlot(definition, TextureType.Roughness);
        material.AO = LoadSlot(definition, TextureType.AO);
        material.Emissive = LoadSlot(definition, TextureType.Emissive);
        MaterialLoader.ApplyScalars(material, definition);
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
        SceneSerializer.Load(AssetDatabase.Project.ResolveAbsolute(assetPath));
    }

    // ---- Layout helpers --------------------------------------------------------

    static bool BeginGrid(string id) {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchProp))
            return false;
        ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthStretch, 0.38f);
        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch, 0.62f);
        return true;
    }

    // Starts a new label/value row and leaves the cursor in the value column.
    static void Row(string label) {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(-1);
    }

    static void SysVec3Row(string label, Vector3 value, Action<Vector3> apply, float speed) {
        Row(label);
        var sv = new System.Numerics.Vector3(value.X, value.Y, value.Z);
        var changed = ImGui.DragFloat3($"##{label}", ref sv, speed);
        if (ImGui.IsItemActivated()) EditorUndo.Push();
        if (changed) apply(new Vector3(sv.X, sv.Y, sv.Z));
    }

    // "RotationEuler" -> "Rotation Euler", "lightIntensity" -> "Light Intensity"
    static string Prettify(string name) {
        if (string.IsNullOrEmpty(name))
            return name;

        var result = new System.Text.StringBuilder(name.Length + 4);
        result.Append(char.ToUpperInvariant(name[0]));
        for (var i = 1; i < name.Length; i++) {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                result.Append(' ');
            result.Append(name[i]);
        }
        return result.ToString();
    }
}
