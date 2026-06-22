using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.AssetInspectors.AssetInspectorGuiAccess;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

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
