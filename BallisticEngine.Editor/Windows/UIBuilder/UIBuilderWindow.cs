using System;
using System.Linq;
using System.Numerics;
using BallisticEngine.UI;

namespace BallisticEngine.Editor;

// The visual UI Builder — a Unity-UI-Builder-style window for designing in-game UI: a palette of element
// types (left, drag-to-canvas/hierarchy or click-to-add), a WYSIWYG canvas rendered through the REAL engine
// UI backend (center, zoom/pan + state preview), and an inspector + hierarchy + StyleSheet (USS) editor
// (right). The authored tree saves to .uxml + .uss that the player loads with UxmlLoader + the USS cascade,
// so what you design is byte-for-byte what ships.
//
// Discovered + shown via [EditorWindowMeta]. The body is pure IEditorGui. All panes operate on one
// UIBuilderDocument (the authoritative tree + selection + rules + undo). Implements IDisposable so the host
// can free the canvas RT + UiHeap slot on teardown / hot-reload (the resource-leak fix).
[EditorWindowMeta("UI Builder", "Window/UI Builder", order: 150, Icon = EditorIcons.Grid, Width = 1180, Height = 720)]
internal sealed class UIBuilderWindow : EditorWindow, IDisposable
{
    readonly UIBuilderDocument _doc = new();
    readonly UIBuilderInspector _inspector = new();
    readonly UIBuilderStylePanel _stylePanel = new();
    UIBuilderCanvas _canvas;

    VisualElement _clipboard;   // copy/paste (a detached clone's UXML text)
    string _clipboardUxml;

    static readonly (string label, string icon, Func<VisualElement> make)[] Palette =
    {
        ("Panel",    EditorIcons.Package,  () => new Panel()),
        ("Label",    EditorIcons.Document, () => new Label("Label")),
        ("Button",   EditorIcons.Wrench,   () => new Button("Button")),
        ("Image",    EditorIcons.Picture,  () => new Image()),
        ("TextField",EditorIcons.Document, () => new TextField()),
        ("Toggle",   EditorIcons.Check,    () => new Toggle()),
        ("Slider",   EditorIcons.Settings, () => new Slider()),
        ("Dropdown", EditorIcons.ChevronDown, () => new Dropdown()),
        ("ScrollView",EditorIcons.Maximize,() => new ScrollView()),
        ("ProgressBar",EditorIcons.Refresh,() => new ProgressBar()),
    };
    const string PalettePayload = "UIB_PALETTE";   // drag-drop payload type (the element label)

    public UIBuilderWindow()
    {
        if (Environment.GetEnvironmentVariable("BALLISTIC_UI_BUILDER") == "1")
        {
            Open = true;
            SeedDemoLayout();
        }
    }

    void SeedDemoLayout()
    {
        var card = new Panel { Name = "card" };
        card.Style.Position = PositionType.Absolute;
        card.Style.Left = 60; card.Style.Top = 60;
        card.Style.Width = Length.Points(300); card.Style.Height = Length.Points(180);
        card.Style.BackgroundColor = new Color(0.16f, 0.18f, 0.24f, 1f);
        card.Style.BorderRadius = 14; card.Style.Padding = 18;
        card.Style.FlexDirection = FlexDirection.Column; card.Style.Gap = 12;
        var title = new Label("Settings") { Name = "title" };
        title.Style.FontSize = 26; title.Style.TextColor = new Color(1, 1, 1, 1);
        var btn = new Button("Apply") { Name = "applyBtn" };
        btn.Style.Width = Length.Points(110); btn.Style.Height = Length.Points(40);
        btn.Style.BackgroundColor = new Color(0.31f, 0.63f, 1f, 1f);
        btn.Style.TextColor = new Color(0, 0, 0, 1); btn.Style.BorderRadius = 8;
        card.Add(title); card.Add(btn);
        _doc.AddElement(_doc.Root, card);
        _doc.Selection = btn;
    }

    public override void OnEnable() => _canvas ??= new UIBuilderCanvas();
    public override void OnDisable() { }   // keep canvas across hide/show; freed in Dispose (host teardown)

    public void Dispose() { _canvas?.Dispose(); _canvas = null; }

    protected override void OnGui(IEditorGui gui)
    {
        if (_canvas == null) OnEnable();

        DrawToolbar(gui);
        gui.Separator();
        HandleShortcuts(gui);

        float scale = gui.Scale;
        float paletteW = 170 * scale;
        float inspectorW = 320 * scale;
        float canvasW = gui.ContentRegionAvail.X - paletteW - inspectorW - gui.ItemSpacing.X * 2;
        if (canvasW < 200) canvasW = gui.ContentRegionAvail.X - paletteW - gui.ItemSpacing.X;
        float bodyH = gui.ContentRegionAvail.Y;

        // ImGui asserts (imgui_widgets.cpp:791) when a child/widget is given a zero dimension —
        // happens when the window is collapsed/too short and avail space rounds to 0. Floor every
        // size we pass so the panel layout degrades gracefully instead of crashing.
        if (bodyH < 1f || paletteW < 1f || canvasW < 1f || inspectorW < 1f) return;

        gui.BeginChild("##palette", new Vector2(paletteW, bodyH), border: true);
        DrawPalette(gui);
        gui.EndChild();
        gui.SameLine();

        gui.BeginChild("##canvas", new Vector2(canvasW, bodyH), border: true);
        _canvas.Draw(gui, _doc, gui.ContentRegionAvail);
        // Canvas is a drop target for palette items — drop at the cursor, into the container under it.
        if (gui.BeginDragDropTarget())
        {
            // While hovering with a palette drag, show where it will land.
            if (_dragPaletteLabel != null)
                _canvas.DrawDropPreview(gui, _doc, gui.Input.MousePos);
            string type = gui.AcceptDragDropPayloadString(PalettePayload);
            if (type != null) DropPaletteOnCanvas(gui, type);
            gui.EndDragDropTarget();
        }
        gui.EndChild();
        gui.SameLine();

        gui.BeginChild("##right", new Vector2(inspectorW, bodyH), border: true);
        DrawHierarchy(gui);
        gui.Separator();
        _inspector.Draw(gui, _doc);
        gui.Separator();
        _stylePanel.Draw(gui, _doc);
        gui.EndChild();

        DrawSaveAsModal(gui);
        DrawUnsavedPrompt(gui);
    }

    // ---- toolbar -------------------------------------------------------------
    void DrawToolbar(IEditorGui gui)
    {
        if (gui.SmallButton($"{EditorIcons.Add} New")) RequestNew();
        gui.SameLine();
        if (gui.SmallButton($"{EditorIcons.FolderOpen} Open")) RequestOpen();
        gui.SameLine();
        gui.BeginDisabled(!_doc.Dirty && _doc.UxmlPath == null);
        if (gui.SmallButton($"{EditorIcons.Save} Save")) Save();
        gui.EndDisabled();
        gui.SameLine();
        if (gui.SmallButton("Save As…")) OpenSaveAs();

        gui.SameLine(); gui.TextDisabled("|"); gui.SameLine();
        gui.BeginDisabled(!_doc.CanUndo);
        if (gui.SmallButton($"{EditorIcons.Undo}")) _doc.Undo();
        gui.EndDisabled();
        gui.SameLine();
        gui.BeginDisabled(!_doc.CanRedo);
        if (gui.SmallButton($"{EditorIcons.Redo}")) _doc.Redo();
        gui.EndDisabled();

        gui.SameLine(); gui.TextDisabled("|"); gui.SameLine();
        float scale = gui.Scale;
        gui.AlignTextToFramePadding(); gui.TextDisabled("Canvas"); gui.SameLine();
        int w = (int)_doc.CanvasWidth, h = (int)_doc.CanvasHeight;
        gui.SetNextItemWidth(60 * scale);
        if (gui.DragInt("##cw", ref w)) { _doc.CanvasWidth = Math.Clamp(w, 16, 4096); SyncRootSize(); }
        gui.SameLine(); gui.TextDisabled("x"); gui.SameLine();
        gui.SetNextItemWidth(60 * scale);
        if (gui.DragInt("##ch", ref h)) { _doc.CanvasHeight = Math.Clamp(h, 16, 4096); SyncRootSize(); }
        // resolution presets
        gui.SameLine();
        if (gui.BeginCombo("##res", "Preset"))
        {
            foreach (var (rn, rw, rh) in Presets)
                if (gui.Selectable($"{rn} ({rw}x{rh})")) { _doc.CanvasWidth = rw; _doc.CanvasHeight = rh; SyncRootSize(); }
            gui.EndCombo();
        }

        gui.SameLine(); gui.TextDisabled("|"); gui.SameLine();
        gui.AlignTextToFramePadding(); gui.TextDisabled("Preview"); gui.SameLine();
        int ps = (int)_canvas.Preview;
        gui.SetNextItemWidth(90 * scale);
        if (gui.Combo("##preview", ref ps, PreviewNames)) _canvas.SetPreview((UIBuilderCanvas.PreviewState)ps);
        gui.SameLine();
        if (gui.SmallButton("Reset View")) _canvas.ResetView();

        gui.SameLine();
        string title = (_doc.UxmlPath != null ? System.IO.Path.GetFileName(_doc.UxmlPath) : "untitled") + (_doc.Dirty ? " *" : "");
        gui.TextDisabled("   " + title);
    }
    static readonly (string, int, int)[] Presets = { ("Full HD", 1920, 1080), ("HD", 1280, 720), ("Tablet", 1024, 768), ("Phone", 390, 844), ("Square", 512, 512) };
    static readonly string[] PreviewNames = { "Normal", "Hover", "Focus", "Active" };

    void SyncRootSize()
    {
        _doc.Root.Style.Width = Length.Points(_doc.CanvasWidth);
        _doc.Root.Style.Height = Length.Points(_doc.CanvasHeight);
        _doc.SyncInline(_doc.Root);   // persist into the root's inline store so the next resolve keeps it
        _doc.MarkDirty();
    }

    // ---- keyboard shortcuts (Delete / Ctrl-D / Ctrl-C/V / arrows nudge) ------
    void HandleShortcuts(IEditorGui gui)
    {
        if (!gui.IsWindowFocusedIncludingChildren() || gui.WantTextInput) return;
        var sel = _doc.Selection;

        if (gui.KeyPressed(EditorGuiKey.Delete) && sel != null && sel != _doc.Root) _doc.RemoveSelected();
        if (gui.KeyCtrl && gui.KeyPressed(EditorGuiKey.D) && sel != null) _doc.Duplicate(sel);
        if (gui.KeyCtrl && gui.KeyPressed(EditorGuiKey.C) && sel != null && sel != _doc.Root)
            _clipboardUxml = UxmlWriter.Write(sel);
        if (gui.KeyCtrl && gui.KeyPressed(EditorGuiKey.V) && _clipboardUxml != null)
            PasteClipboard();

        // arrow nudge (absolute position; 1px, 10px with shift)
        if (sel != null && sel != _doc.Root)
        {
            float step = gui.KeyShift ? 10f : 1f;
            float dx = 0, dy = 0;
            if (gui.KeyPressed(EditorGuiKey.LeftArrow)) dx = -step;
            if (gui.KeyPressed(EditorGuiKey.RightArrow)) dx = step;
            if (gui.KeyPressed(EditorGuiKey.UpArrow)) dy = -step;
            if (gui.KeyPressed(EditorGuiKey.DownArrow)) dy = step;
            if (dx != 0 || dy != 0)
            {
                _doc.PushUndo();
                sel.Style.Position = PositionType.Absolute;
                sel.Style.Left = (float.IsNaN(sel.Style.GetInset(Edge.Left)) ? sel.ResolvedRect.X : sel.Style.Left) + dx;
                sel.Style.Top = (float.IsNaN(sel.Style.GetInset(Edge.Top)) ? sel.ResolvedRect.Y : sel.Style.Top) + dy;
                _doc.SyncInline(sel);
                _doc.MarkDirty();
            }
        }
    }

    void PasteClipboard()
    {
        VisualElement clone = UxmlLoader.LoadFromText(_clipboardUxml);
        if (clone == null) return;
        VisualElement parent = _doc.Selection ?? _doc.Root;
        if (parent != _doc.Root && IsLeaf(parent)) parent = parent.Parent ?? _doc.Root;
        _doc.AddElement(parent, clone);
    }

    // ---- palette (DRAG onto the canvas to place; double-click = quick add to center) ----------------------
    string _dragPaletteLabel;   // the label of the palette item currently being dragged (for the drop preview)

    void DrawPalette(IEditorGui gui)
    {
        gui.PushFont(EditorFont.Bold); gui.Text("Elements"); gui.PopFont();
        gui.TextDisabled("drag onto the canvas");
        gui.Separator();

        bool anyDragging = false;
        foreach (var (label, icon, _) in Palette)
        {
            gui.Selectable($"{icon}  {label}");
            // Double-click = quick-add to the canvas center (no precise placement needed).
            if (gui.IsMouseDoubleClicked(0) && gui.IsItemHovered())
                DropPaletteAtCenter(label);
            if (gui.BeginDragDropSource())
            {
                gui.SetDragDropPayloadString(PalettePayload, label);
                _dragPaletteLabel = label;
                anyDragging = true;
                gui.Text($"{icon}  {label}");
                gui.EndDragDropSource();
            }
        }
        if (!anyDragging) _dragPaletteLabel = null;   // drag ended / none active

        gui.Spacing(); gui.Separator();
        gui.PushFont(EditorFont.Bold); gui.Text("Selection"); gui.PopFont();
        var sel = _doc.Selection;
        gui.BeginDisabled(sel == null || sel == _doc.Root);
        if (gui.Button($"{EditorIcons.Delete} Delete", new Vector2(-1, 0))) _doc.RemoveSelected();
        if (gui.Button("Duplicate", new Vector2(-1, 0)) && sel != null) _doc.Duplicate(sel);
        gui.EndDisabled();
    }

    static VisualElement MakeElement(string label) => Palette.First(p => p.label == label).make();

    // Drop a dragged palette item at the cursor: into the container under it, absolutely positioned there.
    void DropPaletteOnCanvas(IEditorGui gui, string label)
    {
        if (!_canvas.TryDropTarget(gui.Input.MousePos, _doc, out var pt, out var parent))
            parent = _doc.Root;   // dropped outside the canvas image → into the root at... center-ish
        var el = MakeElement(label);
        SizeDefault(el);
        if (parent != null && _canvas.TryDropTarget(gui.Input.MousePos, _doc, out pt, out parent))
            _doc.DropElement(parent, el, pt);
        else
            _doc.DropElement(_doc.Root, el, new Vector2(_doc.CanvasWidth * 0.5f, _doc.CanvasHeight * 0.5f));
        _dragPaletteLabel = null;
    }

    void DropPaletteAtCenter(string label)
    {
        var el = MakeElement(label);
        SizeDefault(el);
        _doc.DropElement(_doc.Root, el, new Vector2(_doc.CanvasWidth * 0.5f, _doc.CanvasHeight * 0.5f));
    }

    static float ElDefaultHeight(VisualElement el) => el switch
    {
        Button or TextField or Toggle or Dropdown => 32f,
        Label => 24f,
        Slider or ProgressBar => 20f,
        _ => 80f,
    };

    static bool IsLeaf(VisualElement el) => el is Label or Image or Slider or ProgressBar or Toggle;

    // ---- hierarchy (select + drag-reorder/reparent) --------------------------
    void DrawHierarchy(IEditorGui gui)
    {
        gui.PushFont(EditorFont.Bold); gui.Text("Hierarchy"); gui.PopFont();
        gui.BeginChild("##hier", new Vector2(0, 150 * gui.Scale), border: false);
        DrawNode(gui, _doc.Root);
        gui.EndChild();
    }

    void DrawNode(IEditorGui gui, VisualElement el)
    {
        gui.PushId(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(el));

        string label = $"{el.TypeName}{(string.IsNullOrEmpty(el.Name) ? "" : $" #{el.Name}")}";
        bool selected = el == _doc.Selection;
        bool leaf = el.ChildCount == 0;

        if (leaf)
        {
            if (gui.Selectable(label, selected)) _doc.Selection = el;
            HierarchyDragDrop(gui, el);
            gui.PopId();
            return;
        }

        gui.SetNextItemOpenOnce(true);
        var flags = EditorTreeFlags.OpenOnArrow | EditorTreeFlags.SpanAvailWidth |
                    (selected ? EditorTreeFlags.Selected : EditorTreeFlags.None) | EditorTreeFlags.DefaultOpen;
        bool open = gui.TreeNodeEx(label, flags);
        if (gui.IsItemClicked()) _doc.Selection = el;
        HierarchyDragDrop(gui, el);
        if (open)
        {
            foreach (var c in el.Children.ToList()) DrawNode(gui, c);
            gui.TreePop();
        }
        gui.PopId();
    }

    // Drag a hierarchy row as a source; drop onto another row to reparent (as a child, appended).
    void HierarchyDragDrop(IEditorGui gui, VisualElement el)
    {
        if (el != _doc.Root && gui.BeginDragDropSource())
        {
            gui.SetDragDropPayloadInt("UIB_NODE", System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(el));
            _dragNode = el;
            gui.Text(el.TypeName);
            gui.EndDragDropSource();
        }
        if (gui.BeginDragDropTarget())
        {
            int? h = gui.AcceptDragDropPayloadInt("UIB_NODE");
            if (h.HasValue && _dragNode != null && _dragNode != el)
                _doc.Reparent(_dragNode, el, el.ChildCount);
            // palette drop onto a hierarchy row → add as child
            string pal = gui.AcceptDragDropPayloadString(PalettePayload);
            if (pal != null) { var ne = MakeElement(pal); SizeDefault(ne); _doc.AddElement(el, ne); }
            gui.EndDragDropTarget();
        }
    }
    VisualElement _dragNode;

    static void SizeDefault(VisualElement el)
    {
        if (el.Style.Width.IsAuto) el.Style.Width = Length.Points(120);
        if (el.Style.Height.IsAuto) el.Style.Height = Length.Points(ElDefaultHeight(el));
        if (el is Panel) el.Style.BackgroundColor = new Color(0.25f, 0.27f, 0.32f, 1f);
    }

    // ---- file ops + unsaved-changes guard ------------------------------------
    Action _pendingAfterPrompt;

    void RequestNew() => GuardUnsaved(() => _doc.NewDocument());
    void RequestOpen() => GuardUnsaved(OpenFile);

    void GuardUnsaved(Action action)
    {
        if (_doc.Dirty) { _pendingAfterPrompt = action; _wantUnsavedPrompt = true; }
        else action();
    }

    bool _wantUnsavedPrompt;
    void DrawUnsavedPrompt(IEditorGui gui)
    {
        if (_wantUnsavedPrompt) { gui.OpenPopup("Unsaved changes##uib"); _wantUnsavedPrompt = false; }
        if (gui.BeginPopupModalAutoResize("Unsaved changes##uib"))
        {
            gui.Text("This document has unsaved changes.");
            gui.Spacing();
            if (gui.Button("Save", new Vector2(90, 0))) { Save(); _pendingAfterPrompt?.Invoke(); _pendingAfterPrompt = null; gui.CloseCurrentPopup(); }
            gui.SameLine();
            if (gui.Button("Discard", new Vector2(90, 0))) { _pendingAfterPrompt?.Invoke(); _pendingAfterPrompt = null; gui.CloseCurrentPopup(); }
            gui.SameLine();
            if (gui.Button("Cancel", new Vector2(90, 0))) { _pendingAfterPrompt = null; gui.CloseCurrentPopup(); }
            gui.EndPopup();
        }
    }

    void OpenFile()
    {
        string path = NativeDialogs.PickFile("Open UXML", "UI Document", new[] { ".uxml" }, DefaultDir());
        if (path != null && !_doc.Open(path))
            Debugging.LogError($"UI Builder: failed to open {path}");
    }

    void Save()
    {
        if (_doc.UxmlPath == null) { OpenSaveAs(); return; }
        _doc.Save();
    }

    // Save-As: pick a folder natively, then prompt for the filename via a modal (no IFileSaveDialog COM,
    // which is vtable-fragile — folder pick + in-editor name field is crash-safe and gives naming control).
    string _saveDir, _saveName = "NewUI";
    bool _wantSaveAs;
    void OpenSaveAs()
    {
        string dir = NativeDialogs.PickFolder("Save UXML into folder", DefaultDir());
        if (dir == null) return;
        _saveDir = dir;
        _saveName = _doc.UxmlPath != null ? System.IO.Path.GetFileNameWithoutExtension(_doc.UxmlPath) : "NewUI";
        _wantSaveAs = true;
    }

    void DrawSaveAsModal(IEditorGui gui)
    {
        if (_wantSaveAs) { gui.OpenPopup("Save As##uib"); _wantSaveAs = false; }
        if (gui.BeginPopupModalAutoResize("Save As##uib"))
        {
            gui.Text("Folder: " + _saveDir);
            gui.SetNextItemWidth(280 * gui.Scale);
            gui.InputText("Name", ref _saveName, 128);
            gui.SameLine(); gui.TextDisabled(".uxml");
            gui.Spacing();
            bool ok = !string.IsNullOrWhiteSpace(_saveName);
            gui.BeginDisabled(!ok);
            if (gui.Button("Save", new Vector2(90, 0)))
            {
                string file = System.IO.Path.Combine(_saveDir, _saveName.Trim() + ".uxml");
                _doc.SaveAs(file);
                gui.CloseCurrentPopup();
            }
            gui.EndDisabled();
            gui.SameLine();
            if (gui.Button("Cancel", new Vector2(90, 0))) gui.CloseCurrentPopup();
            gui.EndPopup();
        }
    }

    static string DefaultDir()
    {
        try
        {
            string assets = AssetDatabase.Project?.AssetsPath;
            if (assets != null)
            {
                string ui = System.IO.Path.Combine(assets, "UI");
                return System.IO.Directory.Exists(ui) ? ui : assets;
            }
        }
        catch { }
        return null;
    }
}
