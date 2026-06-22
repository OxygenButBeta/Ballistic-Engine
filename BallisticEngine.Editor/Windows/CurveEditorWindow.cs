namespace BallisticEngine.Editor;

internal sealed class CurveEditorWindow : EditorWindow {
    public static readonly CurveEditorWindow Instance = new();

    public CurveEditorWindow() {
        DockKey = "win.curveeditor";
        Title = "Curve";
        Icon = EditorIcons.Grid;
        NoCollapse = true;
        DesiredSize = new Vector2(620, 460);
    }

    AnimationCurve target;
    string curveTitle = "Curve";
    Action onChanged;

    float viewT0, viewT1 = 1f, viewV0, viewV1 = 1f;
    bool framed;

    int selectedKey = -1;
    int dragKey = -1;
    int dragTangent;
    bool snapshotPushed;

    public static bool IsOpen => Instance.Open;

    public static void Edit(AnimationCurve curve, string label, Action changed) {
        Instance.OpenInstance(curve, label, changed);
    }

    public static void CloseIfEditing(AnimationCurve curve) {
        if (Instance.Open && ReferenceEquals(Instance.target, curve)) Instance.Open = false;
    }

    void OpenInstance(AnimationCurve curve, string label, Action changed) {
        target = curve;
        curveTitle = label ?? "Curve";
        Title = $"Curve  -  {curveTitle}";
        onChanged = changed;
        Open = true;
        framed = false;
        selectedKey = curve.Count > 0 ? 0 : -1;
    }

    protected override void OnGui(IEditorGui gui) {
        if (target is null) { Open = false; return; }

        if (!framed) { FrameAll(); framed = true; }

        float scale = gui.Scale;
        DrawToolbar(gui);
        gui.Separator();

        float inspectorW = 190 * scale;
        float canvasW = gui.ContentRegionAvail.X - inspectorW - gui.ItemSpacing.X;
        if (canvasW < 120) canvasW = gui.ContentRegionAvail.X;

        DrawCanvas(gui, new Vector2(canvasW, gui.ContentRegionAvail.Y));

        if (canvasW < gui.ContentRegionAvail.X + 1) {
            gui.SameLine();
            gui.BeginChild("##keyinspector", new Vector2(inspectorW, 0), border: true);
            DrawKeyInspector(gui);
            gui.EndChild();
        }
    }

    void DrawToolbar(IEditorGui gui) {
        float scale = gui.Scale;
        if (gui.SmallButton("Linear")) Preset(AnimationCurve.Linear());
        gui.SameLine();
        if (gui.SmallButton("Ease")) Preset(AnimationCurve.EaseInOut());
        gui.SameLine();
        if (gui.SmallButton("Const")) Preset(AnimationCurve.Constant());
        gui.SameLine();
        gui.TextDisabled("|");
        gui.SameLine();
        if (gui.SmallButton($"{EditorIcons.Maximize} Frame all")) FrameAll();
        gui.SameLine();

        gui.SameLine();
        gui.TextDisabled("|");
        gui.SameLine();
        gui.SetNextItemWidth(90 * scale);
        WrapCombo(gui, "Pre", () => target.PreWrap, m => target.PreWrap = m);
        gui.SameLine();
        gui.SetNextItemWidth(90 * scale);
        WrapCombo(gui, "Post", () => target.PostWrap, m => target.PostWrap = m);

        gui.SameLine();
        gui.TextDisabled($"{target.Count} keys");
    }

    void WrapCombo(IEditorGui gui, string id, Func<AnimationCurve.WrapMode> get, Action<AnimationCurve.WrapMode> set) {
        AnimationCurve.WrapMode cur = get();
        if (gui.BeginCombo($"##wrap{id}", $"{id}: {cur}")) {
            foreach (AnimationCurve.WrapMode m in Enum.GetValues<AnimationCurve.WrapMode>()) {
                if (gui.Selectable(m.ToString(), m == cur) && m != cur) {
                    PushUndo();
                    set(m);
                    Changed();
                }
            }
            gui.EndCombo();
        }
    }

    void DrawCanvas(IEditorGui gui, Vector2 size) {
        if (size.X < 20 || size.Y < 20) return;
        float scale = gui.Scale;
        Vector2 origin = gui.CursorScreenPos;
        IEditorDrawList draw = gui.WindowDrawList;

        draw.AddRectFilled(origin, origin + size, gui.ColorU32(new Vector4(0.09f, 0.10f, 0.12f, 1f)), 4f);

        Vector2 ToScreen(float t, float v) => new(
            origin.X + (t - viewT0) / (viewT1 - viewT0) * size.X,
            origin.Y + (1f - (v - viewV0) / (viewV1 - viewV0)) * size.Y);
        float TimeAt(float sx) => viewT0 + (sx - origin.X) / size.X * (viewT1 - viewT0);
        float ValueAt(float sy) => viewV1 - (sy - origin.Y) / size.Y * (viewV1 - viewV0);

        DrawGrid(gui, draw, origin, size);

        const int Samples = 160;
        uint ccol = gui.ColorU32(new Vector4(0.45f, 0.85f, 1f, 1f));
        Vector2 prev = default;
        for (int s = 0; s <= Samples; s++) {
            float t = viewT0 + (viewT1 - viewT0) * s / Samples;
            Vector2 p = ToScreen(t, target.Evaluate(t));
            if (s > 0) draw.AddLine(prev, p, ccol, 2f);
            prev = p;
        }

        gui.Input.InvisibleButton("##curvecanvas", size);
        bool hovered = gui.IsItemHovered();
        Vector2 mouse = gui.Input.MousePos;

        HandleZoomPan(gui, hovered, mouse, origin, size);

        tangentGrabbedThisClick = false;
        if (selectedKey >= 0 && selectedKey < target.Count)
            DrawTangentHandles(gui, draw, ToScreen, mouse, hovered);

        const float dotR = 5.5f;
        int hoverKey = -1;
        for (int i = 0; i < target.Count; i++) {
            AnimationCurve.Keyframe k = target.Keys[i];
            Vector2 sp = ToScreen(k.Time, k.Value);
            bool near = (mouse - sp).LengthSquared() <= (dotR + 4f) * (dotR + 4f);
            if (near && hovered) hoverKey = i;
            uint dc = i == selectedKey ? gui.ColorU32(new Vector4(1f, 0.85f, 0.3f, 1f))
                    : near ? gui.ColorU32(new Vector4(1f, 0.95f, 0.7f, 1f))
                    : gui.ColorU32(new Vector4(1f, 1f, 1f, 1f));
            draw.AddCircleFilled(sp, dotR, dc);
            draw.AddCircle(sp, dotR, gui.ColorU32(new Vector4(0, 0, 0, 0.7f)), 0, 1.5f);
        }

        if (hovered && hoverKey >= 0 && dragTangent == 0 && !tangentGrabbedThisClick &&
            gui.Input.MouseClicked(0)) {
            selectedKey = hoverKey;
            dragKey = hoverKey;
            PushUndo();
        }
        if (dragKey >= 0 && dragKey < target.Count && gui.Input.MouseDown(0)) {
            float nt = TimeAt(mouse.X), nv = ValueAt(mouse.Y);
            selectedKey = dragKey = target.MoveKey(dragKey, nt, nv);
            Changed();
        }
        if (gui.Input.MouseReleased(0)) { dragKey = -1; dragTangent = 0; snapshotPushed = false; }

        if (hovered && hoverKey < 0 && dragTangent == 0 && gui.Input.MouseDoubleClicked(0)) {
            PushUndo();
            selectedKey = target.AddKey(TimeAt(mouse.X), ValueAt(mouse.Y));
            Changed();
        }

        if (hovered && gui.Input.MouseClicked(1)) {
            ctxKey = hoverKey;
            ctxTime = TimeAt(mouse.X);
            ctxValue = ValueAt(mouse.Y);
            gui.OpenPopup("##curvectx");
        }
        DrawContextMenu(gui);

        if (hovered && gui.Input.KeyPressed(EditorGuiKey.F)) FrameAll();
    }

    int ctxKey = -1;
    float ctxTime, ctxValue;

    void DrawContextMenu(IEditorGui gui) {
        if (!gui.BeginPopup("##curvectx"))
            return;

        if (ctxKey >= 0 && ctxKey < target.Count) {
            gui.TextDisabled($"Key #{ctxKey}");
            gui.Separator();
            if (gui.MenuItem("Flat")) { selectedKey = ctxKey; SetTangentMode(0f, 0f); }
            if (gui.MenuItem("Linear")) { selectedKey = ctxKey; SetTangentModeLinear(); }
            if (gui.MenuItem("Constant (Step)")) { selectedKey = ctxKey; SetTangentMode(float.PositiveInfinity, float.PositiveInfinity); }
            gui.Separator();
            gui.BeginDisabled(target.Count <= 1);
            if (gui.MenuItem("Delete Key")) {
                PushUndo();
                target.RemoveKey(ctxKey);
                selectedKey = Math.Clamp(selectedKey, 0, target.Count - 1);
                snapshotPushed = false;
                Changed();
            }
            gui.EndDisabled();
        }
        else {
            if (gui.MenuItem("Add Key Here")) {
                PushUndo();
                selectedKey = target.AddKey(ctxTime, ctxValue);
                snapshotPushed = false;
                Changed();
            }
        }
        gui.EndPopup();
    }

    void DrawGrid(IEditorGui gui, IEditorDrawList draw, Vector2 origin, Vector2 size) {
        float scale = gui.Scale;
        uint line = gui.ColorU32(new Vector4(1f, 1f, 1f, 0.06f));
        uint axis = gui.ColorU32(new Vector4(1f, 1f, 1f, 0.22f));
        uint txt = gui.ColorU32(new Vector4(0.7f, 0.72f, 0.78f, 1f));

        float tStep = NiceStep(viewT1 - viewT0, size.X / (70f * scale));
        float vStep = NiceStep(viewV1 - viewV0, size.Y / (40f * scale));

        for (float t = MathF.Ceiling(viewT0 / tStep) * tStep; t <= viewT1; t += tStep) {
            float x = origin.X + (t - viewT0) / (viewT1 - viewT0) * size.X;
            draw.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + size.Y), line);
            draw.AddText(new Vector2(x + 2, origin.Y + size.Y - 14 * scale), txt, t.ToString("0.##"));
        }
        for (float v = MathF.Ceiling(viewV0 / vStep) * vStep; v <= viewV1; v += vStep) {
            float y = origin.Y + (1f - (v - viewV0) / (viewV1 - viewV0)) * size.Y;
            bool isZero = MathF.Abs(v) < vStep * 0.001f;
            draw.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + size.X, y), isZero ? axis : line);
            draw.AddText(new Vector2(origin.X + 2, y + 1), txt, v.ToString("0.##"));
        }
    }

    static float NiceStep(float range, float targetDivisions) {
        if (range <= 0f || targetDivisions <= 0f) return 1f;
        float raw = range / targetDivisions;
        float mag = MathF.Pow(10f, MathF.Floor(MathF.Log10(raw)));
        float norm = raw / mag;
        float nice = norm < 1.5f ? 1f : norm < 3.5f ? 2f : norm < 7.5f ? 5f : 10f;
        return nice * mag;
    }

    void DrawTangentHandles(IEditorGui gui, IEditorDrawList draw, Func<float, float, Vector2> toScreen, Vector2 mouse, bool hovered) {
        AnimationCurve.Keyframe k = target.Keys[selectedKey];
        Vector2 kp = toScreen(k.Time, k.Value);
        const float handleLen = 40f;
        uint hcol = gui.ColorU32(new Vector4(0.9f, 0.6f, 0.3f, 1f));

        Vector2 InDir() => SlopeDir(k.InTangent, toScreen) * -1f;
        Vector2 OutDir() => SlopeDir(k.OutTangent, toScreen);

        Vector2 inP = kp + InDir() * handleLen;
        Vector2 outP = kp + OutDir() * handleLen;
        draw.AddLine(kp, inP, hcol, 1.5f);
        draw.AddLine(kp, outP, hcol, 1.5f);

        DrawTangentDot(gui, draw, inP, mouse, hovered, -1);
        DrawTangentDot(gui, draw, outP, mouse, hovered, +1);

        if (dragTangent != 0 && gui.Input.MouseDown(0)) {
            Vector2 d = mouse - kp;
            if (dragTangent < 0) d = -d;
            float slope = SlopeFromScreenDelta(d, toScreen);
            float inT = k.InTangent, outT = k.OutTangent;
            if (dragTangent < 0) inT = slope; else outT = slope;
            target.SetTangents(selectedKey, inT, outT);
            Changed();
        }
    }

    void DrawTangentDot(IEditorGui gui, IEditorDrawList draw, Vector2 p, Vector2 mouse, bool hovered, int which) {
        const float r = 5f, grab = 9f;
        bool near = (mouse - p).LengthSquared() <= grab * grab;
        draw.AddCircleFilled(p, r, gui.ColorU32(near ? new Vector4(1f, 0.85f, 0.4f, 1f) : new Vector4(0.9f, 0.6f, 0.3f, 1f)));
        if (hovered && near && dragTangent == 0 && gui.Input.MouseClicked(0)) {
            dragTangent = which;
            tangentGrabbedThisClick = true;
            PushUndo();
        }
    }

    bool tangentGrabbedThisClick;

    static Vector2 SlopeDir(float slope, Func<float, float, Vector2> toScreen) {
        if (float.IsInfinity(slope)) return new Vector2(0f, slope > 0 ? -1f : 1f);
        Vector2 a = toScreen(0f, 0f);
        Vector2 b = toScreen(1f, slope);
        Vector2 d = b - a;
        float len = d.Length();
        return len > 1e-4f ? d / len : new Vector2(1f, 0f);
    }

    static float SlopeFromScreenDelta(Vector2 screenDelta, Func<float, float, Vector2> toScreen) {
        Vector2 a = toScreen(0f, 0f);
        Vector2 unitT = toScreen(1f, 0f) - a;
        Vector2 unitV = toScreen(0f, 1f) - a;
        float dt = unitT.X != 0 ? screenDelta.X / unitT.X : 0f;
        float dv = unitV.Y != 0 ? screenDelta.Y / unitV.Y : 0f;
        return MathF.Abs(dt) < 1e-4f ? (dv >= 0 ? float.PositiveInfinity : float.NegativeInfinity) : dv / dt;
    }

    void HandleZoomPan(IEditorGui gui, bool hovered, Vector2 mouse, Vector2 origin, Vector2 size) {
        if (!hovered) return;
        float wheel = gui.Input.MouseWheel;
        if (wheel != 0f) {
            float zoom = MathF.Pow(0.9f, wheel);
            float ft = (mouse.X - origin.X) / size.X, fv = 1f - (mouse.Y - origin.Y) / size.Y;
            float ct = viewT0 + ft * (viewT1 - viewT0), cv = viewV0 + fv * (viewV1 - viewV0);
            viewT0 = ct + (viewT0 - ct) * zoom; viewT1 = ct + (viewT1 - ct) * zoom;
            viewV0 = cv + (viewV0 - cv) * zoom; viewV1 = cv + (viewV1 - cv) * zoom;
        }

        if (gui.Input.MouseDragging(2)) {
            Vector2 d = gui.Input.MouseDelta;
            float dt = d.X / size.X * (viewT1 - viewT0);
            float dv = d.Y / size.Y * (viewV1 - viewV0);
            viewT0 -= dt; viewT1 -= dt;
            viewV0 += dv; viewV1 += dv;
        }
    }

    void DrawKeyInspector(IEditorGui gui) {
        gui.TextDisabled("Keyframe");
        gui.Separator();
        if (selectedKey < 0 || selectedKey >= target.Count) {
            gui.TextWrapped("Click a key to edit it, or double-click the canvas to add one.");
            return;
        }

        AnimationCurve.Keyframe k = target.Keys[selectedKey];
        gui.TextDisabled($"#{selectedKey}");

        float time = k.Time, value = k.Value;
        gui.SetNextItemWidth(-1);
        if (DragWithUndo(gui, "Time##k", ref time, 0.01f)) { selectedKey = target.MoveKey(selectedKey, time, value); Changed(); }
        k = target.Keys[selectedKey]; value = k.Value;
        gui.SetNextItemWidth(-1);
        if (DragWithUndo(gui, "Value##k", ref value, 0.01f)) { selectedKey = target.MoveKey(selectedKey, target.Keys[selectedKey].Time, value); Changed(); }

        gui.Dummy(new Vector2(0, 4));
        gui.TextDisabled("Tangents");
        k = target.Keys[selectedKey];
        float inT = k.InTangent, outT = k.OutTangent;
        bool stepped = float.IsInfinity(inT) || float.IsInfinity(outT);
        if (!stepped) {
            gui.SetNextItemWidth(-1);
            if (DragWithUndo(gui, "In##t", ref inT, 0.05f)) { target.SetTangents(selectedKey, inT, target.Keys[selectedKey].OutTangent); Changed(); }
            gui.SetNextItemWidth(-1);
            if (DragWithUndo(gui, "Out##t", ref outT, 0.05f)) { target.SetTangents(selectedKey, target.Keys[selectedKey].InTangent, outT); Changed(); }
        } else {
            gui.TextDisabled("(stepped)");
        }

        gui.Dummy(new Vector2(0, 4));
        gui.TextDisabled("Tangent mode");
        if (gui.SmallButton("Flat")) SetTangentMode(0f, 0f);
        gui.SameLine();
        if (gui.SmallButton("Linear")) SetTangentModeLinear();
        gui.SameLine();
        if (gui.SmallButton("Step")) SetTangentMode(float.PositiveInfinity, float.PositiveInfinity);

        gui.Dummy(new Vector2(0, 8));
        if (target.Count > 1 && gui.Button($"{EditorIcons.Delete} Delete key", new Vector2(-1, 0))) {
            PushUndo();
            target.RemoveKey(selectedKey);
            selectedKey = Math.Clamp(selectedKey, 0, target.Count - 1);
            Changed();
        }
    }

    bool DragWithUndo(IEditorGui gui, string label, ref float v, float speed) {
        bool changed = gui.DragFloat(label, ref v, speed);
        if (gui.IsItemActivated()) PushUndo();
        if (gui.IsItemDeactivatedAfterEdit()) snapshotPushed = false;
        return changed;
    }

    void SetTangentMode(float inT, float outT) {
        if (selectedKey < 0) return;
        PushUndo();
        target.SetTangents(selectedKey, inT, outT);
        snapshotPushed = false;
        Changed();
    }

    void SetTangentModeLinear() {
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

    void FrameAll() {
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

    void Preset(AnimationCurve src) {
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

    string[] pendingAfter;

    void PushUndo() {
        if (snapshotPushed) return;
        snapshotPushed = true;
        AnimationCurve curve = target;
        Action changed = onChanged;
        string before = curve.ToCompactString();
        string[] after = [before];
        pendingAfter = after;
        EditorCommands.EditAsset($"Curve {curveTitle}",
            applyOld: () => { ApplyString(curve, before); changed?.Invoke(); },
            applyNew: () => { ApplyString(curve, after[0]); changed?.Invoke(); },
            mutate: () => { });
    }

    static void ApplyString(AnimationCurve curve, string compact) {
        AnimationCurve parsed = AnimationCurve.Parse(compact);
        curve.Clear();
        for (int i = 0; i < parsed.Count; i++) curve.AddKey(parsed.Keys[i]);
        curve.PreWrap = parsed.PreWrap;
        curve.PostWrap = parsed.PostWrap;
    }

    void Changed() {
        if (snapshotPushed && pendingAfter is not null)
            pendingAfter[0] = target.ToCompactString();
        onChanged?.Invoke();
    }
}
