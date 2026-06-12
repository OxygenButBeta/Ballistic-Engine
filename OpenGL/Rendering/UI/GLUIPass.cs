using System;
using System.Collections.Generic;
using BallisticEngine.UI;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

// The OpenGL backend for the UI layer: implements IUIRenderer by drawing each primitive as a quad in
// a screen-space orthographic pass. Invoked AFTER the composite pass (so the UI sits on top of the
// final tonemapped image) into the same target — see GLHDRenderer wiring. Text is NOT yet drawn here
// (SDF atlas is a follow-up); DrawText is a documented no-op so a HUD's boxes/images render now.
//
// Coordinate space: panel/logical pixels, top-left origin, +Y down. The ortho projection maps that
// to clip space; UIDocument.ResolvedScale is folded into the projection so authored px scale to the
// real viewport. Clipping uses GL.Scissor (intersected as the clip stack pushes/pops).
//
// One draw call per primitive in v1 (simple + correct); batching is a later optimization. State is
// saved/restored around the pass so it composes with the 3D renderer (and the ImGui editor) without
// leaking blend/scissor/program state — the same discipline the composite pass uses.
public sealed class GLUIPass : IUIRenderer, IDisposable
{
    readonly StandardShader _shader;
    readonly StandardShader _textShader;
    readonly StandardShader _gradientShader;
    int _vao, _vbo;

    // GL uploads of the registered UI fonts (atlas texture + layout), keyed by family name. Lazily
    // (re)built from the CPU atlases in UIFonts whenever its Version changes — EngineBootstrap bakes/
    // registers them; the UI layer never touches GL. Text with an unknown/empty family uses Default.
    readonly System.Collections.Generic.Dictionary<string, GLUIFont> _fonts =
        new(System.StringComparer.OrdinalIgnoreCase);
    GLUIFont _defaultFont;
    int _fontVersion = -1;

    static GLUIFont _overrideFont;
    public static void SetDefaultFont(GLUIFont font) => _overrideFont = font;

    // Rebuilds the GL font cache from UIFonts when its registry changed. Cheap when unchanged.
    void EnsureFonts()
    {
        if (_overrideFont != null) { _defaultFont = _overrideFont; return; }
        if (UIFonts.Version == _fontVersion) return;
        _fontVersion = UIFonts.Version;

        foreach (var f in _fonts.Values) f.Dispose();
        _fonts.Clear();
        foreach (var kv in UIFonts.All)
            if (kv.Value != null) _fonts[kv.Key] = new GLUIFont(kv.Value);

        _defaultFont = UIFonts.Default != null ? new GLUIFont(UIFonts.Default) : null;
    }

    // Resolves a family name to its uploaded GL font, falling back to the default.
    GLUIFont ResolveFont(string family)
    {
        if (!string.IsNullOrEmpty(family) && _fonts.TryGetValue(family, out var f)) return f;
        return _defaultFont;
    }

    Matrix4 _proj;
    Vector2 _viewportSize; // the actual target pixel size, for scissor Y-flip
    float _scale = 1f;     // logical->device pixel ratio (UIDocument.ResolvedScale)
    bool _scissorWasEnabled;

    // Clip stack in panel pixels; each entry is the INTERSECTION up to that depth, so PopClip just
    // restores the previous scissor without recomputing.
    readonly List<Rect> _clipStack = new();

    public GLUIPass()
    {
        _shader = GraphicAPI.CreateStandardShader(
            EmbeddedShaderSource.Read("UI_Rect_Vert.glsl"),
            EmbeddedShaderSource.Read("UI_Rect_Frag.glsl"));
        _textShader = GraphicAPI.CreateStandardShader(
            EmbeddedShaderSource.Read("UI_Text_Vert.glsl"),
            EmbeddedShaderSource.Read("UI_Text_Frag.glsl"));
        _gradientShader = GraphicAPI.CreateStandardShader(
            EmbeddedShaderSource.Read("UI_Rect_Vert.glsl"),     // shares the rect vertex stage
            EmbeddedShaderSource.Read("UI_Gradient_Frag.glsl"));
        CreateQuad();
    }

    // A unit quad [0,1]x[0,1] as a triangle strip (TL, BL, TR, BR) — one static VBO reused for every
    // primitive; the per-quad rect/transform comes from uniforms.
    void CreateQuad()
    {
        float[] corners = { 0f, 0f,  0f, 1f,  1f, 0f,  1f, 1f };
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, corners.Length * sizeof(float), corners, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    public void Begin(Vector2 canvasSize, float scale)
    {
        // canvasSize is the LOGICAL panel size; the target is canvasSize*scale pixels. Build an ortho
        // that maps logical (0,0)-(w,h) with top-left origin to clip space, baking in the scale so a
        // logical pixel lands on scale device pixels.
        _viewportSize = canvasSize * scale;
        _scale = scale <= 0f ? 1f : scale;
        // Top-left origin: y grows downward, so map y=0 -> +1 (top), y=h -> -1 (bottom).
        _proj = Matrix4.CreateOrthographicOffCenter(0f, canvasSize.X, canvasSize.Y, 0f, -1f, 1f);

        // Save the GL state we touch and set up alpha blending; the UI draws straight-alpha over the
        // already-composited scene.
        _scissorWasEnabled = GL.IsEnabled(EnableCap.ScissorTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        // PREMULTIPLIED-alpha blend: the fragment shader outputs rgb already multiplied by alpha, so
        // src factor is One (not SrcAlpha). This is what makes antialiased rounded edges + borders
        // composite over the scene without color fringing (straight-alpha leaked the border's RGB at
        // partial coverage — the pink-halo bug).
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

        EnsureFonts();

        _shader.Activate();
        _shader.SetMatrix4("uProj", ref _proj);
        _clipStack.Clear();
    }

    public void End()
    {
        GL.BindVertexArray(0);
        GL.Disable(EnableCap.Blend);
        if (_scissorWasEnabled) GL.Enable(EnableCap.ScissorTest);
        else GL.Disable(EnableCap.ScissorTest);
        // ImGui-backend rule (CLAUDE.md): leave Texture0 active so the next consumer doesn't sample a
        // stale unit.
        GL.ActiveTexture(TextureUnit.Texture0);
    }

    public void DrawRect(Rect rect, Color fill, Vector4 radius, float borderWidth, Color borderColor)
    {
        _shader.Activate();
        _shader.SetFloat4("uRect", new Vector4(rect.X, rect.Y, rect.Width, rect.Height));
        _shader.SetFloat2("uSize", new Vector2(rect.Width, rect.Height));
        _shader.SetFloat4("uFill", fill.ToVector4());
        _shader.SetFloat4("uBorderColor", borderColor.ToVector4());
        _shader.SetFloat("uBorderWidth", borderWidth);
        _shader.SetFloat4("uRadius", radius);
        _shader.SetInt("uHasTexture", 0);
        DrawQuad();
    }

    public void DrawGradient(Rect rect, Gradient gradient, Vector4 radius, float opacity)
    {
        if (gradient == null || gradient.Stops.Count == 0) return;

        _gradientShader.Activate();
        _gradientShader.SetMatrix4("uProj", ref _proj);
        _gradientShader.SetFloat4("uRect", new Vector4(rect.X, rect.Y, rect.Width, rect.Height));
        _gradientShader.SetFloat2("uSize", new Vector2(rect.Width, rect.Height));
        _gradientShader.SetFloat4("uRadius", radius);
        _gradientShader.SetFloat("uOpacity", opacity);

        _gradientShader.SetInt("uKind", gradient.Type == Gradient.Kind.Radial ? 1 : 0);
        _gradientShader.SetFloat("uAngle", gradient.AngleDegrees * (MathF.PI / 180f));
        _gradientShader.SetFloat2("uCenter", new Vector2(gradient.CenterX, gradient.CenterY));
        _gradientShader.SetFloat2("uRadii", new Vector2(gradient.RadiusX, gradient.RadiusY));

        int n = Math.Min(gradient.Stops.Count, 8);
        _gradientShader.SetInt("uStopCount", n);
        for (int i = 0; i < n; i++)
        {
            var stop = gradient.Stops[i];
            _gradientShader.SetFloat4($"uStopColor[{i}]", stop.Color.ToVector4());
            _gradientShader.SetFloat($"uStopPos[{i}]", stop.Position);
        }

        DrawQuad();
    }

    public void DrawImage(Rect rect, object texture, Color tint, ScaleMode scaleMode)
    {
        int tex = ResolveTexture(texture);
        if (tex == 0)
        {
            // No GL texture resolved — fall back to a tinted solid so the slot is visible (and the
            // layout is verifiable) rather than nothing.
            DrawRect(rect, tint, Vector4.Zero, 0f, Color.Transparent);
            return;
        }

        _shader.Activate();
        _shader.SetFloat4("uRect", new Vector4(rect.X, rect.Y, rect.Width, rect.Height));
        _shader.SetFloat2("uSize", new Vector2(rect.Width, rect.Height));
        _shader.SetFloat4("uFill", tint.ToVector4());
        _shader.SetFloat("uBorderWidth", 0f);
        _shader.SetFloat4("uRadius", Vector4.Zero);
        _shader.SetInt("uHasTexture", 1);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, tex);
        _shader.SetInt("uTexture", 0);
        DrawQuad();
    }

    // Draws a line of SDF text within rect, styled by `ts` (font family, size, alignment, letter
    // spacing, optional glow/shadow). Draws the shadow/glow pass first (offset, spread), then the main
    // text on top. Silent no-op when no font is available.
    public void DrawText(Rect rect, string text, in TextStyle ts)
    {
        var font = ResolveFont(ts.FontFamily);
        if (font == null || string.IsNullOrEmpty(text)) return;

        _textShader.Activate();
        _textShader.SetMatrix4("uProj", ref _proj);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, font.Texture);
        _textShader.SetInt("uAtlas", 0);
        GL.BindVertexArray(_vao);

        // Shadow / glow pass: same glyphs, offset, in the shadow colour, with a wider SDF edge so a
        // large blur reads as a soft halo. uSpread expands the sampled coverage outward.
        if (ts.HasShadow && ts.ShadowColor.A > 0f)
        {
            var shadowRect = new Rect(rect.X + ts.ShadowOffsetX, rect.Y + ts.ShadowOffsetY, rect.Width, rect.Height);
            _textShader.SetFloat4("uColor", ts.ShadowColor.ToVector4());
            // Map the CSS blur radius to a SMALL SDF spread. The glow must stay inside the glyph's tight
            // quad (the quad isn't expanded), so a large spread would clip into a box — cap it low so it
            // reads as a soft edge-glow rather than a rectangle. Drop-shadows (small blur) are unaffected.
            _textShader.SetFloat("uSpread", System.Math.Min(0.12f, ts.ShadowBlur * 0.004f));
            font.Layout(text, ts.FontSize, shadowRect, ts.Align, ts.LetterSpacing, g =>
            {
                _textShader.SetFloat4("uRect", new Vector4(g.X, g.Y, g.W, g.H));
                _textShader.SetFloat4("uUv", new Vector4(g.U0, g.V0, g.U1, g.V1));
                GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            });
        }

        // Main text pass.
        _textShader.SetFloat4("uColor", ts.Color.ToVector4());
        _textShader.SetFloat("uSpread", 0f);
        font.Layout(text, ts.FontSize, rect, ts.Align, ts.LetterSpacing, g =>
        {
            _textShader.SetFloat4("uRect", new Vector4(g.X, g.Y, g.W, g.H));
            _textShader.SetFloat4("uUv", new Vector4(g.U0, g.V0, g.U1, g.V1));
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        });
    }

    public void PushClip(Rect rect)
    {
        Rect clip = _clipStack.Count > 0 ? Intersect(_clipStack[^1], rect) : rect;
        _clipStack.Add(clip);
        ApplyScissor(clip);
    }

    public void PopClip()
    {
        if (_clipStack.Count == 0) return;
        _clipStack.RemoveAt(_clipStack.Count - 1);
        if (_clipStack.Count > 0) ApplyScissor(_clipStack[^1]);
        else GL.Disable(EnableCap.ScissorTest);
    }

    // --- helpers ---

    void DrawQuad()
    {
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
    }

    void ApplyScissor(Rect clip)
    {
        GL.Enable(EnableCap.ScissorTest);
        // Scissor is in DEVICE pixels with a BOTTOM-left origin. Our clip is in logical pixels, top-
        // left origin, so scale by _scale and flip Y against the device viewport height.
        int x = (int)MathF.Floor(clip.X * _scale);
        int w = (int)MathF.Ceiling(clip.Width * _scale);
        int h = (int)MathF.Ceiling(clip.Height * _scale);
        int yTop = (int)MathF.Floor(clip.Y * _scale);
        int yBottomFlipped = (int)MathF.Round(_viewportSize.Y) - (yTop + h);
        GL.Scissor(x, Math.Max(0, yBottomFlipped), Math.Max(0, w), Math.Max(0, h));
    }

    static Rect Intersect(Rect a, Rect b)
    {
        float x1 = Math.Max(a.Left, b.Left);
        float y1 = Math.Max(a.Top, b.Top);
        float x2 = Math.Min(a.Right, b.Right);
        float y2 = Math.Min(a.Bottom, b.Bottom);
        return new Rect(x1, y1, Math.Max(0f, x2 - x1), Math.Max(0f, y2 - y1));
    }

    // Resolves Image.Texture (an opaque UI-layer handle) to a GL texture id. Accepts a raw GL id
    // (int), an engine Texture2D (its .UID), or an "Assets/..." path string (loaded once via
    // AssetDatabase and cached). Returns 0 when nothing usable, so the caller draws a tinted fallback.
    static readonly System.Collections.Generic.Dictionary<string, int> _pathTexCache = new();

    static int ResolveTexture(object texture)
    {
        switch (texture)
        {
            case int id:
                return id;
            case Texture tex:
                return tex.UID;
            case string path when !string.IsNullOrEmpty(path):
                if (_pathTexCache.TryGetValue(path, out var cached)) return cached;
                var loaded = AssetDatabase.Load<Texture2D>(path);
                int uid = loaded?.UID ?? 0;
                _pathTexCache[path] = uid; // cache even 0 so we don't retry a bad path every frame
                return uid;
            default:
                return 0;
        }
    }

    public void Dispose()
    {
        if (_vbo != 0) { GL.DeleteBuffer(_vbo); _vbo = 0; }
        if (_vao != 0) { GL.DeleteVertexArray(_vao); _vao = 0; }
    }
}
