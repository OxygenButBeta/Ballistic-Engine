# Plan — Editor Fixes (EF1–EF16)

Status: **PLAN ONLY — do not implement until the user gives the command.**
Branch: `dx12-renderer` (current). Editor exe: `BallisticEngine.Editor/`. Renderer: DX12 (`BallisticEngine.DX12/`).
Source: validated live against the codebase 2026-06-17 (file:line grounded below — not guessed).

Goal: fix a batch of editor UX/correctness defects the user reported, plus a **root-and-branch
theme overhaul** (the headline ask). Each chunk lists the **validated root cause**, the **fix
sketch**, and a **DoD/oracle**. Small fixes are byte-localized; the two big ones (EF5 theme,
EF9 windowing) are split into sub-chunks.

---

## Manual multi-chat execution protocol (READ FIRST)

This plan is run **one chat per chunk**, by hand: each chat implements exactly ONE chunk, commits it,
then emits a ready-to-paste prompt for the NEXT chat. You (the user) paste that prompt into a fresh chat.
This keeps each chat's context small and the history bisectable.

**The chunk pointer lives in this section.** Always trust git + this line over any chat's memory:

> ### ▶ NEXT CHUNK: **EF6** (delete the dead Shading-Mode dropdown — full removal confirmed safe on DX12)
> Last committed chunk: **EF4** (FPS/Timing block gated out of the Scene view) · Branch: `dx12-renderer`
>
> **EF4 note (just landed):** the FPS/Timing block no longer shows in the Scene view. Root: a single global
> `showStats` gated the same `stats.Draw` overlay in BOTH views with no view gate, so the Scene view (which
> repaints ON DEMAND — idle until the user interacts) showed a meaningless "FPS" number. Fix (default
> decision (a) — Game-view-only regardless of play state): `StatsPanel.Draw` gained a `bool showTiming`
> param that wraps ONLY the "Timing" section (FPS / Frame / Editor CPU); the Scene-view call site passes
> `showTiming: false`, the Game-view call site passes `showTiming: true`. The Rendering/GPU/GI/Scene
> counters (draws/tris/renderers) STILL show in both views — the plan explicitly allows the Scene view to
> keep a minimal draw/tri counter, just not the FPS number. Touched 2 files: `Panels/StatsPanel.cs` (new
> param + gated block) + `EditorApp/EditorApplication.cs` (the two `stats.Draw` call sites — `showTiming:
> false` Scene, `showTiming: true` Game). `EditorApplication.cs`'s ONLY diff is these two stats hunks (no
> not-mine dirt), so it was stageable wholesale, but staged by exact path anyway (selective protocol). Build
> 0-error (Editor csproj, clean `--no-incremental` scratch dir), oracle EXIT=0 (19 suites). DoD (Scene view
> shows no FPS; Game view unchanged) rides the batched viewport-overlay human-screenshot checkpoint
> (EF1/EF2/EF4/EF6); NOT relaunch-looped (GPU-hang rule).
>
> **For EF6 (next):** delete the dead Shading-Mode dropdown. Root: `EditorDebugViews.Install()` is a no-op on
> DX12 (`EditorDebugViews.cs:6-25`, `// No-op on DX12`); the dropdown (`EditorApplication.cs:1315-1357` per
> the plan — RE-GREP, line numbers drift after EF2/EF4 insertions: search the shading-mode combo / `DebugViewMode`)
> wires `Renderer.DebugViewMode`, `HDRenderer.EditorExtraDebugMode`, `HDRenderer.EditorGiIsolate` — props the
> DX12 renderer NEVER reads. **Full removal CONFIRMED safe (validated):** grep of `BallisticEngine.DX12/` for
> `DebugViewMode`/`Wireframe` = zero reads (only an unrelated GI-isolate hit) — Wireframe/Normals/Depth are ALL
> dead on DX12, not just the buffer modes, so nothing functional is lost. Delete the whole dropdown + its dead
> wiring; remove dangling `EditorDebugComposite`/`EditorExtraDebugMode` scaffolding IF no longer referenced
> anywhere (grep first — the DX12 port TODO may keep the engine-side `DebugView` enum; leave that if still
> referenced). **Do NOT break the GI-isolate path** (`EditorGiIsolate` — separate, still used). DoD: dropdown
> gone, editor builds 0-error, no dangling `EditorDebugComposite`/`EditorExtraDebugMode` references, GI-isolate
> untouched. Section `Docs/Plans/editor-fixes-plan.md` `:861`. EF6 touches `EditorApplication.cs` (shared) →
> again do NOT run the selective-staging empty-set guard against `EditorApplication`; `git diff` it first, and
> if its only diff is your dropdown-removal hunk(s) stage by exact path, else `git add -p` to stage ONLY your
> hunk(s) (+ the plan doc), leaving the not-mine dirt unstaged — exactly as EF1/EF2/EF4/EF8/EF12 did. Verify
> via build (Editor csproj, clean `--no-incremental` scratch dir) + reflection oracle (19 suites, EXIT=0);
> human screenshot batched into the viewport-overlay set; NOT relaunch-looped (GPU-hang rule).
>
> **EF2 note (just landed):** the orientation axis-ball (`OrientationGizmo.Draw`) and the visibility
> eye-menu (`##sceneVisibilityOverlay` in `DrawSceneViewToolbar`) both anchored the viewport's top-right
> corner, so the eye button overlapped the gizmo's lower axis balls. Fix (one hunk in
> `EditorApplication.cs`): the eye-menu's `SetNextWindowPos` Y is pushed DOWN by the gizmo's footprint —
> `eyeMenuY = imageMin.Y + (34+14 + 34+8)*S + margin` (= `imageMin.Y + 90*S + margin`; 90 px = the gizmo
> center offset `radius+14` plus the hover-ring bottom `radius+8`, mirroring `OrientationGizmo.cs:24-25,34`),
> still right-aligned (pivot `(1,0)`), so the axis balls stay fully visible+clickable and the eye button
> sits just below them. Touched ONLY `EditorApplication.cs` — staged selectively (`git add -p`) so the
> pre-existing not-mine dirt (`RenderPassTogglesWindow.Draw(S)` etc.) stayed unstaged. Build 0-error
> (Editor csproj, clean `--no-incremental` scratch dir), oracle EXIT=0 (19 suites). Human-screenshot DoD
> (gizmo fully visible+clickable, eye-menu not overlapping) rides the batched viewport-overlay checkpoint
> (EF1/EF2/EF4/EF6); NOT relaunch-looped (GPU-hang rule).
>
> **For EF4 (next):** the FPS/stats overlay appears in the Scene view in edit mode, which is misleading
> (edit-mode frame timing is on-demand/inconsistent). Root: a single global `showStats` gates BOTH views
> (the `stats.Draw` call sites at `EditorApplication.cs:1749/1974` — re-grep for `stats.Draw`, line numbers
> drift). Fix: keep the Game-view `stats.Draw`; gate the Scene-view one so the FPS readout does NOT show in
> the Scene view. **Open decision the plan pre-resolves → DEFAULT to (a): FPS Game-view-only regardless of
> play state** (alt (b) = Game-view + `SceneManager.IsPlaying` only — do NOT pick (b) unless the user says
> so). The plan allows the Scene view to keep a minimal draw/tri counter if desired, but NOT the FPS number.
> DoD: Scene view shows no FPS; Game view unchanged. Section `Docs/Plans/editor-fixes-plan.md` `:763`. EF4
> likely touches `EditorApplication.cs` (the `stats.Draw` call sites) → again do NOT run the
> selective-staging empty-set guard against `EditorApplication`; `git add -p` and stage ONLY your stats-gate
> hunk(s) (+ the plan doc), leaving the not-mine dirt unstaged — exactly as EF1/EF2/EF8/EF12 did. Verify via
> build (Editor csproj, clean `--no-incremental` scratch dir) + reflection oracle (19 suites, EXIT=0); human
> screenshot batched into the viewport-overlay set; NOT relaunch-looped (GPU-hang rule).
>
> **EF13+EF14 note (just landed):** the Hierarchy gained Collapse All / Expand All toolbar buttons and now
> defaults a freshly-loaded scene to fully collapsed. ALL in `HierarchyPanel.cs` (Editor-only, one file).
> The hierarchy OWNS the tree open-state in a `Dictionary<int,bool> openState` keyed by
> `entity.InstanceId.GetHashCode()` (the id used everywhere else in the panel) — ImGui's implicit per-node
> open-state can't honour an on-demand all-fold OR a collapsed-on-load default without re-fighting the
> user's own arrow toggles every frame, so the tracker is the source of truth. **EF13:** two `EditorIcons.
> GhostButton`s after Delete — `ChevronRight`="Collapse All", `ChevronDown`="Expand All" (both glyphs already
> baked + used elsewhere — no new font codepoint) — set a one-frame `ExpandForce` (`None`/`CollapseAll`/
> `ExpandAll`) that's consumed by the node draw and cleared right after `EndChild`. **EF14:** the
> `ImGuiTreeNodeFlags.DefaultOpen` was DROPPED; instead a node whose id is NOT yet in the tracker is "first
> seen" → defaults **collapsed** (this single rule covers first scene load AND any freshly-created entity,
> with no scene-change detection). **The core invariant (DoD point c):** `SetNextItemOpen` is pushed ONLY on
> a frame where a force is armed OR the node is first-seen; on every other frame ImGui keeps its own state
> and the panel reads `TreeNodeEx`'s return value back into the tracker — so a node the user manually
> expands STAYS expanded across subsequent frames. Only parent nodes (`children.Count > 0`) are tracked
> (leaves have no fold); a `PruneOpenState` drops entries for entities no longer in the scene (delete /
> scene swap), keeping the dict ≤ live-entity size. While the search filter is active the flat list draws no
> tree nodes, so an armed force is simply cleared at frame end (no-op) — correct. Build 0-error (Editor
> csproj, clean `--no-incremental` scratch dir), oracle EXIT=0 (19 suites — the change is pure editor UI, no
> reflection/serializer surface). VISUAL chunk → human-screenshot checkpoint (Collapse All folds the whole
> tree; Expand All unfolds it; a fresh scene loads fully collapsed; a node the user expands stays expanded
> next frame) batched into the editor set; NOT relaunch-looped (GPU-hang rule).
>
> **EF1 reconciliation (no new code this chat):** the EF13+EF14 handoff pointed here for EF1, but EF1's fix
> was ALREADY landed earlier — commit `f48a8447` ("EF5e: real theme overhaul **+ EF1 toolbar fit** + static
> accent + default layout") bundled it. Verified in HEAD at `EditorApplication.cs:2214-2238`
> (`DrawSceneViewToolbar`): the Move/Rotate/Scale buttons size to `bw = max(58*S, max(CalcTextSize(lMove),
> CalcTextSize(lRot), CalcTextSize(lScale)).X + FramePadding.X*2)` with an `// EF1:` comment, and the
> Pivot/Center button does the same (`pivotW = max(58*S, max(CalcTextSize("Pivot"), CalcTextSize("Center")).X
> + framePadX)`). The pill background width was widened to `bw*3 + …` to match. So labels no longer clip to
> "Mov"/"Rota"/"Sca". The plan checklist + this pointer simply hadn't been ticked when EF1 rode in with EF5e.
> Re-verified this chat: Editor csproj builds 0-error (clean `--no-incremental` scratch dir), reflection
> oracle EXIT=0 (19 suites). No code change made — only this bookkeeping (checklist ticked, pointer advanced
> to EF2). The DoD's human-screenshot (full labels, no overlap) is still owed and rides the BATCHED viewport
> overlay checkpoint (EF1/EF2/EF4/EF6) — do NOT relaunch-loop the editor (GPU-hang rule); the user reviews
> the batch.
>
> **EF2 note (SUPERSEDED — now landed; see the "EF2 note (just landed)" at the top of this pointer):** the
> orientation gizmo (axis balls) and the visibility eye-menu OVERLAPPED in the
> viewport's top-right. Root (validated): both anchor top-right within ~10-50px —
> `OrientationGizmo.Draw` centers at `viewMin.X + viewSize.X - radius - 14*scale, viewMin.Y + radius +
> 14*scale` with `radius = 34*scale` (footprint ≈ `82*scale` square top-right, `OrientationGizmo.cs:24-25`),
> while the eye-menu overlay window `##sceneVisibilityOverlay` is pinned to `imageMin.X + imageSize.X -
> margin, imageMin.Y + margin` (top-right pivot `(1,0)`, `EditorApplication.cs:2266-2267` in the SAME
> `DrawSceneViewToolbar`). Fix (plan's preferred): push the eye-menu BELOW the gizmo — offset its
> `SetNextWindowPos` Y down by the gizmo footprint + a gap (≈ `82*scale + a few*S`) so the axis balls stay
> fully visible+clickable and the eye button sits just under them, still right-aligned. (Alt the plan allows:
> move the eye-menu to the top-LEFT cluster with the tools — but prefer below-the-gizmo so the left toolbar
> stays uncluttered.) DoD: human screenshot — gizmo fully visible+clickable, eye-menu not overlapping; build
> 0-error. Section: `Docs/Plans/editor-fixes-plan.md` ~`:668`. **This chunk touches `EditorApplication.cs`**
> (the eye-menu `SetNextWindowPos` lives in `DrawSceneViewToolbar`) — so do NOT run the selective-staging
> guard's empty-set check against `EditorApplication`; instead `git add -p` and stage only your eye-menu hunk
> (+ this plan doc), leaving the pre-existing not-mine dirt (`RenderPassTogglesWindow.Draw(S)` etc.) unstaged,
> exactly as EF1/EF8/EF12 did. NOT relaunch-looped (GPU-hang rule).
>
> **EF8 note (kept for reference):** the Layer Collision Matrix is now its OWN window (Window > Layer Collision
> Matrix), split out of Tags & Layers. New panel `Panels/LayerCollisionMatrixPanel.cs` (`public bool Open`,
> mirrors `TagsLayersPanel`'s `Persist()` + `Begin/End` shell) owns the matrix: the `DrawCollisionMatrix`
> body moved VERBATIM from `TagsLayersPanel` except (a) its `CollapsingHeader("Layer Collision Matrix")`
> wrapper was dropped (it's the whole window now, not a sub-section) and (b) the empty-state hint changed
> "Name some layers ABOVE" → "Name some layers in Tags & Layers" (the layers list lives in the other window
> now). `TagsLayersPanel` keeps only Tags + Layers (removed the `DrawCollisionMatrix` method + its call +
> the matrix `Spacing()`). **Wired exactly like `TagsLayers`** — the four-point pattern the next chunk should
> reuse for any new tool window: (1) `EditorMenus.WindowKeys.LayerCollision = "##win.layercollision"` const +
> `PathToWindowKey["Window/Layer Collision Matrix"] = WindowKeys.LayerCollision` + `[MenuItem("Window/Layer
> Collision Matrix", 25)] static void LayerCollision() => EditorWindows.Toggle(WindowKeys.LayerCollision)`;
> (2) `readonly LayerCollisionMatrixPanel layerCollision = new();` field on `EditorApplication`; (3) the
> three facade-handler switch arms — `ToggleWindow` (`layerCollision.Open = !layerCollision.Open`),
> `OpenWindow` (`= true`), `IsWindowOpen` (`=> layerCollision.Open`); (4) `layerCollision.Draw(S)` added at
> BOTH `tagsLayers.Draw(S)` call sites (fullscreen + normal layout) so it draws in both modes. Both panels
> read the same `LayerManager` store; matrix edits still `LayerSettings.Save` (project config, no scene undo).
> Touched 4 files (new `LayerCollisionMatrixPanel.cs`, `TagsLayersPanel.cs`, `EditorMenus.cs`,
> `EditorApplication.cs` — all mine; the `EditorApplication.cs`/`EditorMenus.cs` edits were the EF8-
> legitimate exception to the selective-staging guard). Build 0-error (Editor csproj, clean
> `--no-incremental` scratch dir), oracle EXIT=0 (19 suites; the A1 Menu/Window registry suite stayed 17/17 —
> it tests the discovery MECHANISM over its own fixtures, so a new real `[MenuItem]` only has to compile).
> Non-GPU logic/wiring chunk → human screenshot (Window menu lists "Layer Collision Matrix"; opening it
> shows the grid; Tags & Layers no longer shows the matrix; a matrix edit persists across a reopen) batched
> into the editor set; NOT relaunch-looped (GPU-hang rule).
>
> **For EF13+EF14 (next):** hierarchy collapse/expand-all toolbar buttons + collapse-by-default on first
> load — DO THESE TOGETHER (one chunk, shared per-node open-state tracker). Root: `HierarchyPanel.cs` toolbar
> `DrawEntities` (~`:32-51`) has +/delete/search but NO Collapse/Expand-All; tree nodes (~`:287-288`) use
> `ImGuiTreeNodeFlags.DefaultOpen` so everything is expanded on load. **NOT just "drop DefaultOpen"** (review
> catch): ImGui tree open-state is implicit per-node in its storage, so to (a) collapse/expand-all on demand,
> (b) default-collapsed on FIRST load, and (c) NOT re-collapse the user's manual expansions every frame, the
> hierarchy needs a small open-state tracker IT owns (keyed by entity id), pushed into ImGui via
> `SetNextItemOpen` ONLY on the frames a force applies (collapse-all / expand-all click, or first-load
> default) — otherwise let ImGui keep the user's per-node state. EF13 = the two toolbar buttons (set
> force-collapse/force-expand for the next frame); EF14 = seed the tracker to collapsed on first scene load.
> DoD: Collapse All folds the whole tree; Expand All unfolds it; fresh scene load → fully collapsed; a node
> the user expands stays expanded across subsequent frames (no per-frame re-collapse). See the EF13+EF14
> section (`Docs/Plans/editor-fixes-plan.md` ~`:941`). Editor-only, non-GPU; verify via build + oracle +
> human screenshot batched; NOT relaunch-looped.
>
> **EF7 note (kept for reference):** the inspector Tag/Layer dropdowns now link to the Tags & Layers project
> window. `DrawTagLayerRow` (`InspectorPanel.cs`) appends, at the BOTTOM of each combo (after the
> existing option loop), an `ImGui.Separator()` + an `ImGui.Selectable($"{EditorIcons.Add} Add Tag...")`
> / `"... Add Layer..."` item; selecting it calls `EditorWindows.Open(EditorMenus.WindowKeys.TagsLayers)`.
> **Decoupling:** the inspector holds NO reference to `TagsLayersPanel` or `EditorApplication` — it routes
> through the static `EditorWindows` facade (the same surface the `[MenuItem("Window/Tags & Layers")]`
> method uses; `EditorApplication.OpenWindow` already maps that key → `tagsLayers.Open = true`). Used
> `Open` (never closes) rather than `Toggle`, so "Add…" reliably brings the panel up even if it's already
> open. New tags/layers defined there persist via the existing `TagManager`/`LayerManager` store
> (`LayerSettings.Save`) and reappear in the dropdown next frame (`TagManager.Tags` /
> `LayerManager.DefinedLayers()` re-read each frame). `EditorIcons.Add` is the already-baked lucide plus
> (same glyph TagsLayersPanel's own "Add Tag" button uses) — no new font codepoint. Touched ONLY
> `InspectorPanel.cs` (mine). Build 0-error (Editor csproj, clean `--no-incremental` scratch dir), oracle
> EXIT=0 (19 suites; `Menu/Window registry (A1)` 17/17 confirms the window-key wiring). Non-GPU
> logic/wiring chunk → human screenshot (open a dropdown → "Add Tag…/Add Layer…" at the bottom opens the
> Tags & Layers window; a tag added there appears in the dropdown) batched into the inspector/editor set;
> NOT relaunch-looped (GPU-hang rule).
>
> **For EF8 (next):** `TagsLayersPanel.Draw` (`Panels/TagsLayersPanel.cs:35`) calls `DrawCollisionMatrix`
> inline in the same window. EF8 = move that matrix UI into its OWN dedicated window (e.g. "Layer Collision
> Matrix"), registered like the other tool windows. Pattern to mirror: add a `WindowKeys.LayerCollision`
> const + a `[MenuItem("Window/Layer Collision Matrix", N)]` in `EditorMenus.cs` → `EditorWindows.Toggle`;
> a new panel class with a `public bool Open` (copy the `TagsLayersPanel.Draw` guard + `Begin/End`
> shell); own it as a field on `EditorApplication` and add it to the `ToggleWindow`/`OpenWindow`/
> `IsWindowOpen` switches (the three sites at `EditorApplication.cs:1438/1456/1469` show the exact pattern
> for `TagsLayers`) AND to the two `tagsLayers.Draw(S)` call sites (`:755` fullscreen + `:830` normal) so
> the new window draws in both layout modes. Both panels read the same `LayerManager` store (matrix edits
> still `LayerSettings.Save` / persist). `TagsLayersPanel` keeps only Tags + Layers. Verify the
> `Menu/Window registry (A1)` oracle suite still passes (it enumerates the `[MenuItem]`s) + build 0-error;
> human screenshot batched. NOT relaunch-looped.
>
> **EF15 note (just landed):** inspector collections gained reorder/clear UI AND polymorphic `List<IFoo>`
> serialize now round-trips. THREE parts. (1) **Serialize fix (RW8) — `SceneSerializer.cs`:**
> `SerializeMemberValue` now detects a `[SerializeReference]` collection whose ELEMENT type is a polymorphic
> base (abstract/interface, or a concrete base a subclass derives from) via new helpers
> `IsPolymorphicElementMember`/`SequenceElementType`/`IsLeafElementType`, and routes it through a new
> `SerializeSequencePolymorphic` that emits a per-element `$type` (the scalar `SerializeReferenceInstance`
> path, applied per element, sharing the cycle-guard `visited` set). A non-`[SerializeReference]` collection
> or a leaf-element list (`List<int>`/`List<Material>`/`List<Vector3>`/`List<EntityRef>`) stays on the
> existing `SerializeValue` → **byte-identical**. (2) **Deserialize gate relaxed — `TryDeserializeReferenceInstance`:**
> the handoff's claim that deserialize was "confirmed ready" was WRONG — the old gate
> `Classify(targetType, member) != Polymorphic` returned false for a recursed element (`member==null`,
> `targetType==IFoo`) because `Classify(IFoo, null)` is `Unsupported` (no attribute visible). Fixed: the gate
> now ALSO fires when `member==null` AND the raw carries a `$type` tag AND `targetType` is a polymorphic base
> (new `IsPolymorphicBaseTarget`) — the `$type` tag is out-of-band (a Vector*/dict/nested map never carries
> it), so its presence on a base target is unambiguous. Symmetric with the serialize side. (3) **Editor UI —
> `DrawCollectionSlot` (`InspectorPanel.cs`):** added per-element reorder up/down (`CollectionMove`, adjacent
> swap, `BeginDisabled` at the ends), insert-above (`CollectionInsertAt`), and a header **Clear**
> (`CollectionClear`, beside Add, shown only when non-empty). Per-element Remove + header Add already existed.
> Row layout is now `[element][↑][↓][+][🗑]`; structural edits are deferred past the row loop (one applied per
> frame) and each is a single-undo `EditorCommands.Structural`. New `EditorIcons.ChevronUp` = lucide `U+E074`
> (chevron-up) — verified via the `lucide.ttf` cmap to be inside the baked range `0xE04C–0xE2A1`, so zero
> tofu risk (no new font codepoint to bake). (4) **Oracle = NEW suite #19** `PolymorphicCollectionTests`
> (`Polymorphic collections (RW8/EF15)`, 20 checks) + `PolymorphicCollectionFixtures`: an interface
> `List<IDamageModifier>` (Crit/Poison/Composite, the last with a nested `[SerializeReference]` Inner) + an
> abstract `Shape[]` (Circle/Square) + a non-polymorphic `List<int>` control + a leaf `Marker`. Asserts
> (a) every concrete element type + member values + ORDER round-trip (incl. the nested polymorphic element),
> (b) byte-stable across two serializations AND a serialize/deserialize/serialize fixed point, (c) the plain
> `List<int>` is byte-identical — exactly **6** `$type` tags total (3 list + 2 array + 1 nested), zero from
> the int list. Touched 6 files (`SceneSerializer.cs`, `InspectorPanel.cs`, `EditorIcons.cs`,
> `Tests.Reflection/Program.cs` + 2 new test files — all mine). Build 0-error (ROOT engine csproj for the
> serialize change + Editor csproj, both clean `--no-incremental` scratch dirs), oracle EXIT=0 (now 19 suites).
> The serialize/deserialize half is CPU/headless/safe; the reorder/insert/clear UI is the only part needing a
> human screenshot → batch into the Inspector-layout set (GPU-hang rule: no relaunch-loop).
>
> **For EF7 (next):** the Tag/Layer dropdowns in the inspector (`InspectorPanel.cs:666-696`, iterating
> `TagManager.Tags` / `LayerManager.DefinedLayers()`) have NO "Add Tag…/Add Layer…" entry. A real management
> window EXISTS (`Panels/TagsLayersPanel.cs:26`, Window > Tags & Layers) but is not linked from the dropdowns.
> EF7 = add an "Add Tag…"/"Add Layer…" item at the bottom of each dropdown that opens the Tags & Layers panel
> (find how other code opens a registered window — likely `EditorWindows`/`panels.Show(key)` or a Window-menu
> path; mirror EF12's `EditorLayout` key usage). EF8 (after) then splits the Layer Collision Matrix out of
> that panel. Non-GPU/logic chunk; verify via build + a human screenshot in the batched set.
>
> **EF10b note (kept for reference):** the top-of-inspector component-LIST search is in. `DrawEntityInspector` now
> draws a conditional search box ABOVE the first component header (after Transform): shown only when the
> entity carries more than `InspectorLayout.ComponentSearchThreshold` (=6) components, via the SAME reusable
> `EditorWidgets.SearchField` EF10a landed (no new widget). The query is a single inspector-owned
> `componentListSearch` field (NOT per-instance — there's one component list per shown entity, unlike the
> per-component member search which needed per-component keys; it carries across selections, fine for a
> transient view filter). The `foreach (Behaviour ...)` draw loop gained a `ComponentMatch(b)` gate that
> filters on the component's DISPLAYED title (`Prettify(b.GetType().Name)` — the exact string
> `ComponentHeader` shows at `:813`, so the filter matches what the user reads), OrdinalIgnoreCase. **Key
> correctness point:** the per-type `typeIndex[bt]` counter is incremented BEFORE the match-skip, so hiding a
> filtered component does NOT shift a visible sibling's Nth-of-type index (which keys prefab-override badges +
> multi-select propagation). Transform is drawn by `DrawTransform` outside `behaviours`, so it's never
> filtered and not counted toward the threshold. No query → `ComponentMatch` passes everyone → byte-identical
> to pre-EF10b. Touched 2 files (`InspectorPanel.cs` + `InspectorLayout.cs` — both mine; added the
> `ComponentSearchThreshold=6` constant alongside `MemberSearchThreshold`). Editor builds 0-error (clean
> `--no-incremental` scratch dir; DX12 compiles standalone since the R0 GI commit), oracle EXIT=0 (18 suites).
> VISUAL chunk → human-screenshot checkpoint (a many-component entity shows the box + filters to matching
> components; a 2-3 component entity shows no box; unfiltered byte-identical), batched into the
> Inspector-layout set; NOT relaunch-looped (GPU-hang rule).
>
> **For EF15 (next):** inspector collections need (1) per-element reorder (up/down) + clear/insert on the list
> drawer (per-element Remove + Add already exist — `InspectorPanel.cs:1608`/`:1575`), and (2) the
> serialize-side polymorphic fix: `SceneSerializer.cs:349 SerializeSequence` calls `SerializeValue(element)`
> NOT `SerializeMemberValue(...)`, so `[SerializeReference] List<IFoo>`/`IFoo[]` elements lose their `$type`
> discriminator. The DESERIALIZE side is CONFIRMED READY (`SceneSerializer.cs:550,634,641` detect a
> `$type`-tagged recursed element even when `member==null`) — fix is serialize-side only, and must keep
> non-polymorphic lists (`List<float>`/`List<Material>`) byte-identical. ORACLE (review catch — reflection
> suite does NOT cover this): add a concrete save→reload→re-save round-trip test over a `List<IFoo>` with ≥2
> distinct concrete element types asserting (a) all concrete types survive, (b) byte-stable across the two
> serializations, (c) a non-polymorphic list is byte-identical to pre-change. See the EF15 section.
>
> **EF10a note (kept for reference):** the per-component member search bar is in. `DrawMemberList` now precomputes
> the `visibleMembers` set (members surviving `[ShowIf]/[HideIf]`) ONCE — it drives both the search-box
> threshold AND the filter. When `visibleMembers.Count > InspectorLayout.MemberSearchThreshold` (=12) a
> search box draws ABOVE the grid via the NEW reusable `EditorWidgets.SearchField(id, hint, ref buffer,
> width, maxLen)` helper (factored out of the inlined `InputTextWithHint` pattern so Hierarchy/Assets/
> Add-Component can adopt it later — adoption NOT done here, optional). The query state is per-component-
> INSTANCE in a `ConditionalWeakTable<object, StrBox>` (`memberSearch`) so each visible component keeps its
> own query and a removed component's entry is GC'd with it (no leak, no eviction). Filtering: `matches` =
> members whose DISPLAYED label (`MemberLabel` = `[LabelText] ?? Prettify(Name)`, mirroring
> `MemberProperty.Label`) contains the query (OrdinalIgnoreCase). Group/header hiding: precomputed
> `groupsWithMatch` (a `[FoldoutGroup]` shows iff it holds a match) + `headersWithMatch` (a `[Header]`
> divider shows iff its SECTION — header up to the next header — has a match). The draw-loop body was
> reordered so chrome is DECOUPLED from the member's own match: `GroupVisible` skips a whole hidden group
> first, then `[Space]`/`[Header]` draw on section/decoration visibility (so a matched field keeps its
> section title even if the header-bearing member's own label doesn't match), and the member's OWN
> `MemberVisible` gate is the LAST step (drops a non-matching row without orphaning its header). No query →
> all predicates pass → byte-identical to pre-EF10a (the changed `foreach` now walks `visibleMembers`, which
> is the same set, in the same order). Validated against `VehicleController` (>12 members, `[Header]`
> sections): typing "steer" leaves only the 6 steer fields under their Steering/Grip headers. Touched 2
> files (`InspectorPanel.cs`, `EditorWidgets.cs` — both mine). Editor builds 0-error (clean `--no-incremental`
> scratch dir; DX12 now compiles too — the `AoResult`→`SsaoResult` gap was resolved by the user's R0 GI
> commit `cb3e9d73` + the GTAO WIP), oracle EXIT=0 (18 suites). VISUAL chunk → human-screenshot checkpoint
> (heavy component shows the box + filters to matching fields/groups; small component shows no box;
> unfiltered byte-identical), batched into the Inspector-layout set; NOT relaunch-looped (GPU-hang rule).
>
> **For EF10b (next):** the component-LIST search is a top-of-inspector box filtering which COMPONENTS draw
> (for many-component entities), same conditional-visibility rule (only show it when the entity has enough
> components). It reuses `EditorWidgets.SearchField`. The component-draw loop is in `DrawContents`/the
> entity branch — find where components iterate (search `foreach (Behaviour` / `DrawComponentHeader`); the
> box sits above the first component header. Conditional threshold: pick a sensible component-count gate
> (the plan leaves it open — a small count like >6 components is reasonable; tune as you see fit). Filter on
> the component's DISPLAY name. EF10a left `EditorWidgets.SearchField` ready; no new widget needed.
>
> **EF11 note (just landed):** the inspector member labels now ellipsize + tooltip instead of silently
> clipping, and slider value text is legible over the amber grab. Both halves of the EF-LAYOUT label rule:
> (1) **Adaptive label / no silent truncation:** `InspectorPanel.Row` + `RowWithTooltip` route their label
> through a new `DrawRowLabel(label, tooltip)` shim → `InspectorLayout.DrawLabelCell(...)`, which ellipsizes
> a label wider than its column and shows the full text on hover (a real `[Tooltip]` wins). The column width
> passed is `ImGui.GetContentRegionAvail().X` measured AT the label cell (column 0) — this is the actual
> remaining label-column width and works for BOTH the top-level proportional `BeginGrid` (≈38% label col)
> AND the fixed-width nested `BeginNestedGrid`, so **no panel-level value-x had to be threaded down** and
> the **top-level `BeginGrid` (:2459) is UNTOUCHED** (depth-0 short labels are visually equivalent — a label
> that fits returns unchanged from `Ellipsize`; only an over-long label now ellipsizes+tooltips instead of
> clipping). **Resolved the EF16↔EF11 double-indent trap:** `DrawLabelCell` applies `DepthIndentTotal`
> ITSELF, so the EF16 manual `ImGui.Indent(LabelDepthIndent())` in Row/RowWithTooltip was REMOVED (and the
> now-unused `LabelDepthIndent()` helper deleted) — the indent is applied exactly once. Also tightened
> `DrawLabelCell`'s ellipsize budget to `columnWidth − indent − gap` (was `− gap`) so a deeply-indented label
> still ellipsizes before touching the value field. (2) **Slider value legibility:** the slider draws its
> value string centered over the frame and the bright amber `SliderGrab` slid under it (white-on-amber
> ~1.8:1). New `EditorTheme.SliderGrabRest` (a darkened amber `0x8A6A30`, white value ~6.5:1) is pushed as
> `ImGuiCol.SliderGrab` around the `##v` slider draw in BOTH inspector adapters (`ImGuiComponentGui` +
> `ImGuiVolumeGui`), scoped to the slider only so the **global EF5 amber accent is untouched**; the
> active/dragging grab stays bright (`SliderGrabActive`, transient). Touched 5 files
> (`InspectorPanel.cs`, `InspectorLayout.cs`, `EditorTheme.cs`, `ImGuiComponentGui.cs`, `ImGuiVolumeGui.cs`
> — all mine). Build 0-error (the Editor compiles clean; the only build failure is the user's IN-PROGRESS
> DX12 `AoResult`→`SsaoResult` rename in `Dx12FrameContext`/`Dx12DeferredLightingPass`/`Dx12CompositePass`
> — NOT mine, NOT staged), oracle EXIT=0 (18 suites). VISUAL chunk → human-screenshot checkpoint (a wide-named
> member like "High Speed Steer Scale" ellipsizes with a hover tooltip; a ranged float slider's value reads
> against the amber grab; short/shallow components unchanged), batched into the Inspector-layout set; NOT
> relaunch-looped (GPU-hang rule).
>
> **For EF10a (next):** the per-component member search bar sits ABOVE the member grid and filters which rows
> draw — it is NOT part of the column model. `InspectorLayout.MemberSearchThreshold = 12` is the conditional-
> visibility gate (only show the box when the component's member count exceeds it). The member-draw loop is
> `DrawMemberList` (the grid opens at `BeginGrid("##members{type.Name}{gridIndex++}")` ~:1079). Factor a small
> reusable `EditorWidgets` search-field helper (Hierarchy/Assets/Add-Component can reuse it later — optional).
> Filter must hide `[Header]`/`[FoldoutGroup]` groups that have no matching child under the filter.
>
> **EF16 note (just landed):** nested member grids no longer march the value box off-screen. The two
> recursion sites (`DrawNestedSlot`, `DrawPolymorphicSlot` in `InspectorPanel.cs`) now wrap their body in a
> new `DrawNestedBody(Action)` that (1) CANCELS the `TreeNode`'s full per-level `IndentSpacing` for the body
> grid (so the grid stops marching one big step right per level) and (2) bumps a static `nestDepth`; the body
> grid is a new `BeginNestedGrid(...)` that uses a FIXED-width label column (`InspectorLayout.LabelColumnWidth`)
> instead of the proportional 0.38/0.62 split, so the value column keeps a usable width at every depth. The
> small per-depth label indent (`InspectorLayout.DepthIndentTotal`) is applied to the LABEL only, in `Row` /
> `RowWithTooltip` (depth 0 → 0px → byte-identical for top-level + the ComponentPreviews/AssetInspectors shim
> rows, which never nest). **Pragmatic deviation from the contract's "single panel-level value-x threaded
> down":** structurally each nested foldout renders INSIDE its parent's value cell (column 1), so the child
> grids do NOT share the panel content-left and a panel-global value-x physically cannot hold across value-cell
> nesting. `BeginNestedGrid` therefore recomputes the anchor from THAT grid's current available width each time
> — `ValueColumnLeft` clamps the label column to ≤62% of the current width, so the value box can never vanish
> however deep (exactly the DoD). The top-level proportional `BeginGrid` (`:2459`) is UNTOUCHED. EF11 will be
> the one to (optionally) unify the top-level grid onto the fixed-column model + route labels through
> `DrawLabelCell` for the ellipsis/tooltip path — when it does, keep depth-0 rows visually equivalent
> (`PreferredLabelWidth=132px` was tuned to the old 0.38 split). New supporting token:
> `EditorTheme.UiScale` (effective DPI×UI scale, published by `ImGuiController.LoadFont`) so the static layout
> helpers convert `InspectorLayout`'s pre-DPI metrics to screen px. Touched 3 files (`InspectorPanel.cs`,
> `EditorTheme.cs`, `ImGuiController.cs` — all mine). Build 0-error (clean `--no-incremental` scratch dir,
> around a running editor's bin lock), oracle EXIT=0 (18 suites). VISUAL chunk → human-screenshot checkpoint
> (a 3-4-deep nested struct/list keeps readable value boxes; short/shallow components unchanged), batched into
> the Inspector-layout screenshot set; NOT relaunch-looped (GPU-hang rule).
>
> **EF-LAYOUT note (just landed):** the ONE inspector column model + its shared helper landed as
> `BallisticEngine.Editor/Panels/Inspector/InspectorLayout.cs` (design note also in the EF-LAYOUT section
> below — read the file header for the authoritative contract). It owns: `ValueColumnLeft` (fixed panel-level
> value-x), `LabelColumnWidth(depth, panelValueLeft, s)` (recovers that x at any depth by narrowing the label
> column), `DepthIndent=12px` / `DepthIndentTotal`, `DrawLabelCell` (per-depth label indent + ellipsis +
> full-text/`[Tooltip]` hover), `Ellipsize`, and the EF10a `MemberSearchThreshold=12`. **NO call sites were
> rewired** — by design the model ships first and EF16→EF11→EF10 each opt their slice in — so the live
> inspector draw is byte-identical to before (oracle stayed 18/18 green; only a NEW file + this plan edit).
> **EF16 is now unblocked and is the FIRST implementer:** make the nested grids (`DrawNestedSlot :1952`,
> `DrawPolymorphicSlot :1896`) stop applying ImGui's full `TreeNode` IndentSpacing to the value column —
> pass `depth` + the panel-level `panelValueLeft` down and give the nested `BeginGrid` a FIXED-width label
> column (`InspectorLayout.LabelColumnWidth(depth, panelValueLeft, s)`) so the value box keeps full width at
> every depth. Keep top-level (depth 0) rows visually equivalent (`PreferredLabelWidth=132px` was tuned to
> the old 0.38 split). DoD: a 4-deep nested struct still shows readable value boxes; short/shallow components
> unchanged. Visual chunk → build clean + hand off a human-screenshot checkpoint (GPU-hang rule: no relaunch-loop).
>
> **EF12 note (kept for reference):** Inspector panel renamed to "Details" everywhere user-facing, KEY unchanged.
> Validated against ImGui source (`ImHashStr` resets the CRC at the last `###`; `CreateNewWindowSettings`
> strips the `###`-prefix before keying the `.ini`), so the safe `Title###Key` pattern keeps the persistent
> identity. Changes: (1) both `panels.Register`/`extraPanels.Register` titles "Inspector"→"Details"
> (`EditorApplication.cs` :156/:181 — KEY stays `EditorLayout.Inspector`); (2) the two `AddTabItem(...,
> "Inspector")` menu labels → "Details" (:911 Add Panel, :1537 Add Tab popup); (3) `DrawDockPanel` (:1495)
> now `Begin($"{d.Title}###{name}")` instead of `Begin(name)` — the docked tab/title now reads the descriptor
> Title (matching the maximized `DrawMaximizedPanel` + multi-instance `DockPanelHost` paths, which already
> used `d.Title`), id still = the KEY so the dock-`.ini`/`.panels`-sidecar/dock-builder all match unchanged;
> (4) Window menu: `[MenuItem("Window/Inspector")]`→`[MenuItem("Window/Details")]` + the `PathToWindowKey`
> map key `"Window/Inspector"`→`"Window/Details"` (value `EditorLayout.Inspector` unchanged so the checkmark
> still binds) in `EditorMenus.cs` :25/:49. **Approved scope decision (user):** the generic `DrawDockPanel`
> change ALSO fixes a pre-existing inconsistency — the Scene Components docked tab now reads "Scene Components"
> (was "Scene", its key) to match the Window menu/maximized view; id stays "Scene" so `.ini` preserved. The
> `EditorLayout.Inspector`/`.SceneComponents` const KEYS are untouched. Touched 2 files: `EditorApplication.cs`
> (mine: title/label/DrawDockPanel hunks — staged selectively with `git add -p` to leave the pre-existing
> not-mine `RenderPassTogglesWindow.Draw(S)` lines unstaged) + `EditorMenus.cs`. Build 0-error (clean
> `--no-incremental` scratch dir), oracle EXIT=0 (18 suites incl. Menu/Window registry A1 17/17). Visual-only
> tab/title/menu change → batched into the editor-screenshot checkpoint (GPU-hang rule: no relaunch-loop).
>
> **EF5 identity decision RESOLVED → (i) faithful UE5** (cool graphite + blue-grey shell + a single
> restrained azure highlight, NO warm accent). The azure accent `0x3D8BD4` (EditorPrefs default) is KEPT;
> the acceptance bar for the whole EF5 series is "looks like UE5". The whole EF5 theme series (EF5a–d) is
> now landed and ready for the user's batched human-screenshot review (GPU-hang rule: no relaunch-loop).
>
> **EF5d note (just landed — LAST EF5 sub-chunk):** the type/spacing + remaining inspector-cluster semantic
> literals are done. (1) **Type/spacing:** routed `StatsPanel`'s 5 stock `ImGui.SeparatorText(...)` section
> dividers (Timing/Rendering/GPU/Global Illumination/Scene) → `EditorDecoration.DrawSectionHeader(...)`, the
> same Caption-font + palette-hairline treatment EF5c gave the inspector cluster (these were the last
> default-look section dividers feeding the residual "flat" feel). The `Display`/`Header`/`Caption` type
> scale is otherwise already applied where warranted (entity-name title = Header @ InspectorPanel:601, meta
> line = Caption @ :613, all section headers = Caption via DrawSectionHeader). LEFT alone deliberately: the
> `CollapsingHeader`s in `TagsLayersPanel`/`SettingsPanel` are INTERACTIVE/collapsible (framed, already read
> as headers) — converting them to non-collapsible section rules would remove function, out of scope. (2)
> **Inspector-cluster semantic literals → EditorTheme tokens** (formal owner per the EF5b handoff):
> `InspectorPanel` prefab-override dots (×2) + prefab-bar text → `EditorTheme.PrefabBlue`; multi-differ "—"
> marker → `Warning`; "Missing (ref)" → `Error`. `ComponentPreviews` animator current/active cyan (×2) →
> NEW `EditorTheme.Info` token (cyan active-highlight — added this chunk); "No PointLight…"/"Assign a
> Prefab…" amber warnings (×2) → `Warning`. `ProfilerPanel` over-budget-zone (≥50% frame) red → `Error`. The
> destructive "Delete N Assets" button red (`InspectorPanel:2314-2315`) → NEW `EditorTheme.Destructive` +
> `DestructiveHovered` tokens (deep desaturated red, base+hovered family mirroring `PrimaryAction`; only
> colored destructive button in Panels/). NOT byte-identical (the cyan/amber/red literals snap to the one
> token family — visual harmonization; behaviour unchanged). What stays as JUSTIFIED literals (annotated):
> the prefab-bar dark-navy SURFACE backing (`:527` — a dark low-alpha bar, NOT an alpha of bright PrefabBlue;
> in-file comment added), accent alpha/scale derivations (`(accent.X,…,α)`), alpha-only overlays/watermarks,
> the dark-on-chip glyph color (`:1248`), the neutral batch-Document icon tint (`:2266`), and the
> `AssetInspectors` material base-color PARSED from the .mat (user data, not chrome). Touched only 5 files:
> `EditorTheme.cs` (+Info/+Destructive tokens), `InspectorPanel.cs`, `ComponentPreviews.cs`, `ProfilerPanel.cs`,
> `StatsPanel.cs` (all fully mine). `EditorApplication.cs`/`EditorMenus.cs`/`TypeCache.cs`/`RenderPassTogglesWindow.cs`
> NOT touched (their pre-existing not-mine dirt is unchanged). Build 0-error (clean `--no-incremental`
> scratch dir), reflection oracle EXIT=0 (all 18 suites green). NOT visually verified — batched into the
> EF5a–d human-screenshot checkpoint (GPU-hang rule).
>
> **EF5c note (just landed):** panel chrome polished by routing the stark stock section/divider widgets
> through the existing `EditorDecoration` primitives (no hand-rolled DrawList chrome). (1) **Section headers:**
> every `ImGui.SeparatorText(...)` in the inspector cluster — the shared `ImGuiComponentGui.Header` adapter
> (so ALL attribute-driven `[Header]` sections at once), `InspectorPanel` (Render Features list + the
> `[Header]` attribute path), `ComponentPreviews` (11 sections), `AssetInspectors` (2) — now calls
> `EditorDecoration.DrawSectionHeader(...)`, which draws a Caption-font, RowCaption-colored label + a
> palette hairline trailing rule instead of ImGui's framed default-look box. (2) **Structural dividers:** the
> toolbar→content `ImGui.Separator()` in `ConsolePanel`, `HierarchyPanel`, and `AssetBrowserPanel`
> (nav-bar→content) now use `EditorDecoration.DrawDivider()` (the quieter `BorderLight` hairline). (3)
> `DrawSectionHeader` was given symmetric built-in vertical pad (`SectionPadY=4f`) + a `PushFont(Caption)`
> so a section title reads as a quiet group rule, not a loud header; the immediately-preceding redundant
> `ImGui.Spacing()` calls were dropped at the converted sites (the pad is now owned in one place). **NOT
> byte-identical** (deliberate visual harmonization — section titles recede, rules are palette-consistent);
> behaviour unchanged. Scope held to EF5c's Inspector/Hierarchy/Assets/Console panels — `StatsPanel`/
> `TagsLayersPanel`/`SettingsPanel` `SeparatorText`/`CollapsingHeader` are OUT of scope and untouched; the
> modal-dialog internal separators in `AssetBrowserPanel` (227/333) + inline favourites separator (498) were
> left as stock (dialog internals, not panel chrome). Touched only the 6 panel files (`AssetBrowserPanel`,
> `ConsolePanel`, `HierarchyPanel`, `InspectorPanel`, `Inspector/Adapters/ImGuiComponentGui`,
> `Inspector/Preview/ComponentPreviews`, `Inspector/AssetInspectors/AssetInspectors` — 7 files) +
> `EditorDecoration.cs` (all fully mine). Editor csproj builds 0-error (clean `--no-incremental` scratch
> dir); reflection oracle EXIT=0 (all 18 suites green). NOT visually verified yet — batched into the EF5a–d
> human-screenshot checkpoint (GPU-hang rule: no relaunch-loop).
>
> **EF5b note (just landed):** the panel "bypass offenders" (hand-typed `SysVec4` color literals that gave
> the UI its raw feel) are now routed through `EditorTheme`. Added a **SEMANTIC tokens** block to
> `EditorTheme.cs` (named by ROLE, not hue): `Error`/`Warning`/`Success`, `PrefabBlue`/`RowChild`/`IconMuted`,
> `PrimaryAction`(+Hovered/Active), `FolderTint`/`FolderTintDim`, the `LogLevel[]` info/warn/error ramp,
> `Hairline`/`TreeGuide`, and `PopupBg`/`InputBg` (modal-prompt surfaces, ramp-derived). Routed: `ConsolePanel`
> (`LevelColors[]`→`EditorTheme.LogLevel`), `HierarchyPanel` (prefab-blue/child-dim/eye-off/tree-guide),
> `AssetBrowserPanel` (prompt titles→`Text`, invalid-name→`Error`, popup/input bg, green Create→`PrimaryAction`,
> favourite + folder-tree gold→`FolderTint`/`FolderTintDim`), `StatsPanel` (border→`Hairline`), `BuildPanel`
> (success/fail summary→`Success`/`Error`), `VolumeProfileEditor` (disabled-override warn→`Warning`). **Tokens
> chosen so this is NOT byte-identical** (it deliberately retunes a handful of slightly-off literals to one
> coherent family — e.g. console warn `0.95,0.80,0.30`→`0xF2CC4D`, child-dim `0.72,0.74,0.78`→`0xB8BDC7`); the
> diff is *visual harmonization*, behaviour unchanged. What stays as JUSTIFIED literals (per the DoD grep):
> alpha-only overlays (`(0,0,0,0)`, `(1,1,1,0.0x)` ghost-button hovers, white-alpha watermark icons),
> alpha/scale DERIVATIONS of an already-token color (`(tint.X,tint.Y,tint.Z,α)`, `(color.X*0.6,…)`,
> `(accent.X,…,α)`), the no-icon-font FALLBACK folder glyph (degraded mode), and the **`Style(ext)` file-type
> color TAXONOMY** (a self-contained data table keyed by extension — annotated in-file as deliberate, not
> chrome). `PrefabBlue` is also used by InspectorPanel:711/811 — but those Inspector usages are OUT of EF5b
> scope (inspector cluster / EF5c–d); the token now exists for them to adopt later. Touched only the six
> panel files + `EditorTheme.cs` (all fully mine); `EditorApplication.cs`/`EditorMenus.cs` NOT touched.
> Editor csproj builds 0-error (clean `--no-incremental` to a scratch dir, around a running-editor bin-copy
> lock); reflection oracle EXIT=0 (all suites green). NOT visually verified yet — batched into the EF5a–d
> human-screenshot checkpoint (GPU-hang rule: no relaunch-loop).
>
> **EF5a note (just landed):** palette + geometry reworked to a deeper-graphite UE5 identity — pure style,
> behaviour byte-unchanged. (1) Geometry (`ImGuiController.ApplyGeometry`): rounding pulled into UE5's small
> band (Window/Child/Popup 5px, Frame/Grab/Tab 4px, Scrollbar 5px — was the soft 6-9px "consumer app" pass);
> spacing/padding/borders unchanged. (2) Palette (`ImGuiController.ApplyColors`): the bg0..titleBg elevation
> ramp pushed darker/cooler — bg0 `#1A1C20`→`#16181C`, bg1 `#212429`→`#1D2026`, bg2 `#282C32`→`#262A31`,
> bg3→`#333842`, header→`#2B3038`, titleBg `#15171A`→`#121418`, menuBar→`#101216`, Tab/TabDimmed darkened to
> match; `textDim` nudged `#848C99`→`#8C94A1` so secondary text clears 4.5:1 even on input frames; border/
> borderLight retuned. (3) `EditorTheme` Bg0..TitleBg ramp mirrored byte-for-byte (the overlay-chrome mirror;
> its comment mandates the sync) + `OverlayBg` (~Bg0@0.82) + RowLabel/RowCaption re-tuned to the new ramp.
> **Contrast VERIFIED (WCAG):** body text `#ECEEF2` = 12-16:1 on every surface; `textDim` ≥4.7:1 on inputs;
> azure accent 4.94:1 on bg0 (>3:1 UI-element min); RowLabel ~9-11:1, RowCaption ~4.7-5.8:1 — none muddy.
> Touched ONLY `ImGuiController.cs` + `EditorTheme.cs` (both fully mine); `EditorApplication.cs` NOT touched
> (its 2 pre-existing not-mine `RenderPassTogglesWindow.Draw(S)` lines are still the only diff there).
> Editor csproj builds 0-error (scratch dir, around running-editor bin-copy lock). NOT visually verified yet
> — batched into the EF5a–d human-screenshot checkpoint (GPU-hang rule: no relaunch-loop).
>
> **EF9d note (just landed):** the Window-menu open-state sync is COMPLETE. The checkmark was already correct
> (`DrawRegistryMenu("Window")` → `EditorWindows.IsOpen(key)` → `IsWindowOpen` → `panels.IsShown(key)`, queried
> every frame; EF9c made that the SAME `Shown` flag it persists, so menu-state and disk-state can't disagree).
> The only real gap was that re-opening a CORE panel from the menu flipped `Shown` false→true and set
> `pendingFocusWindow = key` but NOTHING consumed it for core panels — only the two viewports
> (`SceneView`/`GameView`) called `SetNextWindowFocus` — so a re-opened Inspector re-appeared BEHIND its
> dock-tab neighbour and read as a no-op. EF9d's one-line fix: `DrawDockPanel` now calls
> `ImGui.SetNextWindowFocus()` when its panel == `pendingFocusWindow`, surfacing the re-opened panel (the
> same Unity focus-on-open the viewports already get). Ordering is safe: `panels.DrawCore(DrawDockPanel)`
> runs BEFORE `DrawViewportWindows()` (which clears `pendingFocusWindow` at the end of the frame), and the
> viewport keys never collide with a core-panel key. Human-screenshot verify (re-open Inspector from the
> Window menu → it surfaces; close → checkmark clears) batched into the EF9/windowing visual checkpoint.
>
> **EF9c note (kept for reference):** layout persist/restore is DONE and the `Shown` open/closed state now ROUND-TRIPS
> across restart. EF9c added: (1) `EditorPanelRegistry.HiddenKeys()` / `ApplyHidden(...)` (the closed core
> panels are the persisted unit — viewports are never "closed"); (2) `EditorLayout.SavePanelState/LoadPanelState`
> writing a `<projectStem>.v2.panels` sidecar next to the dock `.ini` (the `.ini` persists window geometry/dock
> node but NOT whether the editor submits the window, so a closed panel would otherwise re-open on next launch);
> (3) wiring — `panels.ApplyHidden(EditorLayout.LoadPanelState())` after `EditorLayout.Load()` on startup, and a
> per-frame change-gated `SavePanelState` at the end of `BuildUI` (closing a panel only flips `Shown`, which does
> NOT dirty ImGui's dock settings, so the existing `WantSaveIniSettings` save can't catch it). `DeleteSaved` now
> also clears the sidecar so "Reset Layout" re-shows every panel (it already called `panels.ResetVisibility()`).
> **PassthruCentralNode REMOVED** (`DockSpace` now uses `ImGuiDockNodeFlags.None`): the central node is ALWAYS
> filled by the Scene/Game view windows so passthrough never engaged visibly, and the review flagged it as a
> maximize/modal-capture breaker — dropping it is byte-identical to the eye and removes the hazard. **For EF9d:**
> the Window menu is registry-driven (`DrawRegistryMenu("Window")` at `EditorApplication.cs:882`); the
> per-panel checkmark/open state reads `EditorWindows.IsOpen` → `IsWindowOpen` → `panels.IsShown(key)`, which is
> now the SAME `Shown` flag EF9c persists. So a closed panel is genuinely "closed" in the registry AND on disk —
> EF9d's job is to confirm the menu checkmarks reflect it and re-opening from the menu works (the wiring at
> `EditorWindowRegistry.cs:72-105` is already correct per the plan; bind/verify, don't rebuild).
>
> **EF9b note (kept for reference):** maximize no longer FIGHTS docking. The fullscreen windows now have their OWN
> ImGui identities — `###maxpanel` (core panels, `DrawMaximizedPanel`) and `###maxinstance` (duplicated
> tabs, `DockPanelHost.DrawMaximizedInstance`) — each with `NoSavedSettings`, instead of reusing the docked
> window's bare label (`"Inspector"`/`"Scene"`/`###KindKey`). The old shared-identity path force-undocked
> the panel on every maximize AND (lacking `NoSavedSettings`) wrote the fullscreen pos/size into that
> window's saved settings — which would have polluted the layout EF9c persists. So **EF9c can now trust the
> docked windows' saved geometry is clean** (maximize never touches it). The EF9a `ref open` close contract
> is intact (close still flips `Shown`/`Open` + `maximize.Clear()` same frame; restore-double-click keys off
> the maximize KEY `name`, not the window label). Editor "fullscreen" is purely an ImGui layout change — it
> does NOT resize the swapchain (only `runtime.Window.OnResizeCallback` → `imgui.WindowResized` +
> `viewport.InvalidateTargetSizes` does), so EF9b introduced NO second/undrained `ResizeBuffers` — the EF3
> coupling DoD is met by construction (the resize harness `Docs/Validation/dx12-resize-harness/` is unchanged
> and still the swapchain-resize guard). **For EF9c:** persisting the layout (`io.WantSaveIniSettings` →
> `EditorLayout.Save()` at `EditorApplication.cs:828`) is already wired; EF9c's real work is restore-on-startup
> + the `PassthruCentralNode` review (`EditorApplication.cs:784`).
>
> **EF3 note for later chunks (important):** the plan's original EF3 root cause ("`Dx12SwapChain.cs:165`
> flushes the WRONG fence — legacy upload, not `frameFence`") was **STALE** — `Dx12Device.Flush()` was
> already hardened in commit `49623af8` (P0b step1) to drain ALL THREE queue fences (render `fence` +
> pipelined `frameFence` + `uploadFence`) before returning, so the drained-resize was already correct.
> Empirically PROVEN: the new checked-in harness `Docs/Validation/dx12-resize-harness/` drives `Resize`
> over a 0×0/shrink/grow/4K→1080p/same-size stress sequence with a real in-flight frame before each
> resize — **no device removal**, in BOTH the default (`FramesInFlight==1`) and `BALLISTIC_DX12_OVERLAP=1`
> (`FramesInFlight==2`) paths. EF3 hardened `Dx12SwapChain.Resize` (explicit drain comment + post-resize
> `currentIndex` re-seed) and added the harness as the permanent regression guard. **EF9b still owes EF3
> a fullscreen re-verify** — but note the editor's "fullscreen" is an ImGui maximized panel (no DXGI mode
> change), so EF9b's fullscreen does NOT resize the swapchain; the only real swapchain resize remains the
> OS window resize through `Dx12BallisticEngineWindow.OnResize`. Re-run the harness after any swapchain/
> `Flush`/fence change.

**Each chat MUST, in order:**
1. Read this plan top-to-bottom. Confirm the NEXT CHUNK pointer above and the chunk's section.
2. Check any **Open Decision** that blocks the chunk. If blocked and unanswered, STOP and ask the
   user — do not guess (e.g. EF5a is blocked on the theme-identity decision).
3. Implement ONLY that one chunk (or sub-chunk). Honor every Cross-cutting rule, especially the
   **GPU-hang safety rule** (EF3/EF5/EF9) and **one-commit-per-chunk**.
4. Verify against the chunk's DoD/oracle. Visual chunks: do NOT relaunch-loop — build clean + (if a
   human screenshot is needed) hand that off as a checkpoint for the user.
5. **Commit** the chunk with the chunk id in the message (e.g. `[editor-fixes] EF3: drained swapchain
   resize + post-resize reset`). Stage explicit paths (no `git add -A`).
6. **Update this plan file**: advance the `▶ NEXT CHUNK` pointer to the next chunk in the execution
   order, set `Last committed chunk`, and tick the Progress checklist below. Commit that doc edit too
   (can be in the same commit as the chunk).
7. **Emit the next-chat handoff prompt** in the MANDATORY template below — never skip a section; empty
   "State to know" / "Gotchas" is not allowed (write "none" only if truly none).

**MANDATORY handoff template (the last thing each chat outputs):**
```
=== NEXT CHAT — paste this into a fresh chat ===
Plan: Docs/Plans/editor-fixes-plan.md  (read it fully first)
Branch: dx12-renderer
Just committed: <chunk id> @ <short SHA> — <one line of what changed>
NEXT CHUNK: <chunk id> — <chunk title>
Blocking open decision: <none | which decision + that it must be answered first>
State to know: <facts the next chat needs that aren't obvious from the plan/code — e.g.
  "EF9b still owes EF3 a fullscreen re-verify"; or "none">
Gotchas hit this chat: <surprises / dead ends to avoid — or "none">
Verify before you start: `git log --oneline -3` shows the commit above; editor csproj builds 0-error.
Your job: implement ONLY <chunk id> per the plan, commit it, update the NEXT CHUNK pointer, then emit
this same handoff for the chunk after it.
=== END ===
```

### Progress checklist (each chat ticks its chunk)
- [x] EF3 — resize crash FIXED (v3). ROOT CAUSE found by headless bisection (NOT the swapchain): the **Hi-Z occlusion pass** (`Dx12GpuDrivenRenderer.BuildHiZ`) re-pointed its BINDLESS Hi-Z SRV only on the FIRST build (`if (hizBindlessIndex < 0)`); on resize `Dx12HiZ.Ensure()` recreates the pyramid resource but the bindless descriptor kept pointing at the DISPOSED old pyramid → the GPU cull shader sampled a freed resource → DXGI_DEVICE_HUNG (PageFaultVA=0). FIX: re-register the all-mips SRV whenever `Ensure()` returns `recreated==true`. Verified: full default resize-stress PASS on CarDemo + Bistro; golden SHA byte-identical. (v1 @ d1799cb7 swapchain hardening + v2 @ ee5edee9 DRED-always/ImGui-grow-drain were correct but not the cause; DRED-always is what enabled v3's diagnosis.) Permanent guards: headless `BALLISTIC_DX12_RESIZE_STRESS=1` mode in `Dx12HeadlessRuntime` + swapchain harness `Docs/Validation/dx12-resize-harness/`.
- [x] EF9a — honor close everywhere (incl. maximized) — `ref open` threaded through both maximized paths; close STICKS + exits fullscreen same frame
- [x] EF9b — maximize/fullscreen (re-verify EF3 fullscreen) — dedicated `###maxpanel`/`###maxinstance` identities + `NoSavedSettings`; maximize no longer undocks/pollutes the docked window; no swapchain resize introduced
- [x] EF9c — layout persist + PassthruCentralNode review — `Shown` open/closed state now round-trips via a `.panels` sidecar (`EditorLayout.Save/LoadPanelState` + `EditorPanelRegistry.HiddenKeys/ApplyHidden`); PassthruCentralNode dropped (central node always filled, removed the maximize/modal-capture hazard)
- [x] EF9d — Window-menu open-state sync — checkmark already queried `panels.IsShown` each frame (EF9c made that the same persisted flag); the bind-gap was that a menu-reopened CORE panel flipped `Shown` but never surfaced (only the two viewports consumed `pendingFocusWindow`). Fix: `DrawDockPanel` now `SetNextWindowFocus()` when its panel == `pendingFocusWindow`, so re-open surfaces it — same Unity focus-on-open the viewports get. No state-vs-disk disagreement possible (EF9c gift).
- [x] EF5a — palette + geometry — identity = (i) faithful UE5 (cool graphite + azure, no warm accent). Reworked `ImGuiController.ApplyGeometry` (rounding into UE5's 4-5px band) + `ApplyColors` (deeper-graphite bg0..titleBg ramp, brighter `textDim` for ≥4.5:1 on inputs) + mirrored the `EditorTheme` Bg0..TitleBg ramp / OverlayBg / RowLabel-RowCaption. Pure style, behaviour byte-unchanged; WCAG contrasts verified (body 12-16:1, accent 4.94:1). Only `ImGuiController.cs`+`EditorTheme.cs` touched. Visual verify batched into the EF5a–d checkpoint.
- [x] EF5b — centralize bypass-color offenders — added a SEMANTIC tokens block to `EditorTheme.cs` (Error/Warning/Success, PrefabBlue/RowChild/IconMuted, PrimaryAction±, FolderTint/Dim, LogLevel[], Hairline/TreeGuide, PopupBg/InputBg) and routed the hand-typed `SysVec4` literals in ConsolePanel/HierarchyPanel/AssetBrowserPanel/StatsPanel/BuildPanel/VolumeProfileEditor through them. Deliberately harmonizes a few slightly-off literals into one family (NOT byte-identical — visual only, behaviour unchanged). Remaining literals in those files are justified (alpha-only overlays, alpha/scale derivations of a token, the no-icon fallback glyph, the `Style(ext)` file-type taxonomy data table — annotated in-file). Only the 6 panels + `EditorTheme.cs` touched. Build 0-error, oracle EXIT=0. Visual verify batched into the EF5a–d checkpoint.
- [x] EF5c — panel chrome polish — routed inspector-cluster `SeparatorText` → `EditorDecoration.DrawSectionHeader` (incl. the shared `ImGuiComponentGui.Header` adapter, so all attribute `[Header]` sections at once) + toolbar `Separator()` → `DrawDivider()` in Console/Hierarchy/Assets. Gave `DrawSectionHeader` Caption-font + symmetric pad so titles recede; dropped now-redundant leading `Spacing()`. Visual-only (not byte-identical), behaviour unchanged; build 0-error, oracle EXIT=0. Out of scope: Stats/TagsLayers/Settings + modal-dialog separators (left stock). Visual verify batched into the EF5a–d checkpoint.
- [x] EF5d — type/spacing tokens everywhere — routed `StatsPanel`'s 5 `SeparatorText` → `EditorDecoration.DrawSectionHeader` (Caption-font + palette hairline, the last stock section dividers); finished the inspector-cluster semantic literals: prefab dots/bar → `PrefabBlue`, multi-differ "—" + ComponentPreviews light/prefab warnings → `Warning`, "Missing (ref)" + ProfilerPanel over-budget → `Error`, animator current/active cyan → NEW `Info` token, destructive "Delete N Assets" button → NEW `Destructive`/`DestructiveHovered` tokens. Justified literals (accent derivations, alpha overlays, dark-on-chip glyph, parsed material base-color, prefab-bar navy surface backing) annotated + left. Type scale otherwise already applied (entity title=Header, meta=Caption, sections=Caption); interactive `CollapsingHeader`s in Tags/Settings left (collapsible, out of scope). Visual-only (not byte-identical), behaviour unchanged. Build 0-error, oracle EXIT=0. LAST EF5 sub-chunk → whole EF5 theme series ready for batched screenshot review.
- [x] EF12 — rename Inspector → "Details" — `panels.Register`/`extraPanels.Register` titles + both `AddTabItem` menu labels + the Window-menu `[MenuItem]` path & `PathToWindowKey` key all "Inspector"→"Details"; `DrawDockPanel` now `Begin($"{d.Title}###{name}")` so the docked tab reads the descriptor Title (validated vs ImGui source: `###` resets the id-hash + strips the `.ini` key prefix, so KEY/`.ini`/`.panels`/dock-builder identity all preserved). Generic change also fixes the pre-existing Scene-Components docked tab "Scene"→"Scene Components" (user-approved). `EditorLayout.*` KEY consts untouched. Build 0-error, oracle EXIT=0.
- [x] EF-LAYOUT — inspector layout model (design + shared helper) — landed the column model + metrics + the shared label primitive as `InspectorLayout.cs` (`ValueColumnLeft`/`LabelColumnWidth`/`DepthIndent`/`DrawLabelCell`/`Ellipsize`/`MemberSearchThreshold`). NO call sites rewired (EF16→EF11→EF10 each opt in) → live inspector byte-identical, oracle 18/18 green. Design note in the EF-LAYOUT section + the file header.
- [x] EF16 — nested indent (fixed value-x) — `DrawNestedSlot`/`DrawPolymorphicSlot` now wrap their body in `DrawNestedBody` (cancels the `TreeNode`'s full per-level `IndentSpacing`, bumps `nestDepth`) + a `BeginNestedGrid` with a FIXED-width label column (`InspectorLayout.LabelColumnWidth`) instead of the proportional 0.38/0.62 split, so the value box keeps a usable width at every nesting depth; the small per-depth label indent (`DepthIndentTotal`) applies to the LABEL only in `Row`/`RowWithTooltip` (depth 0 → 0px → top-level + shim rows byte-identical). Pragmatic deviation: the anchor is recomputed from each nested grid's current width (foldouts render inside the parent value cell, so a single panel-global value-x can't hold); `ValueColumnLeft` clamps label ≤62% so the value never vanishes. Added `EditorTheme.UiScale` token (published by `ImGuiController.LoadFont`). Top-level `BeginGrid` untouched. Build 0-error, oracle EXIT=0. Visual verify batched into the Inspector-layout screenshot set.
- [x] EF11 — adaptive label column + slider value legibility — `Row`/`RowWithTooltip` route their label through `DrawRowLabel` → `InspectorLayout.DrawLabelCell` (ellipsis + full-text/`[Tooltip]` hover), column width = `GetContentRegionAvail().X` at the label cell (works for both the proportional top-level grid AND the fixed nested grid → top-level `BeginGrid` untouched, depth-0 short labels visually equivalent). Resolved the EF16 double-indent trap (removed the manual `Indent`, `DrawLabelCell` owns it; deleted `LabelDepthIndent`) + tightened the ellipsize budget to `columnWidth − indent − gap`. Slider value legibility: new `EditorTheme.SliderGrabRest` (darkened amber) pushed as `SliderGrab` around the `##v` slider in both adapters (`ImGuiComponentGui`/`ImGuiVolumeGui`), scoped → global EF5 accent untouched. Build 0-error (Editor clean; the only failure is the user's in-progress DX12 `AoResult`→`SsaoResult` rename — not mine), oracle EXIT=0. Visual verify batched into the Inspector-layout set.
- [x] EF10a — per-component member search (conditional) — `DrawMemberList` precomputes `visibleMembers` (post-`[ShowIf]`) once → drives both the threshold and the filter; a conditional search box (>`InspectorLayout.MemberSearchThreshold`=12 members) drawn above the grid via the NEW reusable `EditorWidgets.SearchField`; query state per-component-instance in a `ConditionalWeakTable<object,StrBox>` (GC-safe). Filter matches the DISPLAYED label (`MemberLabel` = `[LabelText] ?? Prettify(Name)`); precomputed `groupsWithMatch`/`headersWithMatch` hide `[FoldoutGroup]`/`[Header]` sections with no match. Draw-loop body reordered so chrome is decoupled from the member's own match (group-skip → space/header on section visibility → member's own `MemberVisible` last). No query → byte-identical. Validated on `VehicleController` ("steer" → only the 6 steer fields + their headers). Build 0-error, oracle EXIT=0.
- [x] EF10b — component-list search (conditional) — `DrawEntityInspector` draws a conditional search box above the first component header (after Transform), shown only when the entity has >`InspectorLayout.ComponentSearchThreshold`(=6) components, via the EF10a `EditorWidgets.SearchField`; query is a single inspector-owned `componentListSearch` field (one list per shown entity → no per-instance keying needed). The `foreach (Behaviour ...)` loop gained a `ComponentMatch(b)` gate filtering on the DISPLAYED title (`Prettify(b.GetType().Name)`, the same string `ComponentHeader` shows, OrdinalIgnoreCase). The per-type `typeIndex` counter increments BEFORE the skip so hiding a filtered component never shifts a visible sibling's Nth-of-type index (prefab-override + multi-select keying). Transform is outside `behaviours` → never filtered/counted. No query → byte-identical. Touched `InspectorPanel.cs` + `InspectorLayout.cs` (added `ComponentSearchThreshold=6`). Build 0-error, oracle EXIT=0.
- [x] EF15 — collection reorder/clear + polymorphic list serialize + round-trip test — TWO halves landed. (1) **Serialize fix (RW8):** `SerializeMemberValue` now routes a `[SerializeReference]` collection whose element type is a polymorphic BASE (abstract/interface or concrete-base, via new `IsPolymorphicElementMember`/`SequenceElementType`/`IsLeafElementType`) through a new `SerializeSequencePolymorphic` that emits a per-element `$type` (the scalar `SerializeReferenceInstance` path, per element); non-polymorphic lists (`List<int>`/`List<Material>`) stay on `SerializeValue` → byte-identical. (2) **Deserialize gate relaxed:** `TryDeserializeReferenceInstance` now also fires for a recursed element (`member==null`) when the raw carries a `$type` tag AND `targetType` is a polymorphic base (new `IsPolymorphicBaseTarget`) — the symmetric inverse of the serialize side (the handoff's "deserialize already ready" was wrong: the old `member==null` branch returned false at the `Classify==Polymorphic` gate). (3) **Editor UI:** `DrawCollectionSlot` gained per-element reorder up/down (`CollectionMove`, adjacent swap, disabled at ends), insert-above (`CollectionInsertAt`), and a header **Clear** (`CollectionClear`) beside Add; Remove/Add already existed. New `EditorIcons.ChevronUp` (lucide `U+E074`, inside the baked range — verified via the TTF cmap, zero tofu). All structural edits are one-undo `EditorCommands.Structural`, deferred past the row loop. (4) **Oracle = new suite #19** `Polymorphic collections (RW8/EF15)` (20 checks): interface `List<IDamageModifier>` + abstract `Shape[]` with ≥2 concrete types each + a nested polymorphic element; asserts (a) all concrete types + values + ORDER round-trip, (b) byte-stable across two serializations + a serialize/deserialize/serialize fixed point, (c) a non-polymorphic `List<int>` alongside is byte-identical (exactly 6 `$type` tags, zero from the plain list). Build 0-error (engine ROOT csproj + Editor, clean scratch dirs), oracle EXIT=0 (19 suites). Serialize half is CPU/headless/safe; the reorder UI is the only part needing a human screenshot → batch into the Inspector-layout set (GPU-hang rule: no relaunch-loop).
- [x] EF7 — Tag/Layer "Add…" → open Tags & Layers panel — `DrawTagLayerRow` appends a `Separator` + `Selectable("Add Tag.../Add Layer...")` at the bottom of each combo; selecting it calls `EditorWindows.Open(EditorMenus.WindowKeys.TagsLayers)` (static facade, no reference to the window/app — same surface the Window menu uses). `Open` not `Toggle` so "Add…" always surfaces the panel. New tags/layers persist via TagManager/LayerManager (`LayerSettings.Save`) + reappear next frame. `EditorIcons.Add` = already-baked lucide plus (no new codepoint). Touched only `InspectorPanel.cs`. Build 0-error, oracle EXIT=0 (19 suites, A1 17/17). Human screenshot batched.
- [x] EF8 — split Layer Collision Matrix into its own panel — new `LayerCollisionMatrixPanel` (Window > Layer Collision Matrix) owns the matrix UI (the `DrawCollisionMatrix` body moved verbatim from `TagsLayersPanel`, dropped its `CollapsingHeader` wrapper since it's now the whole window, "above"→"in Tags & Layers" empty-state hint); `TagsLayersPanel` keeps only Tags + Layers. Wired exactly like `TagsLayers`: `WindowKeys.LayerCollision` const + `PathToWindowKey["Window/Layer Collision Matrix"]` + `[MenuItem("Window/Layer Collision Matrix", 25)]`→`EditorWindows.Toggle` in `EditorMenus.cs`; owned as a field on `EditorApplication` + the three switch arms (`ToggleWindow`/`OpenWindow`/`IsWindowOpen`) + both `Draw(S)` call sites (fullscreen `:756` + normal `:831`). Both panels read the same `LayerManager` store; matrix edits still `LayerSettings.Save`. Build 0-error (Editor csproj, clean `--no-incremental` scratch dir), oracle EXIT=0 (19 suites; A1 Menu/Window registry 17/17 confirms the new `[MenuItem]` compiles into the discovery). Touched 4 files (`LayerCollisionMatrixPanel.cs` new, `TagsLayersPanel.cs`, `EditorMenus.cs`, `EditorApplication.cs` — all mine). Human screenshot (two distinct windows; matrix edits persist; Tags & Layers no longer shows the matrix) batched into the editor set; NOT relaunch-looped.
- [x] EF13+EF14 — hierarchy collapse/expand + collapsed-by-default — `HierarchyPanel` now OWNS the tree open-state (`Dictionary<int,bool> openState` keyed by `entity.InstanceId.GetHashCode()`). EF13 = two toolbar GhostButtons (ChevronRight=Collapse All, ChevronDown=Expand All) that arm a one-frame `ExpandForce`; EF14 = a node seen for the FIRST time (id not in tracker) defaults collapsed (covers first scene load + new entities, no scene-change detection). `SetNextItemOpen` is pushed ONLY on a forced/first-seen frame; otherwise ImGui's `TreeNodeEx` return is read back into the tracker so manual expansions persist. `DefaultOpen` flag dropped. Parent nodes only (leaves untracked); tracker pruned to live entities. Editor-only (1 file). Build 0-error, oracle EXIT=0 (19 suites).
- [x] EF1 — gizmo-mode button auto-width — landed bundled with **EF5e** @ `f48a8447` (not a standalone commit): `DrawSceneViewToolbar` (`EditorApplication.cs:2214-2238`) sizes the Move/Rotate/Scale buttons to `bw = max(58*S, widest-of-three CalcTextSize + FramePadding.X*2)` and the Pivot/Center button likewise, with the pill background widened to match — labels no longer clip to "Mov"/"Rota"/"Sca". The checklist/pointer just weren't ticked when it rode in with EF5e; reconciled this chat (no new code), build 0-error + oracle 19/19. Human-screenshot DoD rides the batched viewport-overlay checkpoint (EF1/EF2/EF4/EF6).
- [x] EF2 — gizmo ↔ eye-menu de-overlap — the visibility eye-menu (`##sceneVisibilityOverlay` in `DrawSceneViewToolbar`) was anchored top-right (`imageMin.X+imageSize.X−margin, imageMin.Y+margin`, pivot `(1,0)`) directly under the orientation axis-ball (`OrientationGizmo.Draw`, same top-right corner) → the eye button overlapped the gizmo's lower balls. Fix: push the eye-menu's `SetNextWindowPos` Y DOWN by the gizmo's footprint + gap — `eyeMenuY = imageMin.Y + (34+14 + 34+8)*S + margin = imageMin.Y + 90*S + margin` (90 px = gizmo center offset `radius+14` + hover-ring bottom `radius+8`, mirroring `OrientationGizmo.cs:24-25,34`), staying right-aligned so the axis balls are fully visible+clickable and the eye button sits just below them. Touched ONLY `EditorApplication.cs` (the eye-menu hunk in `DrawSceneViewToolbar`) — staged selectively with `git add -p` to leave the pre-existing not-mine dirt (`RenderPassTogglesWindow.Draw(S)` etc.) unstaged. Build 0-error (Editor csproj, clean `--no-incremental` scratch dir), oracle EXIT=0 (19 suites). Human-screenshot DoD (gizmo fully visible+clickable, eye-menu not overlapping) rides the batched viewport-overlay checkpoint (EF1/EF2/EF4/EF6); NOT relaunch-looped (GPU-hang rule).
- [x] EF4 — FPS scene-view gate — `StatsPanel.Draw` gained `bool showTiming`; Scene-view call passes `false` (no FPS/Frame/Editor-CPU block), Game-view call passes `true` (unchanged). Draw/tri/renderer counters still show in both. Default decision (a): Game-view-only regardless of play state.
- [ ] EF6 — delete dead shading-mode dropdown

---

### Cross-cutting rules (honor every chunk)
- **GPU-hang safety (standing rule):** EF3 + EF9 + EF5 touch swapchain/window/style and CAN hang
  the GPU. NEVER relaunch a hanging build in a loop — a TDR has hard-crashed the dev PC before.
  On first device-removal: stop, capture DRED, make safe, commit, diagnose WITHOUT relaunching.
  Prefer headless verification (`bal render` / `BALLISTIC_SCREENSHOT_PAUSED=1`) over launching the editor.
- **One commit per chunk (bisect discipline):** every EFx (and every sub-chunk EF5a/EF9b/…) lands as
  its own commit with the chunk id in the message, so a regression — especially a GPU hang — can be
  bisected to a single chunk and reverted cleanly. Never batch two chunks into one commit.
- **Rebuild the ROOT engine csproj** after engine changes, not just the Editor exe (stale-dll lesson).
- **Editor is GPU-launch-gated:** most of these need a human to look at the editor. Worker chunks
  that change pure layout/logic verify via the reflection test oracle + 0-error build; visual chunks
  hand off "needs human screenshot" rather than relaunch-looping.
- **Batch the human-screenshot checkpoints:** EF1/EF2/EF4/EF6 (viewport overlay), EF5a–d (theme),
  EF9 fullscreen and the Inspector-layout set all need a human to look. Group each cluster's visual
  verification into ONE editor launch (the user reviews several chunks at once) instead of one
  launch per chunk — fewer round-trips, and fewer risky editor launches.
- Oracle for non-visual correctness: `dotnet run --project BallisticEngine.Tests.Reflection`
  (B1/B2/F3 suites GREEN) + Editor csproj builds 0 CS-errors (MSB bin-copy lock from a running
  editor = environmental, ignore).
- `git add -A` FORBIDDEN while the tree is dirty (lots of untracked unrelated files) — stage explicit paths.

### Plan-review resolutions (validated 2026-06-17, in response to the review)
- **EF9 / "KEEP DockPanelHost" rationale — CONFIRMED & PRESERVED.** Read `DockPanelHost.cs`: it exists
  to support MULTIPLE INSTANCES of a panel type (Unity/VS-style: "Inspector", "Inspector 2", each with
  its own factory-made state), with SINGLETON protection for Scene/Game views (they back the one
  renderer target) and a fullscreen router that recognises a maximized duplicated tab (`OwnsLabel`,
  `DrawMaximizedInstance`). So the rewrite must NOT regress multi-instance + singleton + per-instance
  state. **Revised EF9 stance: keep DockPanelHost's multi-instance role; fix docking ON TOP of it
  (DockSpace + per-window p_open + native maximize) rather than deleting the host.** See revised EF9.
- **EF15 deserialize — CONFIRMED READY (fix is serialize-side only).** `SceneSerializer.cs:550,634,641`
  show the deserialize path already detects a `$type`-tagged element by (abstract element type + tag
  present) even for recursed collection elements (`member==null`). The only gap is serialize-side:
  `:349 SerializeSequence` calls `SerializeValue` (no per-element `$type`). Fix scope is small; the
  round-trip oracle below makes "byte-stable" concrete.
- **EF6 Wireframe — CONFIRMED DEAD on DX12 (full removal safe).** Grep of `BallisticEngine.DX12/` for
  `DebugViewMode`/`Wireframe`: zero reads (only an unrelated GI-isolate hit). Wireframe/Normals/Depth
  are ALL non-functional on DX12, not just the buffer modes. Full dropdown removal loses nothing.
- **EF5 identity decision — RESOLVED 2026-06-17 → (i) faithful UE5** (cool, monochrome graphite +
  restrained blue-grey shell + a single azure highlight `0x3D8BD4`, NO warm accent). This drives BOTH the
  accent and the "does it look like UE5" oracle for the whole EF5 series. EF5a landed against it.

---

## Validated architecture facts (file:line grounded)

**Viewport overlay (EditorApplication.cs):**
- Gizmo-mode buttons (Move/Rotate/Scale) drawn `EditorApplication.cs:2150,2157-2161` with a **fixed
  width `bw = 58 * S`** passed to `GizmoModeButton(...)` → `ImGui.Button(label, new SysVec2(width,0))`
  at `:2119`. No `CalcTextSize` → icon+label overflow gets clipped. **(EF1)**
- Orientation gizmo at `Gizmo/OrientationGizmo.cs:25` (`radius=34*scale`, center pinned
  `viewRight - 48*scale, viewTop + 48*scale` → footprint `viewRight-82*scale .. viewRight`).
  Visibility eye-menu at `EditorApplication.cs:2193-2194` (`SetNextWindowPos` top-right pivot,
  margin `OverlayMargin*S = 10*S`). They share the top-right corner → overlap. **(EF2)**
- FPS/stats overlay: `stats.Draw(...)` called for Scene view `EditorApplication.cs:1749-1751`
  (`RenderStats.Scene`) AND Game view `:1974-1976` (`RenderStats.Game`), both gated only by a single
  global `showStats` (decl `:76`) — no `SceneManager.IsPlaying` / view-type gate. **(EF4)**
- Shading-mode dropdown drawn `EditorApplication.cs:1315-1357`; wires `Renderer.DebugViewMode`,
  `HDRenderer.EditorExtraDebugMode`, `HDRenderer.EditorGiIsolate` (`HDRenderer.cs:20,41,47,54`).
  **DEAD on DX12:** `EditorDebugViews.Install()` is a no-op (`EditorDebugViews.cs:6-25`,
  `// No-op on DX12`), `EditorDebugComposite` delegate never invoked, DX12 renderer never reads
  these props. **(EF6)**

**DX12 swapchain / windowing:**
- Resize path `Dx12SwapChain.cs:162-177`: `dev.Flush()` at `:165` waits on the **legacy `fence`**
  (shared with asset uploads), NOT `frameFence` used by pipelined frames → back-buffer RTV can still
  be bound in an in-flight frame when released at `:166` → **device removal on `ResizeBuffers` `:168`**.
  Present error path captures DRED `:129-138`. Window resize forwarded
  `Dx12BallisticEngineWindow.OnResize → swapChain.Resize()` immediately, no frame-sync. **(EF3)**
- Docking: `DockSpace(... ImGuiDockNodeFlags.PassthruCentralNode)` `EditorApplication.cs:783-784`.
  Core panels via `EditorPanelRegistry.DrawCore` (`EditorPanelRegistry.cs:90-96`, writes back
  `d.Shown` on close — close DOES flip the flag). Extra instances via `DockPanelHost.DrawAll`
  (`:97-105`, writes back `inst.Open`). Maximize is a custom state machine drawing a fullscreen
  docked window (`EditorApplication.cs:728-740`, `DrawMaximizedPanel :1548-1575` with
  `NoResize|NoMove|NoDocking` and **no `p_open` wiring** → close ignored while maximized).
  `EditorWindowRegistry` menu wiring is correct (`EditorWindowRegistry.cs:72-105`). ImGui.NET docking
  branch is present and in use (DockSpace already called). **(EF9)**

**Inspector (InspectorPanel.cs):**
- Member grid `BeginGrid :2445-2448` = 2-col `BeginTable` with **fixed label col `WidthStretch,0.38f`**
  / value `0.62f`; label drawn `TextUnformatted` `:2463` with no clip-guard → long labels clip. **(EF1/EF11)**
- Nested members: `DrawNestedSlot :1973` uses `TreeNodeEx(... DefaultOpen)` → ImGui fixed indent
  (~20-25px) **per level**, stacking → value column pushed off-screen on deep nesting. **(EF16)**
- Collections `DrawCollectionSlot`: **per-element Remove ALREADY EXISTS** `:1608` (`EditorIcons.Delete`),
  Add `:1575`. Missing = reorder/clear + the polymorphic-serialize bug:
  `SceneSerializer.cs:349 SerializeSequence` uses `SerializeValue(element)` NOT
  `SerializeMemberValue(...)` → `[SerializeReference] List<IFoo>` loses per-element `$type` (RW8). **(EF15)**
- Tag/Layer dropdowns `:666-696` iterate `TagManager.Tags` / `LayerManager.DefinedLayers()` directly —
  **no "Add Tag.../Add Layer..." entry**. A real management window EXISTS:
  `Panels/TagsLayersPanel.cs:26` (Window > Tags & Layers) but is NOT linked from the dropdowns; it
  ALSO draws the collision matrix inline (`DrawCollisionMatrix(scale)` `:35`). **(EF7/EF8)**
- Inspector title hardcoded `"Inspector"` in `EditorApplication.cs:179` `panels.Register(...)` (also
  the extra-panel register `:156`); flows through `DockPanelHost.cs:76`. **(EF12)**
- **No per-component member search** exists; only an "Add Component" search buffer
  (`InspectorPanel.cs:51`). No reusable EditorWidgets search-field helper — each search inlines its
  own `InputTextWithHint`. **(EF10)**

**Hierarchy (HierarchyPanel.cs):**
- Tree nodes `:287-288` use `ImGuiTreeNodeFlags.DefaultOpen` → all expanded on load. **(EF14)**
- Toolbar `DrawEntities :32-51` has +/delete/search only — **no Collapse/Expand All**. **(EF13)**

**Theme (ALREADY HAS INFRASTRUCTURE — overhaul not greenfield):**
- `ImGuiBackend/EditorTheme.cs` (type scale Display/Header/Body/Caption + drawer-row palette +
  surface palette Bg0..TitleBg + overlay chrome) and `ImGuiBackend/EditorDecoration.cs` (DrawList
  primitives: cards/dividers/badges/accent stripes) **EXIST**.
- Central style entry `ImGuiController.ApplyGeometry/ApplyColors :229-343` (rounding 6-9px, accent
  from `EditorPrefs.Accent`). **Default accent = azure `0x3D8BD4`, NOT the orange in old screenshots.**
- Fonts centralized `ImGuiController.LoadFont :95-152` (Inter + Lucide, DPI-aware).
- **Bypass offenders (hardcoded colors, the "raw" feel):** `AssetBrowserPanel.cs` (lines
  224,262-263,280-282,330,480,731-732,746,906,1212-1215), `ConsolePanel.cs:24-28,200-209`
  (`LevelColors[]`), `HierarchyPanel.cs:299` (prefab blue), scattered in VolumeProfileEditor/StatsPanel/
  BuildPanel. **(EF5)**

---

## EF1 — Viewport gizmo-mode buttons clip ("Mov"/"Rota"/"Sca")
Root: fixed `bw = 58*S` (`EditorApplication.cs:2150`) ignores icon+label width.
Fix: size each button to `max(58*S, CalcTextSize(label).X + framePadding*2 + iconPad)` (or auto-size
and let the toolbar grow), so Move/Rotate/Scale/Pivot/Snap all read in full. Keep min width so the
3 mode buttons stay visually equal (compute the max label width once, apply to all three).
DoD: human screenshot shows full labels, no overlap. Non-visual guard: build 0-error.

## EF2 — Orientation gizmo ↔ Visibility eye-menu overlap (top-right corner)
Root: both anchored top-right within ~10-50px (`OrientationGizmo.cs:25` + `EditorApplication.cs:2193`).
Fix: reserve the gizmo's footprint (`82*scale` tall/wide) and push the visibility menu BELOW it (offset
its `SetNextWindowPos` Y down by gizmo height + gap), OR move the eye-menu to the top-LEFT cluster with
the toolbar. Prefer: eye-menu below the gizmo, right-aligned, so the axis balls stay fully clickable.
DoD: human screenshot — gizmo fully visible+clickable, eye-menu not overlapping.

## EF3 — Window resize / fullscreen → DX12 device removal ⚠️ CRASH (high priority) — ⚠️ REOPENED (still crashes)
**STATUS 2026-06-17 (after v1 @ d1799cb7): RESIZE STILL CRASHES THE LIVE EDITOR.** A user drag-resize
device-removed with `DXGI_ERROR_DEVICE_HUNG` (0x887A0006) at **`Dx12SwapChain` Present** (not at
`ResizeBuffers`). So the swapchain `Resize` itself is NOT the culprit — a GPU command EARLIER in the frame
hung; Present is just where it surfaced. v1's swapchain hardening + the resize-harness are CORRECT but
INSUFFICIENT (the harness exercises only swapchain clear+present, not the editor's real render path).
**Asymmetry that localizes it:** the PLAYER (`Dx12WindowedRuntime.OnResize`) resizes the swapchain AND the
renderer offscreen target SYNCHRONOUSLY in lockstep and does NOT crash; the EDITOR (`PresentToScreen=false`)
has (a) the ImGui present pass sampling the offscreen `ldr` via the shared `UiHeap`, and (b) a DECOUPLED,
panel-sized offscreen `ldr` resize deferred to a later frame via `viewport.InvalidateTargetSizes()`
(`EditorApplication.cs:251-253`) — `ldr` is disposed+recreated in `AllocateResolutionTargets`
(`DX12HDRenderer.cs:448`, which itself `dev.Flush()`es). The exact faulting op is NOT yet proven (candidates:
mismatched-dimension bind during the drag storm; an ImGui upload-buffer grow disposing an in-flight buffer;
a UiHeap descriptor overwrite). A scratch real-renderer repro is blocked (needs full engine bootstrap —
`DefaultTextures` NRE). **v2 follow-up shipped = MAKE-SAFE + DIAGNOSTICS, not the proven fix:**
(1) DRED **page-fault tracking is now ALWAYS-ON** (`Dx12Device` ctor — negligible cost; auto-breadcrumbs
still opt-in via `BALLISTIC_DX12_DRED=1`) so the NEXT crash logs the faulting VA without a special relaunch;
(2) hardened `ImGuiDx12Renderer.EnsureBuffers` to `dev.Flush()` before disposing an in-flight upload buffer
on a grow (a real latent UAF, defensive). **NEXT (needs the user, ONE careful repro — GPU-hang rule, no
relaunch-loop):** rebuild the editor, reproduce the resize crash ONCE, read `engine.jsonl` for the
`[DX12] Present device-removed` line — it will now carry `DRED=PageFaultVA=0x… reads/writes=…` naming the
freed allocation. That VA → the faulting resource → the real fix (likely: resize the editor offscreen target
in lockstep, or guard the present's sampled `ldr`/descriptor across the resize). Swapchain-side facts below
remain valid.

**(v1, still valid) VALIDATED RESOLUTION of the swapchain-side claim (it was STALE):** the plan claimed `Dx12SwapChain.cs:165`
"flushes the wrong fence" — but `Dx12Device.Flush()` was already hardened in commit `49623af8` (P0b step1)
to drain ALL THREE queue fences (render `fence` via `WaitForGpu`, pipelined `frameFence` via
`WaitFrameFence(frameFenceValue)`, AND `uploadFence`) before returning, so the drained-resize was already
correct on this branch. Proven empirically by the new harness (below) — no device removal across the full
stress sequence in default AND overlap paths. EF3 therefore HARDENED `Dx12SwapChain.Resize` (made the
drain comment accurate to the 3-fence reality + re-seed `currentIndex` post-resize so a stale index can
never index a disposed buffer) and added a permanent regression guard rather than a behavioural fix.
Root (historical, now fixed): pipelined/in-flight frames not drained before back-buffer release+
`ResizeBuffers`. (Tightly coupled to EF9 fullscreen — see coupling note; but the editor "fullscreen" is an
ImGui maximized panel, NOT a DXGI mode change, so it does not resize the swapchain.)
**Harness:** `Docs/Validation/dx12-resize-harness/` (hidden HWND → real Dx12Device+Dx12SwapChain → Resize
over 0×0/shrink/grow/4K→1080p/same-size with a real in-flight frame before each). Re-run after any
swapchain/`Flush`/fence change. SkyTest golden SHA byte-identical (`bal render` never uses the swapchain).
Fix (resize sequence, in order):
1. Drain ALL in-flight frames on `frameFence` (full `WaitForGpu` across `FramesInFlight`), NOT the
   legacy upload `fence`. Resize must be a hard barrier — no frame may be in flight.
2. Release every back-buffer reference (RTVs + back-buffer resources) so nothing is held at `ResizeBuffers`.
3. `ResizeBuffers` (keep the existing 0×0 clamp + same-size early-out).
4. **POST-resize reset (review catch — must not skip):** reacquire back buffers + recreate RTVs, reset
   `currentBackBufferIndex` from the new swapchain, and reset/re-seed the pipelined frame state
   (`frameFence` per-slot values + any `FramesInFlight` ring indices) so the next frame starts from a
   clean fence state. A stale back-buffer index or frame-fence value after resize re-introduces the hang.
5. Route the editor's window OnResize AND fullscreen toggle through this single drained path
   (`Dx12BallisticEngineWindow.OnResize → swapChain.Resize`); no other call site may resize.

**Coupling with EF9b (explicit):** EF3's DoD includes "fullscreen toggle repeatedly", but the fullscreen
MECHANISM changes in EF9b. So: (a) land EF3 first and verify the pure window-resize case (drag-resize),
(b) after EF9b, RE-RUN EF3's fullscreen verification and confirm EF9b's new fullscreen path still goes
through the EF3 drained resize (it must not introduce a second, undrained resize). Add to EF9b's DoD:
"fullscreen enter/exit routes through the EF3 drained Resize — no undrained ResizeBuffers anywhere."

**Verification (review catch — the headless oracle does NOT exercise resize):** headless `bal render`
has no swapchain, so it only proves "offscreen render unchanged", NOT the fix. Real verification options,
preferred order: (1) author a tiny resize-harness exe that creates the swapchain and calls `Resize` in a
loop over a sequence of sizes (incl. 0×0/minimize, shrink, grow, same-size) WITHOUT the full editor — the
smallest surface that exercises the crash; (2) PIX/DRED capture on a single careful manual editor
drag-resize + fullscreen. Use (1) to gain confidence before any (2) editor launch. ⚠️ GPU-hang rule —
diagnose with DRED, do NOT relaunch-loop.
DoD: resize-harness runs the full size sequence with NO device removal; one careful manual editor run
drag-resizes + toggles fullscreen with no removal; headless `bal render` golden scenes stay byte-identical
(regression guard only, not the fix proof).

## EF4 — FPS/stats overlay shows in Scene view (edit mode)
Root: single global `showStats` gates both views (`EditorApplication.cs:1749/1974`); no view/play gate.
Fix: keep the Game-view `stats.Draw` call; gate the Scene-view call so the FPS readout does not appear
in the Scene view (Scene view may keep a minimal draw/tri counter if desired, but NOT the FPS number —
edit mode is on-demand/inconsistent). Decision pending user: (a) FPS Game-view-only regardless of play,
or (b) Game-view + `SceneManager.IsPlaying` only. **Default to (a)** unless told otherwise.
DoD: Scene view shows no FPS; Game view unchanged.

## EF5 — Theme overhaul → Unreal Engine 5 look (HEADLINE; sub-chunks)
NOT greenfield: `EditorTheme.cs`/`EditorDecoration.cs`/`ImGuiController` exist. The "raw/ugly" feel =
(a) palette+geometry not yet pushed to a deep UE5-dark identity, (b) panels bypassing the theme with
hardcoded colors. Target: deep dark graphite base, rounded panel headers, **one strong accent**,
AAA-tool feel — "doesn't look like default ImGui."
✅ **IDENTITY DECISION RESOLVED → (i) faithful UE5** (cool graphite + blue-grey shell + a single
restrained azure highlight, NO warm accent). The azure accent `0x3D8BD4` (EditorPrefs default) is KEPT;
the acceptance bar for the whole EF5 series is "looks like UE5". EF5a–d all build against this identity.
- **EF5a — Palette + geometry pass — ✅ DONE:** reworked `ImGuiController.ApplyGeometry` (rounding pulled
  into UE5's small 4-5px band) + `ApplyColors` (deeper/cooler bg0..titleBg ramp: bg0 `#16181C`, bg1
  `#1D2026`, bg2 `#262A31`, bg3 `#333842`, header `#2B3038`, titleBg `#121418`, menuBar `#101216`; `textDim`
  brightened `#848C99`→`#8C94A1` for ≥4.5:1 on input frames) + mirrored the `EditorTheme` Bg0..TitleBg ramp
  (overlay-chrome mirror) / OverlayBg / RowLabel-RowCaption to match. **Contrasts verified (WCAG):** body
  `#ECEEF2` = 12-16:1 on every surface; `textDim` ≥4.7:1 on inputs; azure accent 4.94:1 on bg0; RowLabel
  ~9-11:1, RowCaption ~4.7-5.8:1 — none muddy. Byte-of-behaviour unchanged; pure style. Only
  `ImGuiController.cs`+`EditorTheme.cs` touched. Visual verify batched into the EF5a–d screenshot checkpoint.
- **EF5b — Centralize bypass offenders — ✅ DONE:** added a SEMANTIC tokens block to `EditorTheme.cs`
  (Error/Warning/Success, PrefabBlue/RowChild/IconMuted, PrimaryAction±, FolderTint/FolderTintDim, the
  LogLevel[] ramp, Hairline/TreeGuide, PopupBg/InputBg) and routed the hand-typed `SysVec4` color literals
  in `AssetBrowserPanel`/`ConsolePanel`/`HierarchyPanel` (+ secondary `StatsPanel`/`BuildPanel`/
  `VolumeProfileEditor`) through them. Deliberately harmonizes a handful of slightly-off literals into one
  coherent family (NOT byte-identical — pure visual, behaviour unchanged). The literals that REMAIN are
  justified per the DoD grep: alpha-only overlays (`(0,0,0,0)`, `(1,1,1,0.0x)` ghost hovers, watermark
  icons), alpha/scale DERIVATIONS of a token (`(tint/color/accent.X,…,α)`, `(color.X*0.6,…)`), the
  no-icon-font fallback folder glyph (degraded mode), and the `Style(ext)` file-type color TAXONOMY (a
  self-contained data table, annotated in-file as deliberate). Build 0-error, reflection oracle EXIT=0.
  Visual verify batched into the EF5a–d checkpoint. **EF5c/EF5d note:** the Inspector/ComponentPreviews/
  ProfilerPanel/AssetInspectors still hold semantic literals (e.g. InspectorPanel:711/811 prefab-blue,
  :1192 warn, :1500 amber-red) — those are the inspector cluster's, addressed by EF5c/EF5d; the EF5b
  tokens (`PrefabBlue`/`Warning`/`Error`) already exist for them to adopt.
- **EF5c — Panel chrome polish — ✅ DONE:** routed the stark stock section/divider widgets through the
  existing `EditorDecoration` primitives (no hand-rolled DrawList chrome — the lib already had them). Every
  inspector-cluster `ImGui.SeparatorText(...)` → `EditorDecoration.DrawSectionHeader(...)` (the shared
  `ImGuiComponentGui.Header` adapter covers ALL attribute `[Header]` sections at once; plus `InspectorPanel`
  Render-Features + the `[Header]` path, `ComponentPreviews` ×11, `AssetInspectors` ×2) — a Caption-font,
  RowCaption-colored label + palette-hairline trailing rule instead of ImGui's framed default box. The
  toolbar→content `ImGui.Separator()` in `ConsolePanel`/`HierarchyPanel`/`AssetBrowserPanel` (nav-bar) →
  `EditorDecoration.DrawDivider()` (quieter `BorderLight` hairline). `DrawSectionHeader` gained a built-in
  symmetric pad (`SectionPadY=4f`) + `PushFont(Caption)` so a title recedes as a quiet group rule; the
  immediately-preceding redundant `ImGui.Spacing()` were dropped (pad now owned in one place). Visual-only
  (deliberate harmonization, NOT byte-identical); behaviour unchanged. Out of scope (untouched): Stats/
  TagsLayers/Settings `SeparatorText`/`CollapsingHeader`, and the modal-dialog internal separators in
  `AssetBrowserPanel` (left stock — dialog internals, not panel chrome). Build 0-error, oracle EXIT=0.
  Visual verify batched into the EF5a–d checkpoint.
- **EF5d — Type/spacing tokens — ✅ DONE:** the type-scale (Display/Header/Body/Caption) is verified applied
  where warranted (entity-name title = Header, meta line = Caption, every section header = Caption via
  `EditorDecoration.DrawSectionHeader`); the LAST stock section dividers — `StatsPanel`'s 5
  `ImGui.SeparatorText(...)` (Timing/Rendering/GPU/Global Illumination/Scene) — were routed through
  `DrawSectionHeader` to match (the residual default-look "flat" spots). Also FINISHED the inspector-cluster
  semantic literals (formal owner per EF5b): `InspectorPanel` prefab dots×2 + prefab-bar text → `PrefabBlue`,
  multi-differ "—" → `Warning`, "Missing (ref)" → `Error`; `ComponentPreviews` animator current/active cyan×2
  → NEW `Info` token, light/prefab warnings×2 → `Warning`; `ProfilerPanel` over-budget zone → `Error`; the
  destructive "Delete N Assets" button → NEW `Destructive`/`DestructiveHovered` tokens. Justified literals
  left + annotated (accent alpha/scale derivations, alpha-only overlays/watermarks, dark-on-chip glyph, the
  neutral batch-Document icon tint, the prefab-bar navy SURFACE backing, the `AssetInspectors` material
  base-color parsed from .mat). Interactive `CollapsingHeader`s in `TagsLayersPanel`/`SettingsPanel` left
  (collapsible — converting would remove function). Touched only `EditorTheme.cs` + the 4 panel files (all
  mine). Visual-only (not byte-identical), behaviour unchanged; build 0-error, oracle EXIT=0. Visual verify
  batched into the EF5a–d checkpoint. **LAST EF5 sub-chunk — the whole theme series is now ready for review.**
DoD: human screenshots before/after each sub-chunk — clearly modern, not default-ImGui; no panel
bypasses the theme (grep for `new SysVec4(` color literals in Panels/ → only justified ones remain).

## EF6 — Delete the dead Shading-Mode dropdown
Root: `EditorDebugViews.Install()` no-op on DX12; dropdown wires props the DX12 renderer never reads
(`EditorApplication.cs:1315-1357`, `EditorDebugViews.cs`, `HDRenderer.cs` debug props).
Fix: remove the dropdown UI + its dead wiring (DebugViewMode/EditorExtraDebugMode/EditorGiIsolate hooks
in the editor). **Full removal CONFIRMED safe (validated):** grep of `BallisticEngine.DX12/` shows NO
reads of `DebugViewMode`/`Wireframe` — Wireframe/Normals/Depth are ALL dead on DX12, not just the buffer
modes, so nothing functional is lost. Delete the whole dropdown. Leave engine-side `DebugView` enum +
`EditorDebugComposite` scaffolding ONLY if still referenced elsewhere (the DX12 port TODO may reinstate
them); otherwise remove the dead scaffolding too. Don't break the GI-isolate path (separate, still used).
DoD: dropdown gone, editor builds 0-error, no dangling `EditorDebugComposite`/`EditorExtraDebugMode`
references, GI-isolate untouched.

## EF7 — Tag/Layer dropdowns: add "Add Tag.../Add Layer..." → open Tags & Layers panel
Root: dropdowns (`InspectorPanel.cs:666-696`) are closed lists; `TagsLayersPanel` exists but unlinked.
Fix: append a separator + "Add Tag..." / "Add Layer..." item at the bottom of each combo; selecting it
opens `TagsLayersPanel` (set its `Open=true`, focus it). New tags/layers persist via the existing
TagManager/LayerManager store and appear in the dropdown next frame.
DoD: from Inspector, the "Add..." entry opens the panel; a tag added there shows in the dropdown.

## EF8 — Split Layer Collision Matrix into its own panel
Root: `TagsLayersPanel.cs:35` draws `DrawCollisionMatrix` inline in the same window.
Fix: move the collision-matrix UI into a new dedicated panel/window (e.g. "Layer Collision Matrix",
registered like other windows via the window registry / Window menu). `TagsLayersPanel` keeps only
tag+layer definitions. Both read the same LayerManager store. (Sequenced after EF7 — same file.)
DoD: two distinct windows; matrix edits still persist; Tags & Layers panel no longer shows the matrix.

## EF9 — Windowing/docking fix on ImGui DockSpace ⚠️ (big; sub-chunks)
Decision (user): use ImGui's built-in docking. **REVISED after reading the code (review catch):** do NOT
delete `DockPanelHost` — it provides MULTI-INSTANCE panels (Unity/VS "Inspector / Inspector 2", each
factory-made state), SINGLETON protection (Scene/Game back the one renderer target), and the fullscreen
router (`OwnsLabel`/`DrawMaximizedInstance`). Deleting it regresses those. Instead: fix the broken docking
behaviour ON TOP of the existing host. The old memory note "KEEP DockPanelHost" was right for this reason.
Symptoms: fullscreen broken; panels can't be maximized; opening a panel then can't close it (only Reset
Layout). Validated causes: `PassthruCentralNode` + custom maximize state machine fighting docking +
`DrawMaximizedPanel`/`DrawMaximizedInstance` using `ImGui.Begin(label, flags)` with NO `p_open` ref
(`EditorApplication.cs:1548-1575`, `DockPanelHost.cs:138`) → the X is ignored while maximized.
- **EF9a — Honor close everywhere (the real "can't close" fix) — ✅ DONE:** threaded a `ref open` through
  BOTH maximized paths (`DrawMaximizedPanel` for registered core panels via `panels.IsShown` + new
  `EditorPanelRegistry.SetShown`; `DockPanelHost.DrawMaximizedInstance` now `Begin(label, ref open, flags)`
  and returns `bool closed`). On X-while-maximized: the panel's `Shown`/`Open` flag flips false (close
  STICKS, no redraw loop) AND `maximize.Clear()` runs the SAME frame so the docked layout returns
  immediately. The normal docked path already honored close (`DrawDockPanel`/`DrawCore`). DockPanelHost
  multi-instance + singleton semantics untouched. Verified: editor builds 0-error (bin-copy lock from a
  running editor ignored), reflection oracle EXIT=0 (18 suites green). Human screenshot of close-while-
  maximized batched into the EF9/windowing visual checkpoint.
- **EF9b — Maximize/fullscreen that doesn't fight docking — ✅ DONE:** the maximize STATE machine
  (`MaximizeController`) was already clean + single-sourced (built in A1b, hardened EF9a). The remaining
  EF9b defect was that the fullscreen WINDOWS reused the docked panels' ImGui identities — core panels via
  `Begin(name, …)` (bare `"Inspector"`/`"Scene"` labels) and duplicated tabs via `Begin(label, …)`
  (`###KindKey`). A single ImGui window can't be both docked (dock-tree member, geometry in the `.ini`) and
  a `NoDocking` fixed-position fullscreen window, so each maximize FORCE-UNDOCKED the panel and — lacking
  `NoSavedSettings` — wrote its fullscreen pos/size into the docked window's saved settings (the layout
  EF9c persists). Fix: both maximize paths now `Begin` a DEDICATED identity (`###maxpanel` /
  `###maxinstance`) carrying `NoSavedSettings`, exactly like the viewport path's pre-existing
  `##viewportmax`. The docked window's identity, dock-node membership, and saved geometry are now NEVER
  touched by maximize. The EF9a `ref open` close contract is preserved unchanged (close flips `Shown`/`Open`
  + `maximize.Clear()` same frame; restore-double-click keys off the maximize KEY `name`, not the window
  label). Multi-instance + singleton semantics untouched. Verified: editor csproj builds 0-error (to a
  scratch output dir, around a running-editor bin-copy lock), reflection oracle EXIT=0 (all suites green).
  **DoD addition (EF3 coupling) — MET BY CONSTRUCTION:** editor "fullscreen" is a pure ImGui maximized-panel
  layout change; it does NOT call `Resize`/`ResizeBuffers` (the only swapchain resize is
  `runtime.Window.OnResizeCallback` → `imgui.WindowResized` + `viewport.InvalidateTargetSizes`). EF9b's
  changes are ImGui-window-identity only (grep of the two changed files for `Resize`/`swapChain` finds only
  the unrelated ImGui `NoResize` window flag), so there is NO second/undrained `ResizeBuffers`. The EF3
  resize harness (`Docs/Validation/dx12-resize-harness/`) is unchanged and remains the swapchain-resize
  regression guard. Human screenshot of maximize+restore not regressing the docked layout is batched into
  the EF9/windowing visual checkpoint.
- **EF9c — Layout persist/restore + PassthruCentralNode review — ✅ DONE:** the dock layout `.ini`
  (`EditorLayout.Save/Load`) already persisted window geometry/dock node, but NOT whether a panel is open —
  a closed core panel re-opened on next launch because `EditorPanelRegistry.Descriptor.Shown` defaults true
  and `DrawCore` simply skips a closed panel's `Begin` (so ImGui never learns it was closed). EF9c closes
  that gap: (1) `EditorPanelRegistry.HiddenKeys()` enumerates the closed core panels + `ApplyHidden(set)`
  re-applies them (viewports excluded — one renderer target, never "closed"); (2) `EditorLayout.SavePanelState/
  LoadPanelState` persist that set to a `<projectStem>.v{LayoutVersion}.panels` sidecar next to the `.ini`
  (versioned together; `DeleteSaved` clears both so Reset Layout re-shows everything, matching the existing
  `panels.ResetVisibility()`); (3) wiring — `panels.ApplyHidden(EditorLayout.LoadPanelState())` right after
  `EditorLayout.Load()` on startup (before the first frame submits panels), and a per-frame, change-gated
  `SavePanelState` at the end of `BuildUI` (closing a panel only flips `Shown`, which does NOT dirty ImGui's
  dock settings, so the existing `WantSaveIniSettings` save could not catch it; gated on a cheap joined-string
  compare so there's no per-frame file I/O). The `EditorLayout.BuildDefault` `###KindKey`/bare-name dock
  contract is untouched. **PassthruCentralNode REMOVED** — `DockSpace(..., ImGuiDockNodeFlags.None)`: the
  central node is ALWAYS filled by the Scene/Game view windows so passthrough never engaged visibly (the host
  window already carries `NoBackground`), and the review flagged the flag as a maximize/modal-capture breaker;
  dropping it is byte-identical to the eye and removes the hazard. Verified: editor csproj builds 0-error (to
  a scratch output dir, around a running-editor bin-copy lock), reflection oracle EXIT=0 (all suites green).
  Human verification (closed panel stays closed across restart; Reset Layout re-shows all; no visual change
  from the PassthruCentralNode drop) batched into the EF9/windowing visual checkpoint.
- **EF9d — Window-menu sync:** Window menu checkmarks reflect each panel's open state (closed panel
  visibly closed + re-openable). `EditorWindowRegistry` wiring already correct (`:72-105`) — bind to it.
⚠️ Same GPU area as EF3 (fullscreen). EF3 lands first; EF9b re-verifies EF3's fullscreen path. GPU-hang rule.
DoD: open/close any panel — close STICKS even when maximized, no Reset Layout needed; maximize any panel
(incl. a duplicated "Inspector 2") and restore; multi-instance + singleton (Scene/Game not duplicable)
still work; fullscreen works and routes through EF3's drained resize; layout persists across restart;
Window menu reflects state.

## EF-LAYOUT — Inspector layout model (design FIRST, then EF11/EF16/EF10/EF15 implement it) — ✅ DONE
Review catch: EF11 (adaptive label column), EF16 (nesting indent), and EF10 (per-component search) all
touch the SAME draw flow (`DrawMemberList`/`BeginGrid :2445`/`DrawNestedSlot :1973`) and can contradict
each other (EF16 wants the value column at a fixed x independent of depth; EF11 wants an adaptive label
column). Resolved them as ONE layout model BEFORE implementing any of the three:
- A single column model: value-field left edge anchored at a fixed x (does NOT shift with nesting depth);
  depth indents the LABEL/foldout only; label column adaptive within `[min, fixed-x − gap]` with ellipsis
  + hover-tooltip for overflow.
- The per-component search bar (EF10a) sits above this grid and only filters which rows draw.
This is a small design note + a shared helper, not a separate deliverable; EF11/EF16/EF10 then each
implement their slice against it. Sequence: write the model → EF16 → EF11 → EF10. Existing short-label,
shallow components must stay byte-identical.

**✅ DONE — shared helper landed: `BallisticEngine.Editor/Panels/Inspector/InspectorLayout.cs`.** It is the
single home for the column model + its metrics + the shared label primitive that EF16/EF11/EF10 route
through, so the three rules live in one place and can't drift. **Deliberately NO call sites rewired this
chunk** (EF16 first, then EF11, then EF10 each opt their slice in) → the inspector draws EXACTLY as before
this commit (byte-identical for the existing short-label / shallow components — the hard constraint), and
the reflection oracle stayed 18/18 green because nothing in the live draw flow changed yet.
- **The contract (read the file header for the full version):**
  - Two columns: LABEL/foldout (left) + VALUE field (right). The value column's LEFT EDGE is anchored at a
    FIXED panel-level x (`InspectorLayout.ValueColumnLeft(panelAvailWidth, s)`) and does NOT move with depth.
  - DEPTH indents the LABEL/foldout ONLY, by a SMALL fixed step (`DepthIndent = 12px`), never the value
    column — the opposite of today, where a nested grid sits inside a `TreeNode`'s full `IndentSpacing`
    (~21px) and marches the WHOLE table (both columns) right per level. A nested grid recovers the same
    value-x by narrowing its own label column: `LabelColumnWidth(depth, panelValueLeft, s) = panelValueLeft
    − depth*DepthIndent`. **Caller threads `panelValueLeft` (computed once per component) down through the
    recursion** so every depth aligns to the same x.
  - The label column is ADAPTIVE within `[MinLabelWidth=96px*S, valueLeft − LabelValueGap=10px*S]`:
    `DrawLabelCell(label, depth, columnWidth, s, tooltip)` draws it with the per-depth indent, ELLIPSIZES
    (`Ellipsize`, O(log n) binary search) when the text exceeds its column, and shows a FULL-TEXT hover
    tooltip when clipped (a real `[Tooltip]` wins). EF11 = adaptive label + legible labels uses this; EF16
    = depth/value-x uses `ValueColumnLeft`/`LabelColumnWidth`/`DepthIndentTotal`.
  - EF10a's per-component search bar sits ABOVE the grid and only filters rows (not part of the column
    model). Its conditional-visibility threshold lives here too: `MemberSearchThreshold = 12` (tune in EF10a).
- **How EF16/EF11 will plug in (the implementer's note):** today the nested grids (`DrawNestedSlot :1952`,
  `DrawPolymorphicSlot :1896`) open their own `BeginGrid` table INSIDE a `TreeNodeEx` that applies ImGui's
  full IndentSpacing. The model's fix is: drop the TreeNode's full indent for the body's GRID (keep the
  foldout header), pass a `depth` + the panel-level `panelValueLeft` into the nested grid, and have the
  nested `BeginGrid` use a FIXED-width label column (`LabelColumnWidth(depth, …)`) instead of today's
  proportional `SizingStretchProp` 0.38/0.62 split. The top-level `BeginGrid` (`:2445`) is depth 0 — when
  EF16/EF11 switch it to the fixed-column model, the existing rows must stay visually equivalent (the
  `PreferredLabelWidth=132px` anchor was picked to match the old 0.38 split's label weight at a typical
  ~340px panel). `InspectorPanel.Row`/`RowWithTooltip` + the two `IInspectorGui.BeginRow` adapters
  (`ImGuiComponentGui`/`ImGuiVolumeGui`) are the label-drawing sites that route through `DrawLabelCell`.

## EF10 — Per-component (and component-list) conditional search bar
Root: no inspector member/component filter exists (`InspectorPanel.cs:51` only Add-Component search).
- **EF10a — Component-internal member search (PRIORITY) — ✅ DONE:** under a component's header, a search box
  that filters that component's exposed fields and hides `[FoldoutGroup]`/`[Header]` sections with no match.
  **Conditional visibility:** shown only when the component's visible-member count exceeds
  `InspectorLayout.MemberSearchThreshold` (=12) — don't clutter a small component. Implemented in
  `DrawMemberList`: precompute `visibleMembers` (post-`[ShowIf]/[HideIf]`) ONCE → drives both the threshold
  and the filter; the box draws above the grid via the NEW reusable `EditorWidgets.SearchField`; the query is
  per-component-INSTANCE in a `ConditionalWeakTable<object,StrBox>` (GC-safe, no eviction). Filter matches the
  DISPLAYED label (`MemberLabel` = `[LabelText] ?? Prettify(Name)`, mirroring `MemberProperty.Label`);
  precomputed `groupsWithMatch` / `headersWithMatch` (a header's SECTION = header→next header) drive the
  group/divider hiding. The draw-loop body was reordered so the `[Header]`/`[Space]` chrome is DECOUPLED from
  the member's own match (a matched field keeps its section title even when the header-bearing member's own
  label doesn't match), and the member's own `MemberVisible` gate is the LAST step. No query → byte-identical
  to pre-EF10a. Validated on `VehicleController` (>12 members, `[Header]` sections — "steer" leaves only the
  6 steer fields under Steering/Grip). Touched `InspectorPanel.cs` + `EditorWidgets.cs`. Build 0-error,
  oracle EXIT=0.
- **EF10b — Component-list search (secondary) — ✅ DONE:** a top-of-inspector box filtering which components
  show, for many-component objects; same conditional-visibility rule. Implemented in `DrawEntityInspector`:
  shown only when `behaviours.Length > InspectorLayout.ComponentSearchThreshold` (=6), via the EF10a
  `EditorWidgets.SearchField`; query in a single inspector-owned `componentListSearch` field (one component
  list per shown entity → no per-instance keying); the draw loop gained a `ComponentMatch(b)` gate filtering
  on the DISPLAYED title `Prettify(b.GetType().Name)` (the string `ComponentHeader` shows, OrdinalIgnoreCase).
  The per-type `typeIndex` counter increments BEFORE the match-skip so a hidden component never shifts a
  visible sibling's Nth-of-type index (prefab-override badge + multi-select keying). Transform is drawn
  outside `behaviours` → never filtered/counted. No query → byte-identical. Build 0-error, oracle EXIT=0.
- Factor a small reusable `EditorWidgets` search-field helper so later panels (Hierarchy/Assets/Add-
  Component) can reuse it (those panels are an optional later round, not required here).
DoD: on a heavy component (e.g. Vehicle Controller) typing "steer" leaves only steer fields + their
group; a small component shows no search box; behavior on unfiltered components byte-identical.

## EF11 — Inspector drawer-row readability (label clip + slider value overlay) — ✅ DONE
Implements the EF-LAYOUT model's label rule. Root: fixed 38% label column (`BeginGrid :2445`) clips long
labels; slider value text overlaps fill.
**✅ DONE.** Two halves: (1) **Adaptive label / no silent truncation** — `InspectorPanel.Row` +
`RowWithTooltip` route their label through a new `DrawRowLabel(label, tooltip)` shim →
`InspectorLayout.DrawLabelCell(...)`, which ellipsizes a label wider than its column and shows the full text
on hover (a real `[Tooltip]` wins). The `columnWidth` passed is `ImGui.GetContentRegionAvail().X` measured AT
the label cell (column 0) — the actual remaining label-column width, so it works for BOTH the top-level
proportional `BeginGrid` (≈38% label col) AND the fixed-width nested `BeginNestedGrid` with NO panel-level
value-x threaded down, and the **top-level `BeginGrid` (:2459) stays UNTOUCHED** (depth-0 labels that fit
return unchanged from `Ellipsize` → visually equivalent; only over-long labels now ellipsize+tooltip instead
of clip). Resolved the EF16↔EF11 **double-indent trap**: `DrawLabelCell` applies `DepthIndentTotal` itself,
so the EF16 manual `ImGui.Indent` in Row/RowWithTooltip was removed (and the now-dead `LabelDepthIndent`
helper deleted) — indent applied exactly once. Tightened `DrawLabelCell`'s ellipsize budget to
`columnWidth − indent − gap`. (2) **Slider value legibility** — new `EditorTheme.SliderGrabRest` (darkened
amber `0x8A6A30`, white value ~6.5:1) pushed as `ImGuiCol.SliderGrab` around the `##v` slider draw in both
inspector adapters (`ImGuiComponentGui`/`ImGuiVolumeGui`), scoped to the slider so the **global EF5 amber
accent is untouched**; the active/dragging grab stays bright. Touched 5 files (`InspectorPanel.cs`,
`InspectorLayout.cs`, `EditorTheme.cs`, `ImGuiComponentGui.cs`, `ImGuiVolumeGui.cs` — all mine). Build
0-error (Editor clean; the only build failure is the user's in-progress DX12 `AoResult`→`SsaoResult` rename
— not mine, not staged), oracle EXIT=0 (18 suites).
DoD: long labels readable (full via tooltip/ellipsis), slider values legible; short-label rows unchanged.
**Visual verify (human screenshot) batched into the Inspector-layout set — NOT relaunch-looped (GPU-hang rule).**

## EF12 — Rename Inspector → "Details"
Root: hardcoded `"Inspector"` (`EditorApplication.cs:179`, also `:156`).
Fix: change the registered title to "Details" (and any user-facing "Inspector" strings/menu). Window
menu + tab show "Details". (Trivial; bundle with EF7/EF11 since same file area.)
DoD: panel tab/title reads "Details" everywhere; Window menu entry updated.

## EF13 + EF14 — Hierarchy collapse/expand + collapsed-by-default (NOT trivial; do together)
Root: toolbar (`HierarchyPanel.cs:32-51`) has no collapse/expand-all; nodes use `:288
ImGuiTreeNodeFlags.DefaultOpen` → all expanded on load.
Review catch: this is more than "drop DefaultOpen". ImGui tree open-state is implicit/per-node in its
storage; to (a) collapse-all/expand-all on demand, (b) default to collapsed on FIRST load, and (c) NOT
re-collapse the user's manual expansions every frame, you need a small per-node open-state tracker the
hierarchy owns (keyed by entity id), driven into ImGui via `SetNextItemOpen` only on the frames where a
force (collapse-all / expand-all / first-load default) applies — otherwise let ImGui keep the user's state.
Fix: build that tracker once; EF13 = two toolbar buttons that set force-collapse/force-expand for the next
frame; EF14 = seed the tracker to collapsed on first scene load. After the forced frame, user expansions persist.
DoD: Collapse All folds the whole tree; Expand All unfolds it; fresh scene load → fully collapsed; a node
the user expands stays expanded across subsequent frames (no per-frame re-collapse).

## EF15 — Inspector collections: reorder/clear + fix polymorphic `List<IFoo>` serialize (RW8)
Root: per-element Remove already exists (`InspectorPanel.cs:1608`); MISSING = reorder/clear UI, AND the
serialize bug `SceneSerializer.cs:349 SerializeSequence` uses `SerializeValue` not `SerializeMemberValue`
→ `[SerializeReference] List<IFoo>`/`IFoo[]` elements lose their `$type` discriminator (don't round-trip).
**Deserialize side CONFIRMED READY (validated):** `SceneSerializer.cs:550,634,641` already detect a
`$type`-tagged recursed element (abstract element type + tag present, even when `member==null`). So the
fix is serialize-side: route sequence elements through the polymorphic-aware path (emit `$type` per
element when the element's declared/element type is `[SerializeReference]`-eligible). Non-polymorphic
lists must stay byte-identical (the existing `SerializeValue` path for primitives/structs/assets).
Fix: (1) add per-element reorder (up/down or drag handle) + clear/insert to the list drawer;
(2) make `SerializeSequence` emit per-element `$type` for polymorphic element types only.
**Oracle (review catch — reflection suite does NOT cover this):** add a concrete save→reload round-trip
test (extend `BallisticEngine.Tests.Reflection` or a scratch harness) over a `List<IFoo>` with ≥2
distinct concrete element types: serialize → deserialize → re-serialize, assert (a) all concrete types
survive, (b) YAML is byte-stable across the two serializations (deterministic key order + `$type`
placement), (c) a non-polymorphic `List<float>`/`List<Material>` is byte-identical to pre-change output.
DoD: the round-trip test passes (a/b/c); reorder/clear work in the editor.

## EF16 — Nested drawing indents too far right (never fits) — ✅ DONE
Implements the EF-LAYOUT model's depth/value-x rule (FIRST of the layout trio). Root: `DrawNestedSlot`/
`DrawPolymorphicSlot` opened their body grid inside a `TreeNodeEx` whose full ImGui `IndentSpacing` marched
BOTH columns right one step per level, AND the proportional 0.38/0.62 split re-shrank the value column each
level → compounding "never fits".
**✅ DONE.** Fix landed in `InspectorPanel.cs`: a new `DrawNestedBody(Action)` wraps each slot's body —
(1) `Unindent(IndentSpacing)` cancels the TreeNode's full per-level indent for the body grid (so it no longer
marches a big step right per level), (2) bumps a static `nestDepth` (restored in `finally`). The body grid is
a new `BeginNestedGrid(id)` that uses a FIXED-width label column (`InspectorLayout.LabelColumnWidth(depth,
valueLeft, s)`) instead of the proportional split, so the value column keeps a usable width at every depth;
the SMALL per-depth label indent (`InspectorLayout.DepthIndentTotal`) is applied to the LABEL only, inside
`Row`/`RowWithTooltip` (depth 0 → 0px → top-level + the ComponentPreviews/AssetInspectors shim rows, which
never nest, stay byte-identical). **Pragmatic deviation from the EF-LAYOUT contract's "single panel-level
value-x threaded down":** each nested foldout structurally renders INSIDE its parent's value cell (column 1),
so the child grids don't share the panel content-left — a panel-global value-x physically can't hold across
value-cell nesting. `BeginNestedGrid` recomputes the anchor from THAT grid's current available width each
time; `ValueColumnLeft` clamps the label column to ≤62% of the current width, so the value box can never
vanish however deep (the DoD). Added a supporting `EditorTheme.UiScale` token (effective DPI×UI scale,
published by `ImGuiController.LoadFont`) so the static layout helpers convert pre-DPI metrics to px. The
top-level proportional `BeginGrid` (`:2459`) is UNTOUCHED. Touched 3 files (`InspectorPanel.cs`,
`EditorTheme.cs`, `ImGuiController.cs` — all mine). Build 0-error, oracle EXIT=0 (18 suites).
DoD: a 4-deep nested structure still shows readable value boxes within the panel width; one extra level
doesn't push values off-screen. **Visual verify (human screenshot) batched into the Inspector-layout set —
NOT relaunch-looped (GPU-hang rule).**

---

## Suggested execution order (dependencies)
1. **EF3** (crash; unblocks safe resize for everything visual) — careful, GPU-hang rule, resize-harness first.
2. **EF9a → EF9b → EF9c → EF9d** (windowing fix on DockSpace; pairs with EF3; EF9b re-verifies EF3 fullscreen).
3. **EF5a–EF5d** (theme overhaul; the headline — AFTER windowing so panels are stable; BLOCKED on the
   EF5 identity decision below — do not start EF5a until resolved).
4. Inspector cluster (same file, batch into one editor-screenshot checkpoint):
   **EF12 (rename) → EF-LAYOUT (design) → EF16 → EF11 → EF10a/EF10b → EF15 → EF7 → EF8**.
5. Hierarchy: **EF13 + EF14** (one chunk, shared per-node open-state tracker).
6. Viewport overlay (one screenshot checkpoint): **EF1 → EF2 → EF4 → EF6**.

Theme (EF5) and windowing (EF9) are the user's emphasized asks — give them the most care and
human-screenshot verification. One commit per chunk (bisect discipline).

## Open decisions to confirm before implementing
- ~~**EF5 (BLOCKS EF5a): theme identity**~~ — **RESOLVED 2026-06-17 → (i) faithful UE5** (cool graphite +
  blue-grey shell + a single restrained azure highlight `0x3D8BD4`, NO warm accent). Acceptance bar for
  EF5a–d = "looks like UE5".
- EF4: FPS Game-view-only (a) vs Game-view-and-playing-only (b). Default (a).
- EF10: member-count threshold for showing the per-component search box (e.g. >12 fields) — tune in EF10a.
- (Resolved by validation, no longer open: EF6 full removal is safe; EF9 keeps DockPanelHost; EF15
  deserialize is ready — see Plan-review resolutions above.)
