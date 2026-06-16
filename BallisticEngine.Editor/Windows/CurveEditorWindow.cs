using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// A full standalone AnimationCurve editor window (Unity's Curve Editor, the "real" one — the inline
// inspector widget is just a preview + an "Open" button into this). One curve is edited at a time:
// the inspector calls CurveEditorWindow.Open(curve, title, onChanged) and we mutate THAT instance in
// place, invoking onChanged after every edit so the owning component re-serializes / re-renders.
//
// Over the inline widget this adds: a large gridded canvas with axis labels, mouse-wheel zoom and
// middle-drag pan, F to frame-all, draggable in/out TANGENT handles (with Free/Flat/Linear/Constant
// modes), a numeric keyframe inspector (time / value / tangents), pre/post wrap-mode dropdowns, and a
// live readout. Undo is whole-curve snapshots pushed before each interaction (EditorUndo.PushCallback),
// so Ctrl+Z restores the curve regardless of which component owns it.
internal static class CurveEditorWindow {
    static bool open;
    static AnimationCurve target;
    static string title = "Curve";
    static Action onChanged;

    // View transform: value/time window currently shown. Auto-framed on open, then user-controlled.
    static float viewT0, viewT1 = 1f, viewV0, viewV1 = 1f;
    static bool framed;

    static int selectedKey = -1;
    static int dragKey = -1;
    static int dragTangent;     // 0 none, -1 in handle, +1 out handle
    static bool snapshotPushed;

    public static bool IsOpen => open;

    // Opens (or retargets) the window onto a curve. onChanged fires after every mutation so the caller
    // can mark its scene/asset dirty. Reframes the view to fit the curve.
    public static void Open(AnimationCurve curve, string label, Action changed) {
        target = curve;
        title = label ?? "Curve";
        onChanged = changed;
        open = true;
        framed = false;
        selectedKey = curve.Count > 0 ? 0 : -1;
    }

    // If the inspector destroys/replaces the curve instance it was editing, drop the reference so we
    // don't mutate a detached object.
    public static void CloseIfEditing(AnimationCurve curve) {
        if (open && ReferenceEquals(target, curve)) open = false;
    }

    public static void Draw(float scale) {
        if (!open) return;
        if (target is null) { open = false; return; }

        ImGui.SetNextWindowSize(new SysVec2(620 * scale, 460 * scale), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin($"{EditorIcons.Grid}  Curve  -  {title}###CurveEditorWindow", ref open,
                ImGuiWindowFlags.NoCollapse)) {
            ImGui.End();
            return;
        }

        if (!framed) { FrameAll(); framed = true; }

        DrawToolbar(scale);
        ImGui.Separator();

        // Split: canvas on the left, the keyframe inspector on the right.
        float inspectorW = 190 * scale;
        float canvasW = ImGui.GetContentRegionAvail().X - inspectorW - ImGui.GetStyle().ItemSpacing.X;
        if (canvasW < 120) canvasW = ImGui.GetContentRegionAvail().X; // too narrow: drop the side panel

        DrawCanvas(new SysVec2(canvasW, ImGui.GetContentRegionAvail().Y), scale);

        if (canvasW < ImGui.GetContentRegionAvail().X + 1) {
            ImGui.SameLine();
            ImGui.BeginChild("##keyinspector", new SysVec2(inspectorW, 0), ImGuiChildFlags.Borders);
            DrawKeyInspector(scale);
            ImGui.EndChild();
        }

        ImGui.End();
    }

    // ---- Toolbar -------------------------------------------------------------

    static void DrawToolbar(float scale) {
        if (ImGui.SmallButton("Linear")) Preset(AnimationCurve.Linear());
        ImGui.SameLine();
        if (ImGui.SmallButton("Ease")) Preset(AnimationCurve.EaseInOut());
        ImGui.SameLine();
        if (ImGui.SmallButton("Const")) Preset(AnimationCurve.Constant());
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        if (ImGui.SmallButton($"{EditorIcons.Maximize} Frame all")) FrameAll();
        ImGui.SameLine();

        // Wrap-mode dropdowns.
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * scale);
        WrapCombo("Pre", () => target.PreWrap, m => target.PreWrap = m);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * scale);
        WrapCombo("Post", () => target.PostWrap, m => target.PostWrap = m);

        ImGui.SameLine();
        ImGui.TextDisabled($"{target.Count} keys");
    }

    static void WrapCombo(string id, Func<AnimationCurve.WrapMode> get, Action<AnimationCurve.WrapMode> set) {
        AnimationCurve.WrapMode cur = get();
        if (ImGui.BeginCombo($"##wrap{id}", $"{id}: {cur}")) {
            foreach (AnimationCurve.WrapMode m in Enum.GetValues<AnimationCurve.WrapMode>()) {
                if (ImGui.Selectable(m.ToString(), m == cur) && m != cur) {
                    PushUndo();
                    set(m);
                    Changed();
                }
            }
            ImGui.EndCombo();
        }
    }

    // ---- Canvas --------------------------------------------------------------

    static void DrawCanvas(SysVec2 size, float scale) {
        if (size.X < 20 || size.Y < 20) return;
        SysVec2 origin = ImGui.GetCursorScreenPos();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();

        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new SysVec4(0.09f, 0.10f, 0.12f, 1f)), 4f);

        SysVec2 ToScreen(float t, float v) => new(
            origin.X + (t - viewT0) / (viewT1 - viewT0) * size.X,
            origin.Y + (1f - (v - viewV0) / (viewV1 - viewV0)) * size.Y);
        float TimeAt(float sx) => viewT0 + (sx - origin.X) / size.X * (viewT1 - viewT0);
        float ValueAt(float sy) => viewV1 - (sy - origin.Y) / size.Y * (viewV1 - viewV0);

        DrawGrid(draw, origin, size, scale);

        // Curve polyline (sampled across the visible time window — extrapolation shows wrap modes).
        const int Samples = 160;
        uint ccol = ImGui.GetColorU32(new SysVec4(0.45f, 0.85f, 1f, 1f));
        SysVec2 prev = default;
        for (int s = 0; s <= Samples; s++) {
            float t = viewT0 + (viewT1 - viewT0) * s / Samples;
            SysVec2 p = ToScreen(t, target.Evaluate(t));
            if (s > 0) draw.AddLine(prev, p, ccol, 2f);
            prev = p;
        }

        ImGui.InvisibleButton("##curvecanvas", size);
        bool hovered = ImGui.IsItemHovered();
        SysVec2 mouse = ImGui.GetMousePos();

        HandleZoomPan(hovered, mouse, origin, size);

        // Tangent handles for the selected key are tested FIRST so a tangent grab wins over a nearby
        // keyframe dot (the out-handle bug). The flag resets each frame.
        tangentGrabbedThisClick = false;
        if (selectedKey >= 0 && selectedKey < target.Count)
            DrawTangentHandles(draw, ToScreen, mouse, hovered);

        // Keyframe dots + hit test.
        const float dotR = 5.5f;
        int hoverKey = -1;
        for (int i = 0; i < target.Count; i++) {
            AnimationCurve.Keyframe k = target.Keys[i];
            SysVec2 sp = ToScreen(k.Time, k.Value);
            bool near = (mouse - sp).LengthSquared() <= (dotR + 4f) * (dotR + 4f);
            if (near && hovered) hoverKey = i;
            uint dc = i == selectedKey ? ImGui.GetColorU32(new SysVec4(1f, 0.85f, 0.3f, 1f))
                    : near ? ImGui.GetColorU32(new SysVec4(1f, 0.95f, 0.7f, 1f))
                    : ImGui.GetColorU32(new SysVec4(1f, 1f, 1f, 1f));
            draw.AddCircleFilled(sp, dotR, dc);
            draw.AddCircle(sp, dotR, ImGui.GetColorU32(new SysVec4(0, 0, 0, 0.7f)), 0, 1.5f);
        }

        // Begin dragging a key — but NOT if a tangent handle already grabbed this click.
        if (hovered && hoverKey >= 0 && dragTangent == 0 && !tangentGrabbedThisClick &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
            selectedKey = hoverKey;
            dragKey = hoverKey;
            PushUndo();
        }
        if (dragKey >= 0 && dragKey < target.Count && ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            float nt = TimeAt(mouse.X), nv = ValueAt(mouse.Y);
            selectedKey = dragKey = target.MoveKey(dragKey, nt, nv);
            Changed();
        }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) { dragKey = -1; dragTangent = 0; snapshotPushed = false; }

        // Double-click empty space adds a key.
        if (hovered && hoverKey < 0 && dragTangent == 0 && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) {
            PushUndo();
            selectedKey = target.AddKey(TimeAt(mouse.X), ValueAt(mouse.Y));
            Changed();
        }

        // Right-click → context menu (Unity's curve right-click): on a key, key ops + tangent modes;
        // on empty space, "Add Key Here". Remember the click position so Add Key lands where clicked.
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) {
            ctxKey = hoverKey;
            ctxTime = TimeAt(mouse.X);
            ctxValue = ValueAt(mouse.Y);
            ImGui.OpenPopup("##curvectx");
        }
        DrawContextMenu();

        // F frames all (Unity).
        if (hovered && ImGui.IsKeyPressed(ImGuiKey.F)) FrameAll();
    }

    static int ctxKey = -1;
    static float ctxTime, ctxValue;

    static void DrawContextMenu() {
        if (!ImGui.BeginPopup("##curvectx"))
            return;

        if (ctxKey >= 0 && ctxKey < target.Count) {
            ImGui.TextDisabled($"Key #{ctxKey}");
            ImGui.Separator();
            // Tangent modes for this key (Unity's Flat / Linear / Constant + Free).
            if (ImGui.MenuItem("Flat")) { selectedKey = ctxKey; SetTangentMode(0f, 0f); }
            if (ImGui.MenuItem("Linear")) { selectedKey = ctxKey; SetTangentModeLinear(); }
            if (ImGui.MenuItem("Constant (Step)")) { selectedKey = ctxKey; SetTangentMode(float.PositiveInfinity, float.PositiveInfinity); }
            ImGui.Separator();
            ImGui.BeginDisabled(target.Count <= 1);
            if (ImGui.MenuItem("Delete Key")) {
                PushUndo();
                target.RemoveKey(ctxKey);
                selectedKey = Math.Clamp(selectedKey, 0, target.Count - 1);
                snapshotPushed = false;
                Changed();
            }
            ImGui.EndDisabled();
        }
        else {
            if (ImGui.MenuItem("Add Key Here")) {
                PushUndo();
                selectedKey = target.AddKey(ctxTime, ctxValue);
                snapshotPushed = false;
                Changed();
            }
        }
        ImGui.EndPopup();
    }

    static void DrawGrid(ImDrawListPtr draw, SysVec2 origin, SysVec2 size, float scale) {
        uint line = ImGui.GetColorU32(new SysVec4(1f, 1f, 1f, 0.06f));
        uint axis = ImGui.GetColorU32(new SysVec4(1f, 1f, 1f, 0.22f));
        uint txt = ImGui.GetColorU32(new SysVec4(0.7f, 0.72f, 0.78f, 1f));

        // Pick a "nice" step for each axis so labels stay readable at any zoom.
        float tStep = NiceStep(viewT1 - viewT0, size.X / (70f * scale));
        float vStep = NiceStep(viewV1 - viewV0, size.Y / (40f * scale));

        for (float t = MathF.Ceiling(viewT0 / tStep) * tStep; t <= viewT1; t += tStep) {
            float x = origin.X + (t - viewT0) / (viewT1 - viewT0) * size.X;
            draw.AddLine(new SysVec2(x, origin.Y), new SysVec2(x, origin.Y + size.Y), line);
            draw.AddText(new SysVec2(x + 2, origin.Y + size.Y - 14 * scale), txt, t.ToString("0.##"));
        }
        for (float v = MathF.Ceiling(viewV0 / vStep) * vStep; v <= viewV1; v += vStep) {
            float y = origin.Y + (1f - (v - viewV0) / (viewV1 - viewV0)) * size.Y;
            bool isZero = MathF.Abs(v) < vStep * 0.001f;
            draw.AddLine(new SysVec2(origin.X, y), new SysVec2(origin.X + size.X, y), isZero ? axis : line);
            draw.AddText(new SysVec2(origin.X + 2, y + 1), txt, v.ToString("0.##"));
        }
    }

    // A 1/2/5 * 10^n step that yields roughly `targetDivisions` gridlines across `range`.
    static float NiceStep(float range, float targetDivisions) {
        if (range <= 0f || targetDivisions <= 0f) return 1f;
        float raw = range / targetDivisions;
        float mag = MathF.Pow(10f, MathF.Floor(MathF.Log10(raw)));
        float norm = raw / mag;
        float nice = norm < 1.5f ? 1f : norm < 3.5f ? 2f : norm < 7.5f ? 5f : 10f;
        return nice * mag;
    }

    static void DrawTangentHandles(ImDrawListPtr draw, Func<float, float, SysVec2> toScreen, SysVec2 mouse, bool hovered) {
        AnimationCurve.Keyframe k = target.Keys[selectedKey];
        SysVec2 kp = toScreen(k.Time, k.Value);
        const float handleLen = 40f;
        uint hcol = ImGui.GetColorU32(new SysVec4(0.9f, 0.6f, 0.3f, 1f));

        // Handle endpoints: step along the tangent slope in screen space (a fixed pixel length).
        SysVec2 InDir() => SlopeDir(k.InTangent, toScreen) * -1f;
        SysVec2 OutDir() => SlopeDir(k.OutTangent, toScreen);

        SysVec2 inP = kp + InDir() * handleLen;
        SysVec2 outP = kp + OutDir() * handleLen;
        draw.AddLine(kp, inP, hcol, 1.5f);
        draw.AddLine(kp, outP, hcol, 1.5f);

        DrawTangentDot(draw, inP, mouse, hovered, -1);
        DrawTangentDot(draw, outP, mouse, hovered, +1);

        // Drag a tangent handle: convert the cursor offset from the key into a slope. BOTH handles use
        // the same forward delta math; the in-handle drags backward, so negate its delta. (The earlier
        // out-handle bug was a hit-test one — the keyframe dot stole the click; fixed below by testing
        // tangent dots BEFORE keyframes and using a wider grab radius.)
        if (dragTangent != 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            SysVec2 d = mouse - kp;
            if (dragTangent < 0) d = -d; // in-handle points backward
            float slope = SlopeFromScreenDelta(d, toScreen);
            float inT = k.InTangent, outT = k.OutTangent;
            if (dragTangent < 0) inT = slope; else outT = slope;
            target.SetTangents(selectedKey, inT, outT);
            Changed();
        }
    }

    static void DrawTangentDot(ImDrawListPtr draw, SysVec2 p, SysVec2 mouse, bool hovered, int which) {
        const float r = 5f, grab = 9f;   // wider grab than the visual radius so it's easy to catch
        bool near = (mouse - p).LengthSquared() <= grab * grab;
        draw.AddCircleFilled(p, r, ImGui.GetColorU32(near ? new SysVec4(1f, 0.85f, 0.4f, 1f) : new SysVec4(0.9f, 0.6f, 0.3f, 1f)));
        if (hovered && near && dragTangent == 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
            dragTangent = which;
            tangentGrabbedThisClick = true;   // suppress the keyframe-drag that runs later this frame
            PushUndo();
        }
    }

    // Set true when a tangent dot grabbed this frame's click, so the keyframe hit-test below skips it
    // (the out-handle sits near its key — without this the key dot stole the drag).
    static bool tangentGrabbedThisClick;

    // Screen-space unit direction of a value/time slope (accounts for the view's non-uniform scale).
    static SysVec2 SlopeDir(float slope, Func<float, float, SysVec2> toScreen) {
        if (float.IsInfinity(slope)) return new SysVec2(0f, slope > 0 ? -1f : 1f); // constant ≈ vertical
        SysVec2 a = toScreen(0f, 0f);
        SysVec2 b = toScreen(1f, slope);
        SysVec2 d = b - a;
        float len = d.Length();
        return len > 1e-4f ? d / len : new SysVec2(1f, 0f);
    }

    // Inverse: a screen-space delta from the key → a value/time slope.
    static float SlopeFromScreenDelta(SysVec2 screenDelta, Func<float, float, SysVec2> toScreen) {
        SysVec2 a = toScreen(0f, 0f);
        SysVec2 unitT = toScreen(1f, 0f) - a;   // screen vector for +1 time
        SysVec2 unitV = toScreen(0f, 1f) - a;   // screen vector for +1 value
        float dt = unitT.X != 0 ? screenDelta.X / unitT.X : 0f;
        float dv = unitV.Y != 0 ? screenDelta.Y / unitV.Y : 0f;
        return MathF.Abs(dt) < 1e-4f ? (dv >= 0 ? float.PositiveInfinity : float.NegativeInfinity) : dv / dt;
    }

    static void HandleZoomPan(bool hovered, SysVec2 mouse, SysVec2 origin, SysVec2 size) {
        if (!hovered) return;
        float wheel = ImGui.GetIO().MouseWheel;
        if (wheel != 0f) {
            // Zoom toward the cursor.
            float zoom = MathF.Pow(0.9f, wheel);
            float ft = (mouse.X - origin.X) / size.X, fv = 1f - (mouse.Y - origin.Y) / size.Y;
            float ct = viewT0 + ft * (viewT1 - viewT0), cv = viewV0 + fv * (viewV1 - viewV0);
            viewT0 = ct + (viewT0 - ct) * zoom; viewT1 = ct + (viewT1 - ct) * zoom;
            viewV0 = cv + (viewV0 - cv) * zoom; viewV1 = cv + (viewV1 - cv) * zoom;
        }
        // Middle-drag pan.
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle)) {
            SysVec2 d = ImGui.GetIO().MouseDelta;
            float dt = d.X / size.X * (viewT1 - viewT0);
            float dv = d.Y / size.Y * (viewV1 - viewV0);
            viewT0 -= dt; viewT1 -= dt;
            viewV0 += dv; viewV1 += dv;
        }
    }

    // ---- Keyframe inspector (right panel) ------------------------------------

    static void DrawKeyInspector(float scale) {
        ImGui.TextDisabled("Keyframe");
        ImGui.Separator();
        if (selectedKey < 0 || selectedKey >= target.Count) {
            ImGui.TextWrapped("Click a key to edit it, or double-click the canvas to add one.");
            return;
        }

        AnimationCurve.Keyframe k = target.Keys[selectedKey];
        ImGui.TextDisabled($"#{selectedKey}");

        float time = k.Time, value = k.Value;
        ImGui.SetNextItemWidth(-1);
        if (DragWithUndo("Time##k", ref time, 0.01f)) { selectedKey = target.MoveKey(selectedKey, time, value); Changed(); }
        k = target.Keys[selectedKey]; value = k.Value;
        ImGui.SetNextItemWidth(-1);
        if (DragWithUndo("Value##k", ref value, 0.01f)) { selectedKey = target.MoveKey(selectedKey, target.Keys[selectedKey].Time, value); Changed(); }

        ImGui.Dummy(new SysVec2(0, 4));
        ImGui.TextDisabled("Tangents");
        k = target.Keys[selectedKey];
        float inT = k.InTangent, outT = k.OutTangent;
        bool stepped = float.IsInfinity(inT) || float.IsInfinity(outT);
        if (!stepped) {
            ImGui.SetNextItemWidth(-1);
            if (DragWithUndo("In##t", ref inT, 0.05f)) { target.SetTangents(selectedKey, inT, target.Keys[selectedKey].OutTangent); Changed(); }
            ImGui.SetNextItemWidth(-1);
            if (DragWithUndo("Out##t", ref outT, 0.05f)) { target.SetTangents(selectedKey, target.Keys[selectedKey].InTangent, outT); Changed(); }
        } else {
            ImGui.TextDisabled("(stepped)");
        }

        ImGui.Dummy(new SysVec2(0, 4));
        ImGui.TextDisabled("Tangent mode");
        if (ImGui.SmallButton("Flat")) SetTangentMode(0f, 0f);
        ImGui.SameLine();
        if (ImGui.SmallButton("Linear")) SetTangentModeLinear();
        ImGui.SameLine();
        if (ImGui.SmallButton("Step")) SetTangentMode(float.PositiveInfinity, float.PositiveInfinity);

        ImGui.Dummy(new SysVec2(0, 8));
        if (target.Count > 1 && ImGui.Button($"{EditorIcons.Delete} Delete key", new SysVec2(-1, 0))) {
            PushUndo();
            target.RemoveKey(selectedKey);
            selectedKey = Math.Clamp(selectedKey, 0, target.Count - 1);
            Changed();
        }
    }

    // A float drag that snapshots once when the drag begins (so Ctrl+Z undoes the whole drag, not steps).
    static bool DragWithUndo(string label, ref float v, float speed) {
        bool changed = ImGui.DragFloat(label, ref v, speed);
        if (ImGui.IsItemActivated()) PushUndo();
        if (ImGui.IsItemDeactivatedAfterEdit()) snapshotPushed = false;
        return changed;
    }

    static void SetTangentMode(float inT, float outT) {
        if (selectedKey < 0) return;
        PushUndo();
        target.SetTangents(selectedKey, inT, outT);
        snapshotPushed = false;
        Changed();
    }

    // Linear: point both tangents at the neighbouring keys' slopes.
    static void SetTangentModeLinear() {
        if (selectedKey < 0) return;
        PushUndo();
        AnimationCurve.Keyframe k = target.Keys[selectedKey];
        float inT = 0f, outT = 0f;
        if (selectedKey > 0) {
            AnimationCurve.Keyframe p = target.Keys[selectedKey - 1];
            float dt = k.Time - p.Time;
            inT = dt != 0 ? (k.Value - p.Value) / dt : 0f;
        }
        if (selectedKey < target.Count - 1) {
            AnimationCurve.Keyframe n = target.Keys[selectedKey + 1];
            float dt = n.Time - k.Time;
            outT = dt != 0 ? (n.Value - k.Value) / dt : 0f;
        }
        target.SetTangents(selectedKey, inT, outT);
        snapshotPushed = false;
        Changed();
    }

    // ---- View / undo / dirty helpers -----------------------------------------

    static void FrameAll() {
        if (target.Count == 0) { viewT0 = 0; viewT1 = 1; viewV0 = 0; viewV1 = 1; return; }
        float t0 = target.Keys[0].Time, t1 = target.Keys[target.Count - 1].Time;
        float v0 = float.MaxValue, v1 = float.MinValue;
        for (int i = 0; i < target.Count; i++) {
            v0 = MathF.Min(v0, target.Keys[i].Value);
            v1 = MathF.Max(v1, target.Keys[i].Value);
        }
        if (t1 <= t0) { t0 -= 0.5f; t1 += 0.5f; }
        if (v1 <= v0) { v0 -= 0.5f; v1 += 0.5f; }
        float tp = (t1 - t0) * 0.08f, vp = (v1 - v0) * 0.15f;
        viewT0 = t0 - tp; viewT1 = t1 + tp;
        viewV0 = v0 - vp; viewV1 = v1 + vp;
    }

    static void Preset(AnimationCurve src) {
        PushUndo();
        target.Clear();
        for (int i = 0; i < src.Count; i++) target.AddKey(src.Keys[i]);
        target.PreWrap = src.PreWrap;
        target.PostWrap = src.PostWrap;
        selectedKey = target.Count > 0 ? 0 : -1;
        snapshotPushed = false;
        FrameAll();
        Changed();
    }

    // Holds the post-edit curve string for the currently-open gesture, so the undo entry's REDO can
    // re-apply it. Updated on every Changed() until the gesture commits (snapshotPushed flips false).
    static string[] pendingAfter;

    // Whole-curve snapshot undo: capture the compact string before/after via a callback entry, so the
    // edit is reversible no matter which component (or .volume) owns the curve. Pushed once per gesture.
    // Undo restores `before`; redo restores `after[0]`, which Changed() keeps current through the drag.
    static void PushUndo() {
        if (snapshotPushed) return;
        snapshotPushed = true;
        AnimationCurve curve = target;          // capture for the closures
        Action changed = onChanged;
        string before = curve.ToCompactString();
        string[] after = [before];              // filled in by Changed() as the gesture proceeds
        pendingAfter = after;
        EditorUndo.PushCallback($"Curve {title}",
            applyOld: () => { ApplyString(curve, before); changed?.Invoke(); },
            applyNew: () => { ApplyString(curve, after[0]); changed?.Invoke(); });
    }

    static void ApplyString(AnimationCurve curve, string compact) {
        AnimationCurve parsed = AnimationCurve.Parse(compact);
        curve.Clear();
        for (int i = 0; i < parsed.Count; i++) curve.AddKey(parsed.Keys[i]);
        curve.PreWrap = parsed.PreWrap;
        curve.PostWrap = parsed.PostWrap;
    }

    static void Changed() {
        // Keep the open gesture's redo target in sync with the live curve.
        if (snapshotPushed && pendingAfter is not null)
            pendingAfter[0] = target.ToCompactString();
        onChanged?.Invoke();
    }
}
