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
    public void Draw(in AssetInspectorContext ctx) =>
        InspectorPanel.DrawTextureImportSettings(ctx.Path, ctx.Guid, ctx.Meta);
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

        var changed = false;
        if (InspectorPanel.BeginGrid("##matslots")) {
            foreach (TextureType slot in new[] {
                         TextureType.Diffuse, TextureType.Normal, TextureType.Metallic,
                         TextureType.Roughness, TextureType.AO, TextureType.Emissive,
                     }) {
                definition.Textures.TryGetValue(slot.ToString(), out var reference);
                var display = reference is null
                    ? "None"
                    : Path.GetFileName(ReferenceToPath(reference) ?? reference);

                InspectorPanel.Row(slot.ToString());
                ImGui.PushID((int)slot);
                if (reference is null)
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                ImGui.Button(display, new SysVec2(-1, 0));
                if (reference is null)
                    ImGui.PopStyleColor();
                if (InspectorPanel.AcceptGuidDrop(out Guid dropped)) {
                    definition.Textures[slot.ToString()] = AssetRef.FromGuid(dropped);
                    changed = true;
                }
                ImGui.PopID();
            }

            // Scalar material properties (stored in the .mat next to the texture refs).
            // Base color: linear RGBA tint multiplying the albedo map (glTF baseColorFactor).
            // White is the neutral "unstated" default, so it's stored as null and rendering
            // is bit-identical to a .mat without the key.
            InspectorPanel.Row("Base Color");
            var baseColor = definition.BaseColor switch {
                { Length: >= 4 } bc => new SysVec4(bc[0], bc[1], bc[2], bc[3]),
                { Length: 3 } bc => new SysVec4(bc[0], bc[1], bc[2], 1f),
                _ => SysVec4.One,
            };
            if (ImGui.ColorEdit4("##matbasecolor", ref baseColor)) {
                definition.BaseColor = baseColor == SysVec4.One
                    ? null
                    : [baseColor.X, baseColor.Y, baseColor.Z, baseColor.W];
                changed = true;
            }

            // Packed ORM: metallic texture carries (occlusion, roughness, metallic) in RGB.
            // Auto-detected from "spec" file names when the .mat doesn't say explicitly.
            InspectorPanel.Row("Packed ORM");
            var packedOrm = MaterialLoader.ResolvePackedOrm(definition);
            if (ImGui.Checkbox("##matpackedorm", ref packedOrm)) {
                definition.PackedOrm = packedOrm;
                changed = true;
            }

            // Alpha cutout: discard below 0.5 diffuse alpha + double-sided (foliage cards).
            // Auto-detected from foliage-style texture names when not set explicitly.
            InspectorPanel.Row("Alpha Cutout");
            var cutout = MaterialLoader.ResolveCutout(definition);
            if (ImGui.Checkbox("##matcutout", ref cutout)) {
                definition.Cutout = cutout;
                changed = true;
            }

            InspectorPanel.Row("Transparent");
            var transparent = definition.Transparent;
            if (ImGui.Checkbox("##mattransparent", ref transparent)) {
                definition.Transparent = transparent;
                changed = true;
            }

            if (definition.Transparent) {
                InspectorPanel.Row("Opacity");
                var opacity = definition.Opacity;
                if (ImGui.SliderFloat("##matopacity", ref opacity, 0f, 1f)) {
                    definition.Opacity = opacity;
                    changed = true;
                }
            }

            InspectorPanel.Row("Emissive Color");
            var emissive = definition.EmissiveColor is { Length: >= 3 } c
                ? new SysVec3(c[0], c[1], c[2])
                : SysVec3.One;
            if (ImGui.ColorEdit3("##matemissivecolor", ref emissive)) {
                definition.EmissiveColor = [emissive.X, emissive.Y, emissive.Z];
                changed = true;
            }

            InspectorPanel.Row("Emissive Intensity");
            var emissivemntensity = definition.EmissiveIntensity;
            if (ImGui.DragFloat("##matemissiveintensity", ref emissivemntensity, 0.05f, 0f, 100f)) {
                definition.EmissiveIntensity = emissivemntensity;
                changed = true;
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
    public void Draw(in AssetInspectorContext ctx) =>
        InspectorPanel.DrawVolumeProfileAsset(ctx.Guid);
}

[AssetInspector(".scene")]
internal sealed class SceneAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        InspectorPanel.DrawSceneAssetActions(ctx.Path);
}

[AssetInspector(".pyscene")]
internal sealed class PysceneAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        InspectorPanel.DrawPysceneHint();
}

// Native text assets — a hint + Show-in-Explorer. Covers the .shader/.glsl/.cubemap group.
[AssetInspector(".shader")]
[AssetInspector(".glsl")]
[AssetInspector(".cubemap")]
internal sealed class TextAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        InspectorPanel.DrawTextAssetHint(ctx.Path);
}

[AssetInspector(".prefab")]
internal sealed class PrefabAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        ctx.Panel.DrawPrefabInspector(ctx.Path);
}

[AssetInspector(".asset")]
internal sealed class DataAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        ctx.Panel.DrawDataAssetInspector(ctx.Path);
}

[AssetInspector(".wav")]
[AssetInspector(".wave")]
[AssetInspector(".ogg")]
internal sealed class AudioClipAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        ctx.Panel.DrawAudioClipAsset(ctx.Path);
}

[AssetInspector(".banim")]
internal sealed class AnimationClipAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        ctx.Panel.DrawAnimationClipAsset(ctx.Path);
}
