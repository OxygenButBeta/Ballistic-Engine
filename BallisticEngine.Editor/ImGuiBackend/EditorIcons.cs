using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal static class EditorIcons {
    public const string Add = "";
    public const string Cancel = "";
    public const string More = "";
    public const string Settings = "";
    public const string Search = "";
    public const string Camera = "";
    public const string Back = "";
    public const string Refresh = "";
    public const string Delete = "";
    public const string Save = "";
    public const string Cloud = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Stop = "";
    public const string ChevronDown = "";
    public const string ChevronRight = "";
    public const string ChevronUp = "";
    public const string Undo = "";
    public const string Redo = "";
    public const string Eye = "";
    public const string Package = "";
    public const string Warning = "";
    public const string Error = "";
    public const string Info = "";
    public const string Folder = "";
    public const string FolderOpen = "";
    public const string Document = "";
    public const string Picture = "";
    public const string Lightbulb = "";
    public const string Color = "";
    public const string Home = "";
    public const string Wrench = "";
    public const string Code = "";
    public const string Check = "";

    public const string Grid = "";
    public const string Pin = "";
    public const string World = "";
    public const string Lock = "";
    public const string LockOpen = "";
    public const string Maximize = "";
    public const string Minimize = "";

    public const string ProbeLight = Lightbulb;
    public const string ProbeReflection = Cloud;

    public const int RangeLow = 0xE04C;
    public const int RangeHigh = 0xE2A1;

    public static readonly SysVec4 AxisX = new(0.85f, 0.34f, 0.36f, 1f);
    public static readonly SysVec4 AxisY = new(0.49f, 0.74f, 0.30f, 1f);
    public static readonly SysVec4 AxisZ = new(0.29f, 0.56f, 0.88f, 1f);

    public static readonly SysVec4 TintLight = new(0.95f, 0.82f, 0.50f, 1f);
    public static readonly SysVec4 TintCamera = new(0.66f, 0.74f, 0.84f, 1f);
    public static readonly SysVec4 TintMesh = new(0.74f, 0.76f, 0.79f, 1f);
    public static readonly SysVec4 TintVolume = new(0.80f, 0.68f, 0.86f, 1f);
    public static readonly SysVec4 TintSky = new(0.68f, 0.79f, 0.80f, 1f);
    public static readonly SysVec4 TintGeneric = new(0.64f, 0.65f, 0.67f, 1f);

    public static (string icon, SysVec4 tint) ForEntity(Entity entity) {
        if (entity.GetComponent<HDCamera>() is not null) return (Camera, TintCamera);
        if (entity.GetComponent<DirectionalLight>() is not null ||
            entity.GetComponent<PointLight>() is not null ||
            entity.GetComponent<SpotLight>() is not null) return (Lightbulb, TintLight);
        if (entity.GetComponent<Renderer>() is not null) return (Package, TintMesh);
        return (Package, TintGeneric);
    }

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

    public static (string icon, SysVec4 tint) ForAssetExtension(string ext) => ext switch {
        ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae" => (Package, TintMesh),
        ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" => (Picture, new SysVec4(0.55f, 0.83f, 0.62f, 1f)),
        ".hdr" or ".exr" => (Picture, new SysVec4(0.95f, 0.80f, 0.45f, 1f)),
        ".wav" or ".wave" or ".ogg" => (Play, new SysVec4(0.93f, 0.55f, 0.72f, 1f)),
        ".mat" => (Color, TintVolume),
        ".volume" => (Color, new SysVec4(0.85f, 0.60f, 0.80f, 1f)),
        ".scene" or ".pyscene" => (Home, new SysVec4(0.93f, 0.65f, 0.45f, 1f)),
        ".shader" or ".glsl" => (Code, new SysVec4(0.45f, 0.80f, 0.83f, 1f)),
        ".cs" => (Code, new SysVec4(0.69f, 0.60f, 0.94f, 1f)),
        ".cubemap" => (Cloud, TintSky),
        ".terrain" => (Grid, new SysVec4(0.60f, 0.78f, 0.52f, 1f)),
        ".prefab" => (Package, new SysVec4(0.45f, 0.72f, 0.96f, 1f)),
        ".asset" => (Settings, new SysVec4(0.80f, 0.72f, 0.45f, 1f)),
        _ => (Document, TintGeneric),
    };

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

    public static float SmallButtonWidth(string icon) =>
        ImGui.CalcTextSize(icon).X + ImGui.GetStyle().FramePadding.X * 2;

    public static void DrawAt(SysVec2 pos, string icon, SysVec4 tint) {
        ImGui.GetWindowDrawList().AddText(pos, ImGui.GetColorU32(tint), icon);
    }
}
