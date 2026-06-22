using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.AssetInspectors.AssetInspectorGuiAccess;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

internal static class AssetInspectorGuiAccess {
    internal static IEditorGui gui => EditorGui.Shared;
}

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

[AssetInspector(".mat")]
internal sealed class MaterialAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawMaterialEditor(ctx.Panel, ctx.Path, ctx.Guid);

    Guid materialPreviewGuid;
    int materialPreviewHash;
    nint materialPreviewTex;
    Dx12EditorPreview.Dx12EditorTexture materialPreviewDx12;
    const int MaterialPreviewSize = 128;
    static bool IsDx12 => RenderBackendSelector.Selected == RenderBackend.Dx12;

    void DrawMaterialPreview(Guid guid, MaterialDefinition definition) {
        if (IsDx12)
            return;
        int hash = System.Text.Json.JsonSerializer.Serialize(definition, PipelineJson.Options).GetHashCode();
        if (guid != materialPreviewGuid || hash != materialPreviewHash || materialPreviewTex == 0) {
            try {
                byte[] pixels = MaterialPreviewRenderer.Render(definition, MaterialPreviewSize);
                materialPreviewDx12?.Dispose();
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
            float pad = (gui.ContentRegionAvail.X - size) * 0.5f;
            if (pad > 0) gui.CursorPosX += pad;
            gui.Image(materialPreviewTex, new SysVec2(size, size));
            gui.Spacing();
        }
    }

    void DrawMaterialEditor(InspectorPanel panel, string path, Guid guid) {
        var absolute = AssetDatabase.Project.ResolveAbsolute(path);
        MaterialDefinition definition;
        try {
            definition = PipelineJson.Read<MaterialDefinition>(absolute);
        }
        catch (Exception exception) {
            gui.TextDisabled($"Unreadable material: {exception.Message}");
            return;
        }

        DrawMaterialPreview(guid, definition);

        gui.TextDisabled($"Shader: {definition.Shader ?? "(none)"}");
        gui.Spacing();

        var properties = ResolveShaderProperties(guid);

        var changed = false;
        if (InspectorPanel.BeginGrid("##matslots")) {
            foreach (var prop in properties) {
                changed |= DrawShaderProperty(prop, definition);
            }
            gui.EndTable();
        }

        if (changed) {
            PipelineJson.Write(absolute, definition);
            ApplyLiveMaterial(guid, definition);
            panel.MarkViewportDirty();
        }

        gui.Spacing();
        gui.TextDisabled("Drag textures from the Assets panel onto the slots.");
    }

    static ShaderProperties ResolveShaderProperties(Guid guid) {
        var mat = AssetDatabase.Load<Material>(guid);
        var declared = mat?.Shader?.Properties;
        return declared is { Count: > 0 } ? declared : StandardShaderProperties.Build();
    }

    static bool DrawShaderProperty(ShaderProperty prop, MaterialDefinition definition) {
        var binding = prop.Semantic == MaterialSemantic.None
            ? MaterialPropertyBinding.ForCustom(prop)
            : MaterialPropertyBinding.For(prop.Semantic);
        if (binding is null) return false;

        InspectorPanel.Row(prop.DisplayName);
        gui.PushId(prop.Name);
        bool changed;
        switch (prop.Type) {
            case ShaderPropertyType.Texture2D: changed = DrawTextureSlot(binding, definition); break;
            case ShaderPropertyType.Color: changed = DrawColor(binding, definition, prop); break;
            case ShaderPropertyType.Range: changed = DrawRange(binding, definition, prop); break;
            default: changed = DrawFloatOrBool(binding, definition, prop); break;
        }
        gui.PopId();
        return changed;
    }

    static bool DrawTextureSlot(MaterialPropertyBinding b, MaterialDefinition definition) {
        bool custom = b.CustomTextureKey is not null;
        string key = custom ? b.CustomTextureKey : b.TextureKey;
        var dict = custom ? (definition.CustomTextures ??= new()) : definition.Textures;
        dict.TryGetValue(key, out var reference);
        var display = reference is null ? "None" : Path.GetFileName(ReferenceToPath(reference) ?? reference);
        if (reference is null)
            gui.PushColor(EditorStyleColor.Text, gui.StyleColor(EditorStyleColor.TextDisabled));
        gui.Button(display, new SysVec2(-1, 0));
        if (reference is null) gui.PopColor();
        if (InspectorPanel.AcceptGuidDrop(out Guid dropped)) {
            dict[key] = AssetRef.FromGuid(dropped);
            return true;
        }
        return false;
    }

    static bool DrawColor(MaterialPropertyBinding b, MaterialDefinition definition, ShaderProperty prop) {
        var v = b.GetVector(definition, prop.DefaultVector);
        if (b.ColorComponents == 3) {
            var c = new SysVec3(v.X, v.Y, v.Z);
            if (gui.ColorEdit3("##c", ref c)) { b.SetVector(definition, new SysVec4(c.X, c.Y, c.Z, 1f), prop.DefaultVector); return true; }
            return false;
        }
        var c4 = new SysVec4(v.X, v.Y, v.Z, v.W);
        if (gui.ColorEdit4("##c", ref c4)) { b.SetVector(definition, c4, prop.DefaultVector); return true; }
        return false;
    }

    static bool DrawRange(MaterialPropertyBinding b, MaterialDefinition definition, ShaderProperty prop) {
        var (min, max) = prop.Range ?? (0f, 1f);
        var f = b.GetFloat(definition, prop.DefaultFloat);
        if (gui.SliderFloat("##r", ref f, min, max)) { b.SetFloat(definition, f, prop.DefaultFloat); return true; }
        return false;
    }

    static bool DrawFloatOrBool(MaterialPropertyBinding b, MaterialDefinition definition, ShaderProperty prop) {
        if (b.IsBool) {
            var on = b.GetFloat(definition, prop.DefaultFloat) != 0f;
            if (gui.Checkbox("##b", ref on)) { b.SetFloat(definition, on ? 1f : 0f, prop.DefaultFloat); return true; }
            return false;
        }
        var f = b.GetFloat(definition, prop.DefaultFloat);
        if (gui.DragFloat("##f", ref f, 0.05f, 0f, 100f)) { b.SetFloat(definition, f, prop.DefaultFloat); return true; }
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
        MaterialLoader.ApplyCustomProperties(material, definition);
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
            gui.TextDisabled("Unreadable volume profile.");
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
        if (gui.Button($"{EditorIcons.Play}  Open Scene", new SysVec2(-1, 0)))
            SceneCommands.Open(path);
    }
}

[AssetInspector(".pyscene")]
internal sealed class PysceneAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        gui.TextWrapped("Falcor scene. On import it generates a sibling .scene you can open.");
}

[AssetInspector(".shader")]
[AssetInspector(".glsl")]
[AssetInspector(".cubemap")]
internal sealed class TextAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawTextAssetHint(ctx.Path);

    static void DrawTextAssetHint(string path) {
        gui.TextDisabled("Edit this file in a text editor.");
        if (gui.Button($"{EditorIcons.FolderOpen}  Show in Explorer", new SysVec2(-1, 0)))
            System.Diagnostics.Process.Start("explorer.exe",
                $"/select,\"{AssetDatabase.Project.ResolveAbsolute(path)}\"");
    }
}

[AssetInspector(".prefab")]
internal sealed class PrefabAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawPrefabInspector(ctx.Panel, ctx.Path);

    static void DrawPrefabInspector(InspectorPanel panel, string path) {
        PrefabAsset prefab = AssetDatabase.Load<PrefabAsset>(path);
        if (prefab is null) {
            gui.TextDisabled("Could not load prefab.");
            return;
        }

        if (gui.Button($"{EditorIcons.Add}  Instantiate into Scene", new SysVec2(-1, 0))) {
            EditorCommands.Structural("Instantiate Prefab", () => {
                Entity root = prefab.Instantiate();
                if (root is not null)
                    panel.Select(root);
                panel.MarkViewportDirty();
            });
        }

        gui.Spacing();
        gui.TextDisabled($"Contents ({prefab.Entities.Count} entit{(prefab.Entities.Count == 1 ? "y" : "ies")})");
        gui.Separator();
        foreach (var doc in prefab.Entities) {
            float indent = doc.Transform?.Parent is null ? 0 : 16f;
            if (indent > 0) gui.Indent(indent);
            gui.TextUnformatted($"{EditorIcons.Package}  {doc.Name}");
            if (indent > 0) gui.Unindent(indent);
        }
    }
}

[AssetInspector(".asset")]
internal sealed class DataAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawDataAssetInspector(ctx.Panel, ctx.Path);

    string dataAssetPath;
    object dataAssetInstance;

    void DrawDataAssetInspector(InspectorPanel panel, string path) {
        if (dataAssetPath != path || dataAssetInstance is null) {
            dataAssetPath = path;
            dataAssetInstance = LoadDataAsset(path);
        }
        if (dataAssetInstance is not DataAsset asset) {
            gui.TextDisabled("Could not load data asset (unknown or renamed type?).");
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

[AssetInspector(".wav")]
[AssetInspector(".wave")]
[AssetInspector(".ogg")]
internal sealed class AudioClipAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawAudioClipAsset(ctx.Path);

    static void DrawAudioClipAsset(string path) {
        AudioClip clip = AssetDatabase.Load<AudioClip>(path);
        if (clip is null) {
            gui.TextDisabled("Could not load audio clip.");
            return;
        }

        EditorDecoration.DrawSectionHeader("Preview");
        bool playing = InspectorPanel.audioPreviewVoice is { IsPlaying: true };
        if (gui.Button(playing ? $"{EditorIcons.Pause}  Stop" : $"{EditorIcons.Play}  Play",
                new SysVec2(120, 0))) {
            InspectorPanel.audioPreviewVoice?.Stop();
            InspectorPanel.audioPreviewVoice = playing ? null : Audio.Play(clip);
        }
        gui.SameLine();
        gui.TextDisabled($"{clip.DurationSeconds:F1}s  -  {clip.Channels}ch  -  {clip.SampleRate} Hz");
        if (!Audio.IsAvailable)
            gui.TextDisabled("(no audio device on this machine - preview is silent)");
    }
}

[AssetInspector(".banim")]
internal sealed class AnimationClipAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawAnimationClipAsset(ctx.Path);

    static void DrawAnimationClipAsset(string path) {
        AnimationClip clip = AssetDatabase.Load<AnimationClip>(path);
        if (clip is null) {
            gui.TextDisabled("Could not load animation clip.");
            return;
        }

        EditorDecoration.DrawSectionHeader("Animation");
        gui.TextDisabled($"Duration: {clip.DurationSeconds:F2}s");
        gui.TextDisabled($"Channels (animated bones): {clip.Data.Channels.Length}");
        gui.TextDisabled($"Ticks/sec: {clip.TicksPerSecond:F0}");
        gui.Spacing();
        gui.TextWrapped("Assign this clip to an Animator on a skinned mesh, then use the Animator's " +
            "scrub slider to preview the pose.");
    }
}
