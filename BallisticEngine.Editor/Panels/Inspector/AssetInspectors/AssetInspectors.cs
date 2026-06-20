using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;
using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

// The per-extension asset inspectors (editor-rework Rule 1 / Phase B2), one self-registering IAssetInspector
// each. These REPLACE the `switch (ext) { case ".mat": ...; case ".png" or ...: ...; }` body of
// InspectorPanel.DrawAssetInspector. [AssetInspector(".ext")] registers each for its extension(s).
//
// RW1.4 (chunk 46): the section BODIES for every asset extension now LIVE HERE (moved out of the InspectorPanel
// god-panel) — Phase B2 only moved the DISPATCH to this registry, leaving the bodies behind under an explicit
// "later chunk" contract; this is that chunk, the asset-side mirror of B1/RW1.x's ComponentPreviews. The
// relocated bodies are byte-identical to the old inline call: they reach the panel's private EditorState through
// ctx.Panel (DrawMemberList / MarkViewportDirty / Select) and the shared grid/row + drag-drop helpers
// (InspectorPanel.BeginGrid / .Row / .AcceptGuidDrop, internal static) and audio-preview statics
// (InspectorPanel.audioPreviewVoice, shared with the AudioSource component preview).
//
// Discovery is by [AssetInspector] (engine attribute) via TypeCache; resolution is by extension, deterministic
// by priority then type name (DeterministicResolver). Most inspectors are stateless — the registry keeps a
// single shared instance per class — so the few that need per-asset cache (the material preview thumbnail) hold
// it as instance state on that one shared instance (the same single-cache lifetime the panel field had).

// Textures: import settings. Covers every image extension the old switch grouped into one case.
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
            ImGui.TextDisabled("No import settings.");
            return;
        }

        if (InspectorPanel.BeginGrid("##texsettings")) {
            InspectorPanel.Row("Texture Type");
            TextureType current = TextureImporter.TypeFromSettings(meta.Settings);
            string[] names = Enum.GetNames<TextureType>();
            int index = Array.IndexOf(names, current.ToString());
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##textype", ref index, names, names.Length)) {
                meta.Settings["textureType"] = names[index];
                meta.Save(MetaFile.PathFor(AssetDatabase.Project.ResolveAbsolute(path)));
                Guid reimported = guid;
                AsyncAssetImport.Request("Reimporting texture...",
                    onFinished: () => AssetDatabase.Invalidate(reimported));
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Changing the type reimports. Loaded materials keep the\nold instance until the scene reloads.");
    }
}

[AssetInspector(".mat")]
internal sealed class MaterialAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawMaterialEditor(ctx.Panel, ctx.Path, ctx.Guid);

    // Material preview thumbnail state. Re-rendered only when the material (guid) or its serialized
    // content (hash) changes, so the GL pass runs once per edit, not per frame. Body moved here in RW1.4
    // (was instance state on InspectorPanel; the registry's single shared inspector instance owns the same
    // single cache the panel field used to).
    Guid materialPreviewGuid;
    int materialPreviewHash;
    nint materialPreviewTex;     // ImGui handle: GL texture name or DX12 UiHeap descriptor ptr
    Dx12EditorPreview.Dx12EditorTexture materialPreviewDx12;   // DX12 backing (disposed on re-render)
    const int MaterialPreviewSize = 128;
    static bool IsDx12 => RenderBackendSelector.Selected == RenderBackend.Dx12;

    void DrawMaterialPreview(Guid guid, MaterialDefinition definition) {
        // DX12: the material-preview GPU render (Dx12EditorPreview) hangs the GPU under load — DISABLED until
        // root-caused (the inspector just omits the sphere). Re-enable with the thumbnail path once verified.
        if (IsDx12)
            return;
        // cheap content fingerprint: re-render only when the serialized material changes
        int hash = System.Text.Json.JsonSerializer.Serialize(definition, PipelineJson.Options).GetHashCode();
        if (guid != materialPreviewGuid || hash != materialPreviewHash || materialPreviewTex == 0) {
            try {
                byte[] pixels = MaterialPreviewRenderer.Render(definition, MaterialPreviewSize);
                materialPreviewDx12?.Dispose();   // free the previous texture + its UiHeap slot
                materialPreviewDx12 = Dx12EditorPreview.UploadTexture(pixels, MaterialPreviewSize);
                materialPreviewTex = materialPreviewDx12.Handle;
                materialPreviewGuid = guid;
                materialPreviewHash = hash;
            }
            catch (Exception e) {
                Debugging.LogError($"Material preview failed: {e.Message}");
                materialPreviewTex = 0;
            }
        }

        if (materialPreviewTex != 0) {
            float size = 120f;
            float pad = (ImGui.GetContentRegionAvail().X - size) * 0.5f;
            if (pad > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + pad);
            ImGui.Image(EditorApplication.Tex(materialPreviewTex), new SysVec2(size, size));
            ImGui.Spacing();
        }
    }

    void DrawMaterialEditor(InspectorPanel panel, string path, Guid guid) {
        var absolute = AssetDatabase.Project.ResolveAbsolute(path);
        MaterialDefinition definition;
        try {
            definition = PipelineJson.Read<MaterialDefinition>(absolute);
        }
        catch (Exception exception) {
            ImGui.TextDisabled($"Unreadable material: {exception.Message}");
            return;
        }

        // Unity-style preview sphere: render the material to a thumbnail (re-rendered only when the
        // material's serialized state changes), upload to a GL texture, show it centered.
        DrawMaterialPreview(guid, definition);

        ImGui.TextDisabled($"Shader: {definition.Shader ?? "(none)"}");
        ImGui.Spacing();

        // The inspector is GENERATED from the shader's DECLARED properties (Unity ShaderLab style) —
        // no more hardcoded 6-slot + scalar block. A shader that declares a new property shows it here
        // with zero editor wiring. The on-disk schema stays MaterialDefinition (the .mat JSON) with its
        // null-means-default elision; MaterialPropertyBinding joins each declared property (by semantic)
        // to its MaterialDefinition field, so editing one property writes the same JSON delta the old
        // hand-rolled UI produced — .mat files don't churn on open.
        var properties = ResolveShaderProperties(guid);

        var changed = false;
        if (InspectorPanel.BeginGrid("##matslots")) {
            foreach (var prop in properties) {
                // Honour the load-time conditional flags (PackedOrm/Cutout auto-detect) so the shown
                // value matches what actually renders when the .mat leaves them unstated.
                changed |= DrawShaderProperty(prop, definition);
            }
            ImGui.EndTable();
        }

        if (changed) {
            PipelineJson.Write(absolute, definition);
            ApplyLiveMaterial(guid, definition);
            panel.MarkViewportDirty();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Drag textures from the Assets panel onto the slots.");
    }

    // The declared property list to render. Prefer the loaded material's actual shader (a custom shader
    // would declare its own); fall back to the Standard set when the material isn't loaded yet.
    static ShaderProperties ResolveShaderProperties(Guid guid) {
        var mat = AssetDatabase.Load<Material>(guid);
        var declared = mat?.Shader?.Properties;
        return declared is { Count: > 0 } ? declared : StandardShaderProperties.Build();
    }

    // Render one declared property and write any edit back to the MaterialDefinition via its semantic.
    // Returns true if the value changed. bool-as-float properties (NormalFlipY/Transparent/PackedOrm/
    // Cutout) draw as checkboxes; IsEmissive is load-derived (not authorable) and skipped.
    static bool DrawShaderProperty(ShaderProperty prop, MaterialDefinition definition) {
        var binding = MaterialPropertyBinding.For(prop.Semantic);
        if (binding is null) return false; // not an authorable channel (e.g. IsEmissive) — skip

        InspectorPanel.Row(prop.DisplayName);
        ImGui.PushID(prop.Name);
        bool changed;
        switch (prop.Type) {
            case ShaderPropertyType.Texture2D: changed = DrawTextureSlot(binding, definition); break;
            case ShaderPropertyType.Color: changed = DrawColor(binding, definition, prop); break;
            case ShaderPropertyType.Range: changed = DrawRange(binding, definition, prop); break;
            default: changed = DrawFloatOrBool(binding, definition, prop); break;
        }
        ImGui.PopID();
        return changed;
    }

    static bool DrawTextureSlot(MaterialPropertyBinding b, MaterialDefinition definition) {
        definition.Textures.TryGetValue(b.TextureKey, out var reference);
        var display = reference is null ? "None" : Path.GetFileName(ReferenceToPath(reference) ?? reference);
        if (reference is null)
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.Button(display, new SysVec2(-1, 0));
        if (reference is null) ImGui.PopStyleColor();
        if (InspectorPanel.AcceptGuidDrop(out Guid dropped)) {
            definition.Textures[b.TextureKey] = AssetRef.FromGuid(dropped);
            return true;
        }
        return false;
    }

    static bool DrawColor(MaterialPropertyBinding b, MaterialDefinition definition, ShaderProperty prop) {
        var v = b.GetVector(definition, prop.DefaultVector);
        // Emissive is RGB-only in the .mat; base color is RGBA. Drive by binding's component count.
        if (b.ColorComponents == 3) {
            var c = new SysVec3(v.X, v.Y, v.Z);
            if (ImGui.ColorEdit3("##c", ref c)) { b.SetVector(definition, new SysVec4(c.X, c.Y, c.Z, 1f), prop.DefaultVector); return true; }
            return false;
        }
        var c4 = new SysVec4(v.X, v.Y, v.Z, v.W);
        if (ImGui.ColorEdit4("##c", ref c4)) { b.SetVector(definition, c4, prop.DefaultVector); return true; }
        return false;
    }

    static bool DrawRange(MaterialPropertyBinding b, MaterialDefinition definition, ShaderProperty prop) {
        var (min, max) = prop.Range ?? (0f, 1f);
        var f = b.GetFloat(definition, prop.DefaultFloat);
        if (ImGui.SliderFloat("##r", ref f, min, max)) { b.SetFloat(definition, f, prop.DefaultFloat); return true; }
        return false;
    }

    static bool DrawFloatOrBool(MaterialPropertyBinding b, MaterialDefinition definition, ShaderProperty prop) {
        if (b.IsBool) {
            var on = b.GetFloat(definition, prop.DefaultFloat) != 0f;
            if (ImGui.Checkbox("##b", ref on)) { b.SetFloat(definition, on ? 1f : 0f, prop.DefaultFloat); return true; }
            return false;
        }
        var f = b.GetFloat(definition, prop.DefaultFloat);
        if (ImGui.DragFloat("##f", ref f, 0.05f, 0f, 100f)) { b.SetFloat(definition, f, prop.DefaultFloat); return true; }
        return false;
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
}

[AssetInspector(".volume")]
internal sealed class VolumeProfileAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawVolumeProfileAsset(ctx.Guid);

    static void DrawVolumeProfileAsset(Guid guid) {
        var profile = AssetDatabase.Load<VolumeProfile>(guid);
        if (profile is null) {
            ImGui.TextDisabled("Unreadable volume profile.");
            return;
        }

        if (VolumeProfileEditor.Draw(profile))
            VolumeProfileEditor.SaveToAsset(profile);
    }
}

[AssetInspector(".scene")]
internal sealed class SceneAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawSceneAssetActions(ctx.Path);

    static void DrawSceneAssetActions(string path) {
        if (ImGui.Button($"{EditorIcons.Play}  Open Scene", new SysVec2(-1, 0)))
            SceneCommands.Open(path);
    }
}

[AssetInspector(".pyscene")]
internal sealed class PysceneAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        ImGui.TextWrapped("Falcor scene. On import it generates a sibling .scene you can open.");
}

// Native text assets — a hint + Show-in-Explorer. Covers the .shader/.glsl/.cubemap group.
[AssetInspector(".shader")]
[AssetInspector(".glsl")]
[AssetInspector(".cubemap")]
internal sealed class TextAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawTextAssetHint(ctx.Path);

    // Native text assets: show a hint but no noisy "unsupported" line.
    static void DrawTextAssetHint(string path) {
        ImGui.TextDisabled("Edit this file in a text editor.");
        if (ImGui.Button($"{EditorIcons.FolderOpen}  Show in Explorer", new SysVec2(-1, 0)))
            System.Diagnostics.Process.Start("explorer.exe",
                $"/select,\"{AssetDatabase.Project.ResolveAbsolute(path)}\"");
    }
}

// Prefab inspector: its captured entity tree (read-only) + an Instantiate-into-scene action.
// The backend is capture/instantiate (no live instance overrides), so this views the asset and
// plants copies; editing happens by instantiating, changing in the scene, and re-creating.
[AssetInspector(".prefab")]
internal sealed class PrefabAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawPrefabInspector(ctx.Panel, ctx.Path);

    static void DrawPrefabInspector(InspectorPanel panel, string path) {
        PrefabAsset prefab = AssetDatabase.Load<PrefabAsset>(path);
        if (prefab is null) {
            ImGui.TextDisabled("Could not load prefab.");
            return;
        }

        if (ImGui.Button($"{EditorIcons.Add}  Instantiate into Scene", new SysVec2(-1, 0))) {
            // Plants a new entity tree into the scene -> whole-scene Structural snapshot.
            EditorCommands.Structural("Instantiate Prefab", () => {
                Entity root = prefab.Instantiate();
                if (root is not null)
                    panel.Select(root);
                panel.MarkViewportDirty();
            });
        }

        ImGui.Spacing();
        ImGui.TextDisabled($"Contents ({prefab.Entities.Count} entit{(prefab.Entities.Count == 1 ? "y" : "ies")})");
        ImGui.Separator();
        foreach (var doc in prefab.Entities) {
            float indent = doc.Transform?.Parent is null ? 0 : 16f;
            if (indent > 0) ImGui.Indent(indent);
            ImGui.TextUnformatted($"{EditorIcons.Package}  {doc.Name}");
            if (indent > 0) ImGui.Unindent(indent);
        }
    }
}

// DataAsset inspector: reflect the loaded instance through the SAME member list the component
// inspector uses (honors [Range]/[Header]/[Tooltip]/[FoldoutGroup]/asset pickers). Edits write
// straight back to the .asset file via DataAssetSerializer: an asset edit, not scene state, so NO
// scene undo (the .volume edit-write-back pattern). Change is detected by a serialized-text diff.
[AssetInspector(".asset")]
internal sealed class DataAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawDataAssetInspector(ctx.Panel, ctx.Path);

    // The DataAsset (ScriptableObject-equivalent) currently being edited, cached so edits accumulate
    // on one instance; reloaded when the selected .asset path changes. Was instance state on InspectorPanel;
    // the registry's single shared inspector instance owns the same single cache (RW1.4).
    string dataAssetPath;
    object dataAssetInstance;

    void DrawDataAssetInspector(InspectorPanel panel, string path) {
        if (dataAssetPath != path || dataAssetInstance is null) {
            dataAssetPath = path;
            dataAssetInstance = LoadDataAsset(path);
        }
        if (dataAssetInstance is not DataAsset asset) {
            ImGui.TextDisabled("Could not load data asset (unknown or renamed type?).");
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

// Audio asset view: a Preview/Stop button + clip stats, so you can audition a .wav/.ogg straight
// from the asset browser without dropping it on an AudioSource. Same Audio facade as the component
// preview (play-mode-independent; silent no-op with no audio device). Shares the audioPreviewVoice
// static on InspectorPanel with the AudioSource component preview (RW1.3).
[AssetInspector(".wav")]
[AssetInspector(".wave")]
[AssetInspector(".ogg")]
internal sealed class AudioClipAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawAudioClipAsset(ctx.Path);

    static void DrawAudioClipAsset(string path) {
        AudioClip clip = AssetDatabase.Load<AudioClip>(path);
        if (clip is null) {
            ImGui.TextDisabled("Could not load audio clip.");
            return;
        }

        EditorDecoration.DrawSectionHeader("Preview");
        bool playing = InspectorPanel.audioPreviewVoice is { IsPlaying: true };
        if (ImGui.Button(playing ? $"{EditorIcons.Pause}  Stop" : $"{EditorIcons.Play}  Play",
                new SysVec2(120, 0))) {
            InspectorPanel.audioPreviewVoice?.Stop();
            InspectorPanel.audioPreviewVoice = playing ? null : Audio.Play(clip);
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{clip.DurationSeconds:F1}s  -  {clip.Channels}ch  -  {clip.SampleRate} Hz");
        if (!Audio.IsAvailable)
            ImGui.TextDisabled("(no audio device on this machine - preview is silent)");
    }
}

// Animation-clip asset view: clip stats. A skeletal pose preview needs a skinned mesh to drive,
// which an asset-only view doesn't have - assign the clip to an Animator on a skinned entity and
// use the Animator scrub. Here we just summarize the clip.
[AssetInspector(".banim")]
internal sealed class AnimationClipAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawAnimationClipAsset(ctx.Path);

    static void DrawAnimationClipAsset(string path) {
        AnimationClip clip = AssetDatabase.Load<AnimationClip>(path);
        if (clip is null) {
            ImGui.TextDisabled("Could not load animation clip.");
            return;
        }

        EditorDecoration.DrawSectionHeader("Animation");
        ImGui.TextDisabled($"Duration: {clip.DurationSeconds:F2}s");
        ImGui.TextDisabled($"Channels (animated bones): {clip.Data.Channels.Length}");
        ImGui.TextDisabled($"Ticks/sec: {clip.TicksPerSecond:F0}");
        ImGui.Spacing();
        ImGui.TextWrapped("Assign this clip to an Animator on a skinned mesh, then use the Animator's " +
            "scrub slider to preview the pose.");
    }
}
