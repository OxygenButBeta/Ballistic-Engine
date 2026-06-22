using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal static partial class EditorWidgets {
    static int curveDragKey = -1;

    public static bool CurveEditor(string id, AnimationCurve curve, Action onExternalEdit = null) {
        bool edited = false;
        ImGui.PushID(id);

        float w = ImGui.GetContentRegionAvail().X;
        const float height = 90f;
        SysVec2 origin = ImGui.GetCursorScreenPos();
        var size = new SysVec2(MathF.Max(w, 60f), height);
        var draw = ImGui.GetWindowDrawList();

        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new SysVec4(0.10f, 0.11f, 0.13f, 1f)), 4f);
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(new SysVec4(0.30f, 0.32f, 0.36f, 1f)), 4f);

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

        if (vMin < 0f && vMax > 0f) {
            float zy = origin.Y + (1f - (0f - vMin) / (vMax - vMin)) * size.Y;
            draw.AddLine(new SysVec2(origin.X, zy), new SysVec2(origin.X + size.X, zy),
                ImGui.GetColorU32(new SysVec4(0.4f, 0.4f, 0.45f, 0.4f)));
        }

        const int Samples = 64;
        uint curveColor = ImGui.GetColorU32(new SysVec4(0.45f, 0.85f, 1f, 1f));
        SysVec2 prev = default;
        for (var s = 0; s <= Samples; s++) {
            float time = t0 + (t1 - t0) * s / Samples;
            SysVec2 p = ToScreen(time, curve.Evaluate(time));
            if (s > 0) draw.AddLine(prev, p, curveColor, 2f);
            prev = p;
        }

        ImGui.InvisibleButton("##curvebox", size);
        bool hovered = ImGui.IsItemHovered();
        SysVec2 mouse = ImGui.GetMousePos();

        float SnapTimeFromMouse() => t0 + (t1 - t0) * Math.Clamp((mouse.X - origin.X) / size.X, 0f, 1f);
        float SnapValueFromMouse() => vMax - (vMax - vMin) * Math.Clamp((mouse.Y - origin.Y) / size.Y, 0f, 1f);

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

        if (hovered && hoverKey >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
            curveDragKey = hoverKey;
            EditorUndo.Push($"Edit {id}");
        }

        if (curveDragKey >= 0 && curveDragKey < curve.Count && ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            curveDragKey = curve.MoveKey(curveDragKey, SnapTimeFromMouse(), SnapValueFromMouse());
            edited = true;
        }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            curveDragKey = -1;

        if (hovered && hoverKey < 0 && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) {
            EditorUndo.Push($"Add key {id}");
            curve.AddKey(SnapTimeFromMouse(), SnapValueFromMouse());
            edited = true;
        }

        if (hovered && hoverKey >= 0 && curve.Count > 1 && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) {
            EditorUndo.Push($"Remove key {id}");
            curve.RemoveKey(hoverKey);
            edited = true;
        }

        if (ImGui.SmallButton("Linear")) { EditorUndo.Push($"Preset {id}"); ReplaceCurve(curve, AnimationCurve.Linear()); edited = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("Ease")) { EditorUndo.Push($"Preset {id}"); ReplaceCurve(curve, AnimationCurve.EaseInOut()); edited = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("Const")) { EditorUndo.Push($"Preset {id}"); ReplaceCurve(curve, AnimationCurve.Constant()); edited = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton($"{EditorIcons.Maximize} Edit"))
            CurveEditorWindow.Edit(curve, id, onExternalEdit ?? (() => { }));
        ImGui.SameLine();
        ImGui.TextDisabled($"{curve.Count} keys");

        ImGui.PopID();
        return edited;
    }

    static void ReplaceCurve(AnimationCurve target, AnimationCurve source) {
        target.Clear();
        for (var i = 0; i < source.Count; i++)
            target.AddKey(source.Keys[i]);
        target.PreWrap = source.PreWrap;
        target.PostWrap = source.PostWrap;
    }

    static int gradColorDrag = -1, gradAlphaDrag = -1;
    static int gradColorPick = -1;

    public static bool GradientEditor(string id, ColorGradient g) {
        bool edited = false;
        ImGui.PushID(id);

        float w = MathF.Max(ImGui.GetContentRegionAvail().X, 60f);
        const float barH = 22f, stopH = 7f;
        SysVec2 cursor = ImGui.GetCursorScreenPos();
        SysVec2 barOrigin = cursor + new SysVec2(0f, stopH + 2f);
        var barSize = new SysVec2(w, barH);
        var draw = ImGui.GetWindowDrawList();

        const float check = 6f;
        for (float x = 0; x < w; x += check)
            for (float y = 0; y < barH; y += check) {
                bool dark = (((int)(x / check) + (int)(y / check)) & 1) == 0;
                uint cc = dark ? 0xFF606060 : 0xFF909090;
                SysVec2 a = barOrigin + new SysVec2(x, y);
                SysVec2 b = a + new SysVec2(MathF.Min(check, w - x), MathF.Min(check, barH - y));
                draw.AddRectFilled(a, b, cc);
            }

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

        SysVec2 totalSize = new SysVec2(w, barH + stopH * 2f + 4f);
        ImGui.SetCursorScreenPos(cursor);
        ImGui.InvisibleButton("##gradbar", totalSize);
        bool hovered = ImGui.IsItemHovered();
        SysVec2 mouse = ImGui.GetMousePos();
        float mt = Math.Clamp((mouse.X - barOrigin.X) / w, 0f, 1f);

        float alphaRowY = cursor.Y;
        float colorRowY = barOrigin.Y + barH + 2f;

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

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
            if (hoverColor >= 0) { gradColorDrag = hoverColor; EditorUndo.Push($"Edit {id}"); }
            else if (hoverAlpha >= 0) { gradAlphaDrag = hoverAlpha; EditorUndo.Push($"Edit {id}"); }
        }

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

        if (hovered && hoverColor < 0 && mouse.Y >= colorRowY - 2f && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) {
            EditorUndo.Push($"Add color {id}");
            g.AddColorKey(mt, g.EvaluateColor(mt));
            edited = true;
        }

        if (hovered && hoverAlpha < 0 && mouse.Y <= alphaRowY + stopH + 2f && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) {
            EditorUndo.Push($"Add alpha {id}");
            g.AddAlphaKey(mt, g.EvaluateAlpha(mt));
            edited = true;
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) {
            if (hoverColor >= 0 && g.ColorKeyCount > 1) { EditorUndo.Push($"Remove color {id}"); g.RemoveColorKey(hoverColor); edited = true; }
            else if (hoverAlpha >= 0 && g.AlphaKeyCount > 1) { EditorUndo.Push($"Remove alpha {id}"); g.RemoveAlphaKey(hoverAlpha); edited = true; }
        }

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

    public static void AudioScrubber(AudioClip clip, float volume, float pitch,
        ref IAudioVoice voice, ref float scrubTime, Action markDirty) {
        float duration = MathF.Max(clip.DurationSeconds, 0.001f);
        bool live = voice is { IsPlaying: true };

        if (live)
            scrubTime = Math.Clamp(voice.TimeSeconds, 0f, duration);

        ImGui.SetNextItemWidth(-1);
        float t = scrubTime;
        if (ImGui.SliderFloat("##audioScrub", ref t, 0f, duration, "%.2fs")) {
            scrubTime = Math.Clamp(t, 0f, duration);
            if (voice is { IsPlaying: true })
                voice.TimeSeconds = scrubTime;
            else {
                voice = Audio.Play(clip, volume, pitch, loop: false);
                if (voice is not null)
                    voice.TimeSeconds = scrubTime;
            }
        }

        if (live)
            markDirty();
    }

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
            markDirty();
        }

        float t = previewTime;
        if (ImGui.SliderFloat("##animScrub", ref t, 0f, duration, "%.2fs")) {
            previewTime = t;
            playing = false;
        }

        if (!SceneManager.IsPlaying) {
            animator.EvaluatePreview(previewTime);
            markDirty();
        }

        if (animator.EventCount > 0) {
            ImGui.Spacing();
            ImGui.SeparatorText("Events");
            ImGui.TextDisabled($"{animator.EventCount} event(s) registered");
            if (!string.IsNullOrEmpty(animator.LastFiredEvent))
                ImGui.TextDisabled($"Last fired: {animator.LastFiredEvent}");
        }
    }
}
