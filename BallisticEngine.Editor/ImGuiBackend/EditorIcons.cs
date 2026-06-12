using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Icon glyphs from Lucide (bundled lucide.ttf), merged into the UI font atlas by ImGuiController.
// Use them inline in any label string. Codepoints are written as \uXXXX escapes (ASCII-safe source,
// no multibyte literals) from Lucide's PUA block; see Assets/Fonts/lucide.ttf. The merged glyph
// range in ImGuiController must cover these (0xE04C–0xE2A1).
internal static class EditorIcons {
    public const string Add = "";          // lucide plus
    public const string Cancel = "";       // lucide x
    public const string More = "";         // lucide ellipsis ("..." menu)
    public const string Settings = "";     // lucide settings
    public const string Search = "";       // lucide search
    public const string Camera = "";       // lucide camera
    public const string Back = "";         // lucide arrow-left
    public const string Refresh = "";      // lucide refresh-cw
    public const string Delete = "";       // lucide trash-2
    public const string Save = "";         // lucide save
    public const string Cloud = "";        // lucide cloud (skybox / environment)
    public const string Play = "";         // lucide play
    public const string Pause = "";        // lucide pause
    public const string Stop = "";         // lucide square
    public const string ChevronDown = "";  // lucide chevron-down
    public const string ChevronRight = ""; // lucide chevron-right
    public const string Undo = "";         // lucide undo-2
    public const string Redo = "";         // lucide redo-2
    public const string Eye = "";          // lucide eye (visibility toggle)
    public const string Package = "";      // lucide box (meshes / 3D objects)
    public const string Warning = "";      // lucide triangle-alert
    public const string Error = "";        // lucide circle-x
    public const string Info = "";         // lucide info
    public const string Folder = "";       // lucide folder
    public const string FolderOpen = "";   // lucide folder-open
    public const string Document = "";     // lucide file
    public const string Picture = "";      // lucide image
    public const string Lightbulb = "";    // lucide lightbulb (lights)
    public const string Color = "";        // lucide palette (materials / volume profiles)
    public const string Home = "";         // lucide house
    public const string Wrench = "";       // lucide wrench (generic component)
    public const string Code = "";         // lucide code (shaders)
    public const string Check = "";        // lucide check

    public const string Grid = "";   // lucide grid-3x3 (grid toggle)
    public const string Pin = "";    // lucide map-pin (component gizmos toggle)
    public const string World = "";  // lucide globe (world gizmo space)
    public const string Lock = "";   // lucide lock (U+E10F)
    public const string LockOpen = ""; // lucide lock-open (U+E110)
    public const string Maximize = "";  // lucide maximize (U+E116)
    public const string Minimize = "";  // lucide minimize (U+E11E)

    // The contiguous glyph range to bake (smallest..largest of the codepoints above), used by
    // ImGuiController when merging lucide.ttf. Keep in sync if icons outside this range are added.
    public const int RangeLow = 0xE04C;
    public const int RangeHigh = 0xE2A1;

    // Unity-ish axis colors for X/Y/Z drag chips.
    public static readonly SysVec4 AxisX = new(0.85f, 0.34f, 0.36f, 1f);
    public static readonly SysVec4 AxisY = new(0.49f, 0.74f, 0.30f, 1f);
    public static readonly SysVec4 AxisZ = new(0.29f, 0.56f, 0.88f, 1f);

    // Soft category tints used for entity/component icons so the UI reads at a glance.
    public static readonly SysVec4 TintLight = new(0.98f, 0.83f, 0.45f, 1f);
    public static readonly SysVec4 TintCamera = new(0.55f, 0.78f, 0.98f, 1f);
    public static readonly SysVec4 TintMesh = new(0.70f, 0.76f, 0.86f, 1f);
    public static readonly SysVec4 TintVolume = new(0.78f, 0.60f, 0.93f, 1f);
    public static readonly SysVec4 TintSky = new(0.56f, 0.82f, 0.90f, 1f);
    public static readonly SysVec4 TintGeneric = new(0.58f, 0.63f, 0.72f, 1f);

    // Icon + tint describing an entity by its "most interesting" component (Unity-style).
    public static (string icon, SysVec4 tint) ForEntity(Entity entity) {
        if (entity.GetComponent<HDCamera>() is not null) return (Camera, TintCamera);
        if (entity.GetComponent<DirectionalLight>() is not null ||
            entity.GetComponent<PointLight>() is not null ||
            entity.GetComponent<SpotLight>() is not null) return (Lightbulb, TintLight);
        if (entity.GetComponent<Renderer>() is not null) return (Package, TintMesh);
        return (Package, TintGeneric);
    }

    // Icon + tint for a component type shown in the Inspector / hierarchy Scene tab.
    public static (string icon, SysVec4 tint) ForComponentType(Type type) {
        if (typeof(HDCamera).IsAssignableFrom(type)) return (Camera, TintCamera);
        if (type.Name.Contains("Light", StringComparison.Ordinal)) return (Lightbulb, TintLight);
        if (typeof(Renderer).IsAssignableFrom(type)) return (Package, TintMesh);
        if (typeof(Volume).IsAssignableFrom(type) || type.Name.Contains("Volume", StringComparison.Ordinal))
            return (Color, TintVolume);
        if (type.Name.Contains("Sky", StringComparison.Ordinal) ||
            type.Name.Contains("Fog", StringComparison.Ordinal)) return (Cloud, TintSky);
        return (Wrench, TintGeneric);
    }

    // Icon + tint for an asset file extension (browser tiles, picker rows, inspector header).
    public static (string icon, SysVec4 tint) ForAssetExtension(string ext) => ext switch {
        ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae" => (Package, TintMesh),
        ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" => (Picture, new SysVec4(0.55f, 0.83f, 0.62f, 1f)),
        ".hdr" or ".exr" => (Picture, new SysVec4(0.95f, 0.80f, 0.45f, 1f)),
        ".mat" => (Color, TintVolume),
        ".volume" => (Color, new SysVec4(0.85f, 0.60f, 0.80f, 1f)),
        ".scene" or ".pyscene" => (Home, new SysVec4(0.93f, 0.65f, 0.45f, 1f)),
        ".shader" or ".glsl" => (Code, new SysVec4(0.45f, 0.80f, 0.83f, 1f)),
        ".cs" => (Code, new SysVec4(0.69f, 0.60f, 0.94f, 1f)),
        ".cubemap" => (Cloud, TintSky),
        ".terrain" => (Grid, new SysVec4(0.60f, 0.78f, 0.52f, 1f)),
        ".prefab" => (Package, new SysVec4(0.45f, 0.72f, 0.96f, 1f)),   // blue box (Unity prefab blue)
        ".asset" => (Settings, new SysVec4(0.80f, 0.72f, 0.45f, 1f)),   // DataAsset (ScriptableObject)
        _ => (Document, TintGeneric),
    };

    // An icon-only button with a transparent background (toolbar / row actions).
    public static bool GhostButton(string id, string icon, string tooltip = null, float width = 0) {
        ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(0, 0, 0, 0));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(1, 1, 1, 0.08f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new SysVec4(1, 1, 1, 0.14f));
        var clicked = ImGui.Button($"{icon}##{id}", new SysVec2(width, 0));
        ImGui.PopStyleColor(3);
        if (tooltip is not null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    // Frameless icon-only button at text height (in-row actions like the visibility eye).
    public static bool GhostButtonSmall(string id, string icon, string tooltip = null) {
        ImGui.PushStyleColor(ImGuiCol.Button, new SysVec4(0, 0, 0, 0));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new SysVec4(1, 1, 1, 0.10f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new SysVec4(1, 1, 1, 0.16f));
        var clicked = ImGui.SmallButton($"{icon}##{id}");
        ImGui.PopStyleColor(3);
        if (tooltip is not null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    // Width a small ghost icon button will occupy (for right-edge alignment before drawing).
    public static float SmallButtonWidth(string icon) =>
        ImGui.CalcTextSize(icon).X + ImGui.GetStyle().FramePadding.X * 2;

    // Draws an icon glyph at the given screen position via the draw list (no layout impact).
    public static void DrawAt(SysVec2 pos, string icon, SysVec4 tint) {
        ImGui.GetWindowDrawList().AddText(pos, ImGui.GetColorU32(tint), icon);
    }
}
