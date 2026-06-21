using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BallisticEngine.UI;

namespace BallisticEngine.Editor;

// The authoritative model behind the visual UI Builder window: a live VisualElement tree (the same
// runtime type the player draws + the USS cascade resolves), the current selection, the document's USS
// rules, file path / dirty state, and a self-contained text-snapshot undo stack.
//
// The tree is the single source of truth — the canvas renders it through the real Dx12UIRenderer, the
// inspector edits Style.* on the selected element, and Save serializes it via UxmlWriter/UssWriter. We do
// NOT reuse EditorUndo (that is scene-YAML scoped); a UI document round-trips through its OWN
// (uxml, uss) text pair, which is also exactly what's written to disk, so undo == reload-a-snapshot and
// can never drift from the save format.
//
// PROVENANCE (the inline-shadows-class fix): each element's `InlineStyle` is the AUTHORITATIVE store of
// its inline overrides — the inspector writes there, the writer reads from there, and the canvas resolves
// classes through the real StyleResolver so a class-provided value is never frozen into inline. The
// element's live `Style.*` is the RESOLVED (cascade + inline) value used only for rendering/measure.
//
// Public so the headless test harness can exercise add/remove/reparent/undo/save-load + class assignment
// + the inline/class provenance without a GPU (the document carries NO DX12 dependency).
public sealed class UIBuilderDocument
{
    // Root of the authored tree. Always a Panel sized to the design canvas; children are the user's UI.
    public VisualElement Root { get; private set; }

    // The currently-selected element (null = nothing / the root). The inspector + canvas overlay read it.
    public VisualElement Selection { get; set; }

    // USS rules: selector -> a carrier element whose Style holds the rule body (see UssWriter.Rule). Kept
    // in insertion order for stable .uss output. The carrier's InlineStyle is the rule's authored body.
    public List<UssWriter.Rule> Rules { get; } = new();

    // Where this document saves (the .uxml path; the .uss is the sibling with the same stem). Null = unsaved.
    public string UxmlPath { get; private set; }
    public string UssPath => UxmlPath is null ? null : Path.ChangeExtension(UxmlPath, ".uss");

    public bool Dirty { get; private set; }

    // Bumped on every structural/style mutation so the canvas can skip re-rendering an unchanged document
    // (the per-frame GPU-flush perf fix). The canvas caches the last version it rendered.
    public int Version { get; private set; }

    // Design canvas size (logical px) — the root's box. Editable from the toolbar.
    public float CanvasWidth { get; set; } = 800f;
    public float CanvasHeight { get; set; } = 480f;

    // ---- undo (self-contained text snapshots, keyed by stable element id) ------
    readonly Stack<Snapshot> _undo = new();
    readonly Stack<Snapshot> _redo = new();
    readonly record struct Snapshot(string Uxml, string Uss, string SelectionId, float W, float H);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    int _nextId = 1;

    public UIBuilderDocument() => NewDocument();

    // Start a blank document: a single root panel with a neutral dark background so the canvas reads.
    public void NewDocument()
    {
        _nextId = 1;
        Root = new Panel { Name = "root" };
        Root.Style.Width = Length.Points(CanvasWidth);
        Root.Style.Height = Length.Points(CanvasHeight);
        Root.Style.BackgroundColor = new Color(0.12f, 0.13f, 0.16f, 1f);
        SyncInline(Root);
        Rules.Clear();
        Selection = null;
        UxmlPath = null;
        Dirty = false;
        _undo.Clear();
        _redo.Clear();
        Version++;
    }

    // ---- stable id (survives undo's text round-trip) --------------------------
    // Every element carries a "uib-id" class as a stable identity token so undo can re-select the SAME
    // element after the tree is rebuilt from text (names are user-editable + optional). The token class is
    // hidden from the inspector's class field and never written as a USS selector target.
    const string IdPrefix = "uib-id-";

    static string IdOf(VisualElement el)
    {
        foreach (var c in el.ClassList)
            if (c.StartsWith(IdPrefix, StringComparison.Ordinal)) return c;
        return null;
    }

    void EnsureId(VisualElement el)
    {
        if (IdOf(el) == null) el.AddToClassList(IdPrefix + _nextId++);
    }

    // True for the internal identity-token class (hidden from the user-facing class list).
    public static bool IsInternalClass(string c) => c.StartsWith(IdPrefix, StringComparison.Ordinal);

    VisualElement FindById(string id)
    {
        if (id == null) return null;
        if (IdOf(Root) == id) return Root;
        foreach (var d in Root.Descendants())
            if (IdOf(d) == id) return d;
        return null;
    }

    // ---- inline-override provenance -------------------------------------------
    // Re-derive an element's InlineStyle from its CURRENT Style relative to the value the CLASSES alone
    // would resolve — so only the genuine inline overrides are stored (never a class-provided value). The
    // inspector calls this after every edit to the selected element.
    public void SyncInline(VisualElement el)
    {
        // Baseline = a fresh element carrying the SAME classes, resolved against the sheet. The diff of the
        // element's live style vs. that baseline is exactly its inline overrides.
        var baseline = new Panel();
        foreach (var c in el.ClassList) baseline.AddToClassList(c);
        var sheet = BuildSheet();
        StyleResolver.ResolveElement(baseline, sheet != null ? new List<StyleSheet> { sheet } : null, el.Parent?.Style);
        el.InlineStyle = StyleSerialize.DiffFromBaseline(el.Style, baseline.Style);
        Version++;
    }

    // ---- canvas resolve (apply USS classes for real, then inline on top) ------
    // Resolve the WHOLE tree through the real cascade so classes actually style the canvas. Inline
    // overrides (el.InlineStyle) are replayed by the resolver as the highest layer. Called by the canvas
    // before each render.
    public void ResolveForRender()
    {
        var sheet = BuildSheet();
        var sheets = sheet != null ? new List<StyleSheet> { sheet } : null;
        // Re-apply each element's tracked InlineStyle so the resolver's inline pass sees it (the resolver
        // reads el.InlineStyle), then resolve from scratch top-down.
        StyleResolver.ResolveTree(Root, sheets, null);
    }

    // Build a StyleSheet from the current Rules (each carrier's InlineStyle is the rule body). Returns null
    // when there are no rules. Cheap; rebuilt per resolve (correctness over caching).
    StyleSheet BuildSheet()
    {
        if (Rules.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var r in Rules)
        {
            string body = r.Carrier.InlineStyle ?? StyleSerialize.DiffFromDefaults(r.Carrier.Style);
            sb.Append(r.Selector).Append('{').Append(body ?? "").Append("}\n");
        }
        return StyleSheet.Parse(sb.ToString());
    }

    // ---- mutation API (every mutating call snapshots first for undo) ----------

    public void PushUndo()
    {
        _undo.Push(Capture());
        _redo.Clear();
        if (_undo.Count > 200) {
            var keep = new List<Snapshot>(_undo);
            keep.RemoveAt(keep.Count - 1);
            _undo.Clear();
            for (int i = keep.Count - 1; i >= 0; i--) _undo.Push(keep[i]);
        }
        Dirty = true;
    }

    public void MarkDirty() { Dirty = true; Version++; }

    // Drop a NEW element into `parent` at an ABSOLUTE position given a point in PANEL-absolute logical
    // coords (the drop point). The element is centered on the drop point and absolutely positioned relative
    // to the parent's content origin — so "drop here" lands here, instead of a flex append. This is the
    // primary placement path (palette drag-drop). Selects + snapshots like AddElement.
    public VisualElement DropElement(VisualElement parent, VisualElement child, System.Numerics.Vector2 panelPoint)
    {
        parent ??= Root;
        PushUndo();
        parent.Add(child);
        EnsureId(child);

        // Element's intended size (defaults already applied by the caller) → center on the drop point.
        float w = child.Style.Width.Unit == Length.Kind.Points ? child.Style.Width.Value : 120;
        float h = child.Style.Height.Unit == Length.Kind.Points ? child.Style.Height.Value : 40;
        // Parent content-box origin (border + padding) — the reference for an absolute child's left/top.
        var pr = parent.ResolvedRect;
        var ps = parent.Style;
        float ox = pr.X + Nz0(ps.GetBorderWidth(Edge.Left)) + Nz0(ps.GetPadding(Edge.Left));
        float oy = pr.Y + Nz0(ps.GetBorderWidth(Edge.Top)) + Nz0(ps.GetPadding(Edge.Top));
        child.Style.Position = PositionType.Absolute;
        child.Style.Left = panelPoint.X - ox - w * 0.5f;
        child.Style.Top = panelPoint.Y - oy - h * 0.5f;

        SyncInline(child);
        Selection = child;
        Version++;
        return child;
    }
    static float Nz0(float v) => float.IsNaN(v) ? 0f : v;

    // Add `child` under `parent` (or the root) at the end. Returns child for chaining. Snapshots undo.
    public VisualElement AddElement(VisualElement parent, VisualElement child)
    {
        PushUndo();
        (parent ?? Root).Add(child);
        EnsureId(child);
        SyncInline(child);
        Selection = child;
        Version++;
        return child;
    }

    public void RemoveSelected() => Remove(Selection);

    public void Remove(VisualElement el)
    {
        if (el is null || el == Root) return;
        PushUndo();
        var parent = el.Parent;
        el.RemoveFromHierarchy();
        if (Selection == el) Selection = parent;
        Version++;
    }

    // Duplicate an element (deep, via the UXML round-trip) as a sibling right after it. Returns the clone.
    public VisualElement Duplicate(VisualElement el)
    {
        if (el is null || el == Root || el.Parent is null) return null;
        PushUndo();
        string uxml = UxmlWriter.Write(el);
        VisualElement clone = UxmlLoader.LoadFromText(uxml);
        if (clone is null) return null;
        StripIds(clone);                       // fresh ids so the clone is distinct under undo
        int idx = IndexInParent(el) + 1;
        el.Parent.Insert(idx, clone);
        AssignIdsRecursive(clone);
        Selection = clone;
        Version++;
        return clone;
    }

    // Reparent `el` under `newParent` at `index`. No-op on a cycle (dropping into a descendant).
    public void Reparent(VisualElement el, VisualElement newParent, int index)
    {
        if (el is null || newParent is null || el == Root) return;
        if (el == newParent || IsAncestor(el, newParent)) return;
        PushUndo();
        index = Math.Clamp(index, 0, newParent.ChildCount);
        newParent.Insert(index, el);
        Version++;
    }

    static int IndexInParent(VisualElement el)
    {
        var p = el.Parent; if (p == null) return 0;
        for (int i = 0; i < p.ChildCount; i++) if (p.Children[i] == el) return i;
        return 0;
    }

    static bool IsAncestor(VisualElement maybeAncestor, VisualElement node)
    {
        for (var p = node.Parent; p != null; p = p.Parent)
            if (p == maybeAncestor) return true;
        return false;
    }

    // ---- class assignment (the USS class-list editor + inline reconcile) -------
    public void AddClass(VisualElement el, string cls)
    {
        if (el is null || string.IsNullOrWhiteSpace(cls) || IsInternalClass(cls)) return;
        PushUndo();
        el.AddToClassList(cls.Trim());
        ResolveForRender();      // re-resolve so the class takes effect…
        SyncInline(el);          // …then re-derive inline so we don't double-store the class's values
        Version++;
    }

    public void RemoveClass(VisualElement el, string cls)
    {
        if (el is null || string.IsNullOrEmpty(cls)) return;
        PushUndo();
        el.RemoveFromClassList(cls);
        ResolveForRender();
        SyncInline(el);
        Version++;
    }

    // The user-facing class list (hides the internal identity token).
    public static IEnumerable<string> UserClasses(VisualElement el)
    {
        foreach (var c in el.ClassList) if (!IsInternalClass(c)) yield return c;
    }

    // ---- USS rules -----------------------------------------------------------
    public VisualElement AddRule(string selector)
    {
        PushUndo();
        var carrier = new Panel();
        carrier.InlineStyle = "";
        Rules.Add(new UssWriter.Rule(selector, carrier));
        Version++;
        return carrier;
    }

    public void RemoveRule(int index)
    {
        if (index < 0 || index >= Rules.Count) return;
        PushUndo();
        Rules.RemoveAt(index);
        ResolveForRender();
        Version++;
    }

    public void RenameRule(int index, string selector)
    {
        if (index < 0 || index >= Rules.Count || string.IsNullOrWhiteSpace(selector)) return;
        PushUndo();
        Rules[index] = new UssWriter.Rule(selector.Trim(), Rules[index].Carrier);
        ResolveForRender();
        Version++;
    }

    // Called after the inspector edits a rule's carrier style — re-derive the carrier's inline body and
    // re-resolve the tree so canvas elements carrying that selector update live.
    public void RuleEdited(int index)
    {
        if (index < 0 || index >= Rules.Count) return;
        var c = Rules[index].Carrier;
        c.InlineStyle = StyleSerialize.DiffFromDefaults(c.Style);
        ResolveForRender();
        Version++;
    }

    // Selectors that currently match the selection (for the inspector's "matched rules" readout).
    public IEnumerable<string> MatchedSelectors(VisualElement el)
    {
        if (el is null) yield break;
        foreach (var c in UserClasses(el)) yield return "." + c;
        // Type selector if any rule names this element's type.
        foreach (var r in Rules)
            if (r.Selector.Equals(el.TypeName, StringComparison.OrdinalIgnoreCase)) yield return r.Selector;
    }

    // ---- undo / redo ---------------------------------------------------------
    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Capture());
        Apply(_undo.Pop());
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Capture());
        Apply(_redo.Pop());
    }

    Snapshot Capture() => new(UxmlWriter.Write(Root), SerializeUss(), IdOf(Selection ?? Root), CanvasWidth, CanvasHeight);

    void Apply(Snapshot s)
    {
        LoadFromText(s.Uxml, s.Uss, keepPath: true);
        CanvasWidth = ClampSize(s.W); CanvasHeight = ClampSize(s.H);
        Root.Style.Width = Length.Points(CanvasWidth);
        Root.Style.Height = Length.Points(CanvasHeight);
        Selection = FindById(s.SelectionId);   // by stable id → survives the rebuild even for unnamed elems
        Dirty = true;
        Version++;
    }

    static float ClampSize(float v) => float.IsNaN(v) || v < 16 ? 16 : MathF.Min(v, 4096);

    // ---- serialization / file I/O --------------------------------------------

    public string SerializeUxml() => UxmlWriter.Write(Root);
    public string SerializeUss() => UssWriter.Write(Rules);

    public void Save() => SaveAs(UxmlPath);

    public void SaveAs(string uxmlPath)
    {
        if (string.IsNullOrEmpty(uxmlPath)) return;
        UxmlPath = uxmlPath;
        File.WriteAllText(uxmlPath, SerializeUxml());
        string uss = SerializeUss();
        if (!string.IsNullOrEmpty(uss))
            File.WriteAllText(UssPath, uss);
        Dirty = false;
    }

    public bool Open(string uxmlPath)
    {
        if (!File.Exists(uxmlPath)) return false;
        string uxml = File.ReadAllText(uxmlPath);
        string ussPath = Path.ChangeExtension(uxmlPath, ".uss");
        string uss = File.Exists(ussPath) ? File.ReadAllText(ussPath) : null;
        if (!LoadFromText(uxml, uss, keepPath: false)) return false;
        UxmlPath = uxmlPath;
        Dirty = false;
        _undo.Clear();
        _redo.Clear();
        Version++;
        return true;
    }

    bool LoadFromText(string uxml, string uss, bool keepPath)
    {
        VisualElement root = UxmlLoader.LoadFromText(uxml);
        if (root is null) return false;

        Root = root;
        if (string.IsNullOrEmpty(Root.Name)) Root.Name = "root";

        // Clamp canvas size from the root's authored size (reject 0/NaN/percent/auto → keep current).
        if (Root.Style.Width.Unit == Length.Kind.Points && Root.Style.Width.Value >= 16)
            CanvasWidth = ClampSize(Root.Style.Width.Value);
        if (Root.Style.Height.Unit == Length.Kind.Points && Root.Style.Height.Value >= 16)
            CanvasHeight = ClampSize(Root.Style.Height.Value);

        Rules.Clear();
        ParseUssIntoRules(uss);

        // Re-issue stable ids to every element (loaded files have none; undo snapshots round-trip them as
        // the uib-id class so FindById works). Track the high-water mark so new ids don't collide.
        RebuildIdCounter();
        AssignIdsRecursive(Root);

        Selection = null;
        if (!keepPath) UxmlPath = null;
        return true;
    }

    void RebuildIdCounter()
    {
        int max = 0;
        void Scan(VisualElement el)
        {
            string id = IdOf(el);
            if (id != null && int.TryParse(id.AsSpan(IdPrefix.Length), out int n)) max = Math.Max(max, n);
            foreach (var c in el.Children) Scan(c);
        }
        Scan(Root);
        _nextId = max + 1;
    }

    void AssignIdsRecursive(VisualElement el)
    {
        EnsureId(el);
        foreach (var c in el.Children) AssignIdsRecursive(c);
    }

    static void StripIds(VisualElement el)
    {
        foreach (var c in new List<string>(el.ClassList))
            if (IsInternalClass(c)) el.RemoveFromClassList(c);
        foreach (var ch in el.Children) StripIds(ch);
    }

    // Parse a .uss into editable rules via the REAL StyleSheet parser (comment/variable/at-rule safe),
    // re-deriving each rule's editable body from a carrier the cascade applies. We keep the raw declaration
    // text per rule (the writer re-emits it), so even selectors the carrier can't fully model round-trip.
    void ParseUssIntoRules(string uss)
    {
        if (string.IsNullOrWhiteSpace(uss)) return;
        foreach (var (selector, body) in UssBlocks(uss))
        {
            if (string.IsNullOrEmpty(selector)) continue;
            var carrier = new Panel();
            carrier.InlineStyle = body ?? "";
            if (!string.IsNullOrEmpty(body))
                StyleApplier.ApplyInline(carrier.Style, body.Replace('\n', ' '));
            Rules.Add(new UssWriter.Rule(selector, carrier));
        }
    }

    // Comment-aware block splitter: strips /* */ comments first, then scans selector{body} pairs. Handles
    // a '{' inside a comment (the brace-scanner bug) and leaves variable/at-rule bodies intact as text.
    static IEnumerable<(string selector, string body)> UssBlocks(string uss)
    {
        string s = StripComments(uss);
        int i = 0;
        while (i < s.Length)
        {
            int brace = s.IndexOf('{', i);
            if (brace < 0) yield break;
            int close = MatchBrace(s, brace);
            if (close < 0) yield break;
            string selector = s[i..brace].Trim();
            string body = s[(brace + 1)..close].Trim();
            yield return (selector, body);
            i = close + 1;
        }
    }

    static string StripComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 1;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    static int MatchBrace(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }
}
