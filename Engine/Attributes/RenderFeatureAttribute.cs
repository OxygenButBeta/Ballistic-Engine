namespace BallisticEngine;

// Optional metadata for a RenderFeature (the authored custom-render-pass layer — phase 3 of the
// pass-graph migration, the engine's mirror of Unity URP's ScriptableRendererFeature). A feature is
// DISCOVERED without it (by base-type reflection in ComponentRegistry.Build, exactly like Behaviour /
// VolumeComponent); the attribute only customizes how the type appears in the editor's "Add Render
// Feature" menu. Plain System.Attribute with primitive args — ZERO ImGui/GL/DX12 — so a game can
// author a feature in GameScripts.dll against the engine library only (the seam decision, design §3).
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RenderFeatureAttribute : Attribute {
    public string DisplayName { get; }
    public string Menu { get; }

    // When true, the type is still REGISTERED (so existing scenes that reference it deserialize and
    // the backend bridge can resolve it) but does NOT appear in the editor's Add menu — for built-in
    // features that are wired automatically rather than hand-placed. Mirrors ComponentAttribute.
    public bool HideFromAddMenu { get; set; }

    public RenderFeatureAttribute(string displayName = null, string menu = null) {
        DisplayName = displayName;
        Menu = menu;
    }
}
