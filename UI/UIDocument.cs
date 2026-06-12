using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace BallisticEngine.UI;

// The scene component that puts a UI on screen — Ballistic's analogue of Unity's UIDocument. Add it
// to an entity, point it at a .uxml (structure) and optional .uss (styles), and it builds the retained
// tree, runs the cascade, and each frame solves layout + dispatches pointer input. The (deferred)
// IUIRenderer walks every Active document and draws it.
//
// Render registration lives in OnAttach/OnDetach (BOTH edit and play modes) — per the engine's hard
// rule, registering in OnEnabled would leave the editor viewport blank. So the document joins the
// static Active set on attach and leaves it on detach, exactly like SceneLighting/Skybox.
//
// Asset text is pulled through an injected resolver (TextResolver) rather than a direct AssetDatabase
// call, because UI/ must not depend on AssetPipeline (layering). EngineBootstrap wires the resolver,
// same pattern as Physics.World / FalcorSceneImporter.Converter.
public class UIDocument : Behaviour
{
    // ---- the renderer's discovery list ----
    // Every attached UIDocument, in attach order. The UI render pass iterates this each frame. A list
    // (not a single Active) because multiple documents can be on screen at once (a HUD + a menu).
    static readonly List<UIDocument> _active = new();
    public static IReadOnlyList<UIDocument> Active => _active;

    // Resolves an "Assets/..."-style path to the asset's raw text (.uxml/.uss are native text assets).
    // Injected by EngineBootstrap so UI/ stays free of AssetPipeline. Returns null if unresolved.
    public static Func<string, string> TextResolver;

    // ---- authored references (serialized) ----

    // Project-relative paths to the structure + style assets. Strings (not a typed asset) so the
    // component serializes cleanly without UI/ pulling in the asset types; the resolver turns them
    // into text. Setting either at runtime and calling Rebuild() re-applies live.
    public string Uxml { get; set; }
    public string Uss { get; set; }

    // ---- presentation ----

    public enum RenderMode
    {
        // Flat full-screen overlay drawn on top of the scene, sized to the viewport. The default and
        // the only mode the first GL pass implements; covers all HUDs and menus.
        ScreenSpaceOverlay,
        // UI rendered on a quad in the 3D world (in-scene monitors, floating nameplates). Uses the
        // entity's transform + PanelSize as the quad's local pixel canvas. Renderer support is later.
        WorldSpace,
    }
    public RenderMode Mode { get; set; } = RenderMode.ScreenSpaceOverlay;

    public enum ScaleMode
    {
        // 1 USS px = 1 screen px. Crisp but tiny on 4K — offered, not the default.
        ConstantPixelSize,
        // Author against ReferenceResolution; the panel scales to fit the real viewport. Default so
        // ported designs (which assume a fixed canvas) look right on any display.
        ScaleWithScreenSize,
    }
    public ScaleMode Scale { get; set; } = ScaleMode.ScaleWithScreenSize;

    // The canvas the design was authored for (match the port skill's PanelSettings default).
    public Vector2 ReferenceResolution { get; set; } = new(1920, 1080);

    // 0 = match width, 1 = match height, 0.5 = blend — how ScaleWithScreenSize reconciles a viewport
    // whose aspect differs from the reference (the skill's match=0.5 default).
    public float MatchWidthOrHeight { get; set; } = 0.5f;

    // For WorldSpace: the panel's pixel canvas size (also the overlay's logical size before scaling).
    public Vector2 PanelSize { get; set; } = new(1920, 1080);

    // Higher draws later (on top) when multiple overlay documents stack.
    public int SortOrder { get; set; }

    // ---- runtime state (not serialized) ----

    [NotSerialized] public VisualElement Root { get; private set; }
    [NotSerialized] public float ResolvedScale { get; private set; } = 1f;

    readonly UIInputModule _input = new();
    StyleSheet _sheet;

    // Per-document tween engine for selection slides, fades, pulses (driven by the controller). Ticked
    // each frame in UpdateFrame before layout solves, so animated values are current when drawn.
    public UIAnimator Animator { get; } = new();

    // ---- lifecycle ----

    protected internal override void OnAttach()
    {
        _active.Add(this);
        Rebuild();
    }

    // Test/headless seam: register a document with a code-built tree directly into the Active set,
    // without an entity attach or the asset pipeline. Used by smoke tests and the renderer's UI
    // screenshot verification (no SceneManager dependency). Not for game code — real documents get
    // their tree from Uxml/Uss via OnAttach -> Rebuild.
    public static void RegisterForTest(UIDocument doc, VisualElement root)
    {
        if (doc == null || root == null) return;
        doc.Root = root;
        if (!_active.Contains(doc)) _active.Add(doc);
    }

    protected internal override void OnDetach()
    {
        _active.Remove(this);
        _input.Reset();
        Root = null;
    }

    // (Re)loads UXML + USS from their assets and rebuilds the tree. Safe to call anytime — e.g. after
    // changing Uxml/Uss at runtime, or on a hot-reload. No-ops gracefully when nothing's assigned.
    public void Rebuild()
    {
        if (string.IsNullOrEmpty(Uxml))
        {
            Root = null;
            return;
        }

        string uxmlText = ResolveText(Uxml);
        if (uxmlText == null)
        {
            Debugging.LogWarning($"UIDocument: could not load UXML '{Uxml}'.");
            Root = null;
            return;
        }

        // Build structure. UxmlLoader applies inline style="" onto each element DURING parse.
        Root = UxmlLoader.LoadFromText(uxmlText);
        if (Root == null) return;

        // Apply the stylesheet cascade. To preserve CSS precedence (inline > stylesheet), the loader
        // records each element's inline declarations and we re-assert them AFTER the sheet runs — so a
        // class rule can't clobber an explicit inline value. See UxmlLoader.CaptureInline.
        _sheet = string.IsNullOrEmpty(Uss) ? null : StyleSheet.Parse(ResolveText(Uss) ?? "");
        ApplyStyles();
    }

    // Re-runs the stylesheet cascade over the whole tree. Call after building dynamic content (rows,
    // lists) so elements added AFTER Rebuild() pick up their USS — otherwise their classes were added
    // after the initial cascade and they'd render unstyled. Public so controllers can invoke it.
    public void ApplyStyles()
    {
        if (Root == null) return;
        _sheet?.Apply(Root);
        // Inline wins: re-apply captured inline declarations last.
        ReassertInline(Root);
    }

    static void ReassertInline(VisualElement el)
    {
        if (el.InlineStyle != null)
            StyleApplier.ApplyInline(el.Style, el.InlineStyle);
        foreach (var c in el.Children)
            ReassertInline(c);
    }

    // ---- per-frame ----

    // Solves layout + advances animation for a render pass. `renderSize` is the pixel size of the
    // RENDER target (the offscreen game texture in the editor, the window in the player). Does NOT
    // process input — input needs the on-screen panel rect, which the renderer doesn't know; the host
    // calls ProcessInput separately. Kept as UpdateFrame for the renderer's existing call.
    public void UpdateFrame(Rect viewport) => UpdateFrame(viewport, 0f);

    public void UpdateFrame(Rect viewport, float dt)
    {
        if (Root == null) return;

        // Advance animations first so tweened/looped values are current before layout + draw.
        if (dt > 0f) Animator.Tick(dt);

        // Compute the logical canvas size + scale from the scale mode, then solve layout against the
        // LOGICAL size so authored px line up with the reference resolution; the renderer applies
        // ResolvedScale as a uniform transform.
        Vector2 logical = ComputeLogicalSize(viewport.Size, out float scale);
        ResolvedScale = scale;
        LogicalSize = logical;

        // Root fills the logical canvas unless the design set explicit root dimensions.
        LayoutPass.Solve(Root, logical.X, logical.Y);
    }

    // The logical canvas size from the last solve (pixels). Hosts need it to map screen-space mouse
    // coords into the UI's logical space for input.
    [NotSerialized] public Vector2 LogicalSize { get; private set; }

    // Processes pointer input against the laid-out tree. `panelScreenRect` is where the UI's render
    // surface sits ON SCREEN in the SAME coordinate space as Input.MousePosition (the whole window for
    // the player; the Game-view image's screen rect for the editor). The mouse is mapped from that rect
    // into the UI's logical space (so a UI authored at 1920×1080 hit-tests correctly regardless of the
    // panel's on-screen size/offset). Call once per frame after UpdateFrame, only when input should be
    // routed to the UI (e.g. editor: play mode + Game-view focused; player: always).
    public void ProcessInput(Rect panelScreenRect)
    {
        if (Root == null) return;
        _input.Update(Root, panelScreenRect, LogicalSize);
    }

    Vector2 ComputeLogicalSize(Vector2 viewportPx, out float scale)
    {
        if (Mode == RenderMode.WorldSpace || Scale == ScaleMode.ConstantPixelSize)
        {
            scale = 1f;
            return Mode == RenderMode.WorldSpace ? PanelSize : viewportPx;
        }

        // ScaleWithScreenSize: pick a scale that maps the reference resolution onto the viewport,
        // blending the width- and height-derived scales by MatchWidthOrHeight (log-blend like Unity
        // so a 0.5 match feels balanced rather than width-biased).
        Vector2 reference = ReferenceResolution;
        float scaleW = viewportPx.X / Math.Max(1f, reference.X);
        float scaleH = viewportPx.Y / Math.Max(1f, reference.Y);
        float logW = MathF.Log(Math.Max(1e-4f, scaleW));
        float logH = MathF.Log(Math.Max(1e-4f, scaleH));
        scale = MathF.Exp(logW * (1f - MatchWidthOrHeight) + logH * MatchWidthOrHeight);

        // The logical canvas is the viewport divided by the scale, so it covers the whole screen.
        return new Vector2(viewportPx.X / scale, viewportPx.Y / scale);
    }

    static string ResolveText(string path) => TextResolver?.Invoke(path);
}
