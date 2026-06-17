using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Reusable inspector widgets (editor-rework Rule 1 / Phase B3). These four interactive editors —
// the AnimationCurve editor, the ColorGradient bar, the audio-clip scrubber, and the Animator pose
// scrubber — used to live INLINE in InspectorPanel (private statics, hand-rolled, predating the drawer
// pipeline). B3 lifts them VERBATIM into the shared EditorWidgets library (same home as ToggleSwitch /
// DropShadow) so any caller — the inspector member drawer, an asset view, a future terminal drawer in
// the B0 stack, the standalone CurveEditorWindow — can reuse them without going through InspectorPanel.
//
// ★ BYTE-IDENTICAL by construction: the bodies are MOVED unchanged — same ImGui call sequence, same draw
//   math, same undo-Push sites. The only structural change is that the per-widget mutable state the audio
//   and animator scrubbers need (the live voice, the scrub/preview time, the play toggle) is now passed
//   by `ref` instead of being a static field, so the WIDGET owns no shared state and the CALLER decides
//   where that state lives (InspectorPanel keeps its existing statics — they're shared with the audio
//   asset view — and just hands them in by ref). The curve/gradient editors keep their own drag-tracking
//   statics here (single-widget-at-a-time assumption, exactly as before).
internal static partial class EditorWidgets {

    // ---- AnimationCurve editor ---------------------------------------------------
    // An interactive curve widget: a plot box that samples the curve into a polyline, draggable
    // keyframe dots (drag to move time+value), double-click empty space to add a key, right-click a
    // key to remove it, and preset buttons (Linear / Ease / Constant). Reusable for ANY AnimationCurve
    // member. The curve is mutated in place; returns true when an edit happened (caller marks dirty).
    // The plot auto-fits its value range to the keys (with a small pad) so any amplitude is visible.
    static int curveDragKey = -1; // index of the key being dragged (-1 = none); single-widget assumption

    public static bool CurveEditor(string id, AnimationCurve curve, Action onExternalEdit = null) {
        bool edited = false;
        ImGui.PushID(id);

        float w = ImGui.GetContentRegionAvail().X;
        const float height = 90f;
        SysVec2 origin = ImGui.GetCursorScreenPos();
        var size = new SysVec2(MathF.Max(w, 60f), height);
        var draw = ImGui.GetWindowDrawList();

        // Background + border.
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new SysVec4(0.10f, 0.11f, 0.13f, 1f)), 4f);
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(new SysVec4(0.30f, 0.32f, 0.36f, 1f)), 4f);

        // Time range = [first key, last key] (default [0,1]); value range auto-fits the keys.
        float t0 = 0f, t1 = 1f, vMin = 0f, vMax = 1f;
        if (curve.Count > 0) {
            t0 = curve.Keys[0].Time;
            t1 = curve.Keys[curve.Count - 1].Time;
            vMin = float.MaxValue; vMax = float.MinValue;
            for (var i = 0; i < curve.Count; i++) {
                vMin = MathF.Min(vMin, curve.Keys[i].Value);
                vMax = MathF.Max(vMax, curve.Keys[i].Value);
            }
        }
        if (t1 <= t0) t1 = t0 + 1f;
        if (vMax <= vMin) { vMin -= 0.5f; vMax += 0.5f; }
        float vPad = (vMax - vMin) * 0.12f;
        vMin -= vPad; vMax += vPad;

        SysVec2 ToScreen(float time, float value) {
            float fx = (time - t0) / (t1 - t0);
            float fy = (value - vMin) / (vMax - vMin);
            return new SysVec2(origin.X + fx * size.X, origin.Y + (1f - fy) * size.Y);
        }

        // Zero line (if 0 is in the value range) for reference.
        if (vMin < 0f && vMax > 0f) {
            float zy = origin.Y + (1f - (0f - vMin) / (vMax - vMin)) * size.Y;
            draw.AddLine(new SysVec2(origin.X, zy), new SysVec2(origin.X + size.X, zy),
                ImGui.GetColorU32(new SysVec4(0.4f, 0.4f, 0.45f, 0.4f)));
        }

        // Sample the curve into a polyline across the box width.
        const int Samples = 64;
        uint curveColor = ImGui.GetColorU32(new SysVec4(0.45f, 0.85f, 1f, 1f));
        SysVec2 prev = default;
        for (var s = 0; s <= Samples; s++) {
            float time = t0 + (t1 - t0) * s / Samples;
            SysVec2 p = ToScreen(time, curve.Evaluate(time));
            if (s > 0) draw.AddLine(prev, p, curveColor, 2f);
            prev = p;
        }

        // An invisible button over the box captures interaction (hover/click/drag).
        ImGui.InvisibleButton("##curvebox", size);
        bool hovered = ImGui.IsItemHovered();
        SysVec2 mouse = ImGui.GetMousePos();

        float SnapTimeFromMouse() => t0 + (t1 - t0) * Math.Clamp((mouse.X - origin.X) / size.X, 0f, 1f);
        float SnapValueFromMouse() => vMax - (vMax - vMin) * Math.Clamp((mouse.Y - origin.Y) / size.Y, 0f, 1f);

        // Draw + hit-test keyframe dots.
        const float dotR = 5f;
        int hoverKey = -1;
        for (var i = 0; i < curve.Count; i++) {
            SysVec2 sp = ToScreen(curve.Keys[i].Time, curve.Keys[i].Value);
            bool near = (mouse - sp).LengthSquared() <= (dotR + 3f) * (dotR + 3f);
            if (near && hovered) hoverKey = i;
            uint dc = (i == curveDragKey || near)
                ? ImGui.GetColorU32(new SysVec4(1f, 0.85f, 0.3f, 1f))
                : ImGui.GetColorU32(new SysVec4(1f, 1f, 1f, 1f));
            draw.AddCircleFilled(sp, dotR, dc);
        }

        // Begin a drag on a key (snapshot for undo once).
        if (hovered && hoverKey >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
            curveDragKey = hoverKey;
            EditorUndo.Push($"Edit {id}");
        }
        // Drag the held key.
        if (curveDragKey >= 0 && curveDragKey < curve.Count && ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            curveDragKey = curve.MoveKey(curveDragKey, SnapTimeFromMouse(), SnapValueFromMouse());
            edited = true;
        }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            curveDragKey = -1;

        // Double-click empty space adds a key on the curve at that time.
        if (hovered && hoverKey < 0 && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) {
            EditorUndo.Push($"Add key {id}");
            curve.AddKey(SnapTimeFromMouse(), SnapValueFromMouse());
            edited = true;
        }
        // Right-click a key removes it (keep at least one).
        if (hovered && hoverKey >= 0 && curve.Count > 1 && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) {
            EditorUndo.Push($"Remove key {id}");
            curve.RemoveKey(hoverKey);
            edited = true;
        }

        // Preset buttons + "open full editor" + key count.
        if (ImGui.SmallButton("Linear")) { EditorUndo.Push($"Preset {id}"); ReplaceCurve(curve, AnimationCurve.Linear()); edited = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("Ease")) { EditorUndo.Push($"Preset {id}"); ReplaceCurve(curve, AnimationCurve.EaseInOut()); edited = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("Const")) { EditorUndo.Push($"Preset {id}"); ReplaceCurve(curve, AnimationCurve.Constant()); edited = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton($"{EditorIcons.Maximize} Edit"))
            CurveEditorWindow.Open(curve, id, onExternalEdit ?? (() => { }));
        ImGui.SameLine();
        ImGui.TextDisabled($"{curve.Count} keys");

        ImGui.PopID();
        return edited;
    }

    // Replaces a curve's keys with another curve's (in place — preserves the member's instance).
    static void ReplaceCurve(AnimationCurve target, AnimationCurve source) {
        target.Clear();
        for (var i = 0; i < source.Count; i++)
            target.AddKey(source.Keys[i]);
        target.PreWrap = source.PreWrap;
        target.PostWrap = source.PostWrap;
    }

    // ---- Gradient editor ---------------------------------------------------------
    // An interactive gradient bar (Unity's gradient editor, trimmed): the bar samples Evaluate across
    // its width; COLOR stops sit as triangles BELOW the bar (drag horizontally to move, click to open a
    // color picker, double-click empty to add, right-click to remove), ALPHA stops as triangles ABOVE
    // (drag horizontally to move, vertical drag to change alpha). Reusable for ANY Gradient member;
    // mutated in place; returns true on edit. The checkerboard behind the bar shows alpha.
    static int gradColorDrag = -1, gradAlphaDrag = -1;
    static int gradColorPick = -1; // color stop whose picker popup is open

    public static bool GradientEditor(string id, ColorGradient g) {
        bool edited = false;
        ImGui.PushID(id);

        float w = MathF.Max(ImGui.GetContentRegionAvail().X, 60f);
        const float barH = 22f, stopH = 7f;
        SysVec2 cursor = ImGui.GetCursorScreenPos();
        SysVec2 barOrigin = cursor + new SysVec2(0f, stopH + 2f); // leave room for alpha stops above
        var barSize = new SysVec2(w, barH);
        var draw = ImGui.GetWindowDrawList();

        // Checkerboard so alpha is visible.
        const float check = 6f;
        for (float x = 0; x < w; x += check)
            for (float y = 0; y < barH; y += check) {
                bool dark = (((int)(x / check) + (int)(y / check)) & 1) == 0;
                uint cc = dark ? 0xFF606060 : 0xFF909090;
                SysVec2 a = barOrigin + new SysVec2(x, y);
                SysVec2 b = a + new SysVec2(MathF.Min(check, w - x), MathF.Min(check, barH - y));
                draw.AddRectFilled(a, b, cc);
            }

        // Sample the gradient across the bar width into thin vertical slices.
        const int slices = 96;
        for (var s = 0; s < slices; s++) {
            float t0 = (float)s / slices, t1 = (float)(s + 1) / slices;
            Vector4 c0 = g.Evaluate(t0);
            uint col = ImGui.GetColorU32(new SysVec4(c0.X, c0.Y, c0.Z, c0.W));
            SysVec2 a = barOrigin + new SysVec2(t0 * w, 0f);
            SysVec2 b = barOrigin + new SysVec2(t1 * w, barH);
            draw.AddRectFilled(a, b, col);
        }
        draw.AddRect(barOrigin, barOrigin + barSize, 0xFF202224);

        // Interaction surface covering the bar + both stop rows.
        SysVec2 totalSize = new SysVec2(w, barH + stopH * 2f + 4f);
        ImGui.SetCursorScreenPos(cursor);
        ImGui.InvisibleButton("##gradbar", totalSize);
        bool hovered = ImGui.IsItemHovered();
        SysVec2 mouse = ImGui.GetMousePos();
        float mt = Math.Clamp((mouse.X - barOrigin.X) / w, 0f, 1f);

        float alphaRowY = cursor.Y;                       // alpha stops above the bar
        float colorRowY = barOrigin.Y + barH + 2f;        // color stops below the bar

        // ---- Color stops (below) ----
        int hoverColor = -1;
        for (var i = 0; i < g.ColorKeyCount; i++) {
            float kx = barOrigin.X + g.ColorKeys[i].Time * w;
            var tip = new SysVec2(kx, colorRowY);
            var bl = new SysVec2(kx - stopH * 0.6f, colorRowY + stopH);
            var br = new SysVec2(kx + stopH * 0.6f, colorRowY + stopH);
            Vector3 kc = g.ColorKeys[i].Color;
            uint fill = ImGui.GetColorU32(new SysVec4(kc.X, kc.Y, kc.Z, 1f));
            draw.AddTriangleFilled(tip, bl, br, fill);
            draw.AddTriangle(tip, bl, br, (i == gradColorDrag) ? 0xFF30D0FF : 0xFF202224);
            if (hovered && MathF.Abs(mouse.X - kx) < stopH && mouse.Y >= colorRowY - 2f && mouse.Y <= colorRowY + stopH + 2f)
                hoverColor = i;
        }

        // ---- Alpha stops (above) ----
        int hoverAlpha = -1;
        for (var i = 0; i < g.AlphaKeyCount; i++) {
            float kx = barOrigin.X + g.AlphaKeys[i].Time * w;
            var tip = new SysVec2(kx, alphaRowY + stopH);
            var tl = new SysVec2(kx - stopH * 0.6f, alphaRowY);
            var tr = new SysVec2(kx + stopH * 0.6f, alphaRowY);
            float av = g.AlphaKeys[i].Alpha;
            uint fill = ImGui.GetColorU32(new SysVec4(av, av, av, 1f));
            draw.AddTriangleFilled(tip, tl, tr, fill);
            draw.AddTriangle(tip, tl, tr, (i == gradAlphaDrag) ? 0xFF30D0FF : 0xFF202224);
            if (hovered && MathF.Abs(mouse.X - kx) < stopH && mouse.Y >= alphaRowY - 2f && mouse.Y <= alphaRowY + stopH + 2f)
                hoverAlpha = i;
        }

        // ---- Begin drags / picker / add / remove ----
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
            if (hoverColor >= 0) { gradColorDrag = hoverColor; EditorUndo.Push($"Edit {id}"); }
            else if (hoverAlpha >= 0) { gradAlphaDrag = hoverAlpha; EditorUndo.Push($"Edit {id}"); }
        }
        // Open a color picker popup on a color-stop click-release (only if not dragged far).
        if (hoverColor >= 0 && ImGui.IsMouseReleased(ImGuiMouseButton.Left) && gradColorDrag == hoverColor) {
            gradColorPick = hoverColor;
            ImGui.OpenPopup("##gradcolpick");
        }

        if (gradColorDrag >= 0 && gradColorDrag < g.ColorKeyCount && ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            gradColorDrag = g.MoveColorKey(gradColorDrag, mt, g.ColorKeys[gradColorDrag].Color);
            edited = true;
        }
        if (gradAlphaDrag >= 0 && gradAlphaDrag < g.AlphaKeyCount && ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            gradAlphaDrag = g.MoveAlphaKey(gradAlphaDrag, mt, g.AlphaKeys[gradAlphaDrag].Alpha);
            edited = true;
        }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) { gradColorDrag = -1; gradAlphaDrag = -1; }

        // Double-click empty space on the color row adds a color stop (sampled current color).
        if (hovered && hoverColor < 0 && mouse.Y >= colorRowY - 2f && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) {
            EditorUndo.Push($"Add color {id}");
            g.AddColorKey(mt, g.EvaluateColor(mt));
            edited = true;
        }
        // Double-click empty space on the alpha row adds an alpha stop.
        if (hovered && hoverAlpha < 0 && mouse.Y <= alphaRowY + stopH + 2f && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) {
            EditorUndo.Push($"Add alpha {id}");
            g.AddAlphaKey(mt, g.EvaluateAlpha(mt));
            edited = true;
        }
        // Right-click removes the hovered stop (keep at least one of each kind).
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) {
            if (hoverColor >= 0 && g.ColorKeyCount > 1) { EditorUndo.Push($"Remove color {id}"); g.RemoveColorKey(hoverColor); edited = true; }
            else if (hoverAlpha >= 0 && g.AlphaKeyCount > 1) { EditorUndo.Push($"Remove alpha {id}"); g.RemoveAlphaKey(hoverAlpha); edited = true; }
        }

        // Color picker popup for the selected color stop.
        if (ImGui.BeginPopup("##gradcolpick")) {
            if (gradColorPick >= 0 && gradColorPick < g.ColorKeyCount) {
                Vector3 c = g.ColorKeys[gradColorPick].Color;
                var sv = new SysVec3(c.X, c.Y, c.Z);
                if (ImGui.ColorPicker3("##pick", ref sv)) {
                    g.MoveColorKey(gradColorPick, g.ColorKeys[gradColorPick].Time, new Vector3(sv.X, sv.Y, sv.Z));
                    edited = true;
                }
            }
            ImGui.EndPopup();
        }

        // Preset buttons + counts.
        if (ImGui.SmallButton("Fire")) { EditorUndo.Push($"Preset {id}"); ReplaceGradient(g, ColorGradient.Fire()); edited = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("Fade")) { EditorUndo.Push($"Preset {id}"); ReplaceGradient(g, ColorGradient.FadeOut(new Vector3(1f, 1f, 1f))); edited = true; }
        ImGui.SameLine();
        ImGui.TextDisabled($"{g.ColorKeyCount}c / {g.AlphaKeyCount}a");

        ImGui.PopID();
        return edited;
    }

    static void ReplaceGradient(ColorGradient target, ColorGradient source) {
        target.Clear();
        for (var i = 0; i < source.ColorKeyCount; i++)
            target.AddColorKey(source.ColorKeys[i].Time, source.ColorKeys[i].Color);
        for (var i = 0; i < source.AlphaKeyCount; i++)
            target.AddAlphaKey(source.AlphaKeys[i].Time, source.AlphaKeys[i].Alpha);
    }

    // ---- Audio scrubber ----------------------------------------------------------
    // Time slider under the preview button: shows the play head while previewing and lets you scrub.
    // Dragging seeks the live voice; releasing on a stopped voice restarts playback from that offset
    // (so you can scrub a finished/idle clip to a spot and hear it from there). The live `voice` and the
    // persisted `scrubTime` are passed by ref so the CALLER owns that state (it's shared with the audio
    // asset preview in InspectorPanel); `markDirty` keeps the inspector repainting under on-demand render.
    public static void AudioScrubber(AudioClip clip, float volume, float pitch,
        ref IAudioVoice voice, ref float scrubTime, Action markDirty) {
        float duration = MathF.Max(clip.DurationSeconds, 0.001f);
        bool live = voice is { IsPlaying: true };

        // While playing, the play head drives the slider; otherwise keep the last scrub position so the
        // handle doesn't snap back to 0 between previews.
        if (live)
            scrubTime = Math.Clamp(voice.TimeSeconds, 0f, duration);

        ImGui.SetNextItemWidth(-1);
        float t = scrubTime;
        if (ImGui.SliderFloat("##audioScrub", ref t, 0f, duration, "%.2fs")) {
            scrubTime = Math.Clamp(t, 0f, duration);
            if (voice is { IsPlaying: true })
                voice.TimeSeconds = scrubTime;   // seek the live voice
            else {
                // Scrubbing an idle clip: start a fresh voice and jump it to the scrub point.
                voice = Audio.Play(clip, volume, pitch, loop: false);
                if (voice is not null)
                    voice.TimeSeconds = scrubTime;
            }
        }

        // Keep the inspector repainting so the play head animates under on-demand rendering.
        if (live)
            markDirty();
    }

    // ---- Animator pose scrubber --------------------------------------------------
    // Animator preview: a play/pause toggle + a scrub slider that evaluates the clip in edit mode, so
    // you can pose the skinned character without entering play. Drives Animator.EvaluatePreview, which
    // runs the same sample->skeleton->skinning pipeline as play-mode Tick. The preview time + play toggle
    // are passed by ref so the caller owns that persistent state; `markDirty` repaints the viewport.
    public static void AnimatorScrubber(Animator animator, ref float previewTime, ref bool playing, Action markDirty) {
        ImGui.Spacing();
        ImGui.SeparatorText("Preview");

        if (animator.Clip is null) {
            ImGui.TextDisabled("Assign a Clip to preview.");
            return;
        }

        float duration = MathF.Max(animator.Clip.DurationSeconds, 0.001f);

        if (ImGui.Button(playing ? $"{EditorIcons.Pause}  Pause" : $"{EditorIcons.Play}  Play",
                new SysVec2(100, 0)))
            playing = !playing;
        ImGui.SameLine();
        if (ImGui.Button($"{EditorIcons.Refresh}  Reset", new SysVec2(100, 0))) {
            previewTime = 0f;
            playing = false;
        }

        if (playing) {
            previewTime += (float)Time.DeltaTime;
            if (animator.Loop && previewTime > duration)
                previewTime %= duration;
            markDirty(); // keep the viewport repainting while previewing
        }

        float t = previewTime;
        if (ImGui.SliderFloat("##animScrub", ref t, 0f, duration, "%.2fs")) {
            previewTime = t;
            playing = false;
        }

        // Apply the previewed pose this frame (edit mode only — play mode drives it from Tick).
        if (!SceneManager.IsPlaying) {
            animator.EvaluatePreview(previewTime);
            markDirty();
        }

        // Animation events (script-driven). Show the count + the last fired event so you can confirm
        // they're wired and firing in play mode.
        if (animator.EventCount > 0) {
            ImGui.Spacing();
            ImGui.SeparatorText("Events");
            ImGui.TextDisabled($"{animator.EventCount} event(s) registered");
            if (!string.IsNullOrEmpty(animator.LastFiredEvent))
                ImGui.TextDisabled($"Last fired: {animator.LastFiredEvent}");
        }
    }
}
