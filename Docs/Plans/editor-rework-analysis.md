# Editor Rework — Analysis & Plan Foundation (2026-06-17)

**Worktree:** `e:/Unity Projects/Ballistic-Engine-editor`  **Branch:** `editor-rework-2026` (off `dx12-renderer` @ `c8380f2e`)
**Status:** ★ SPINE DONE (chunks 1–42) + MERGED into `dx12-renderer` (merge `b7f474f1`). NOW running §9 REMAINING-WORK PLAN on `dx12-renderer` directly (main worktree `e:/Unity Projects/Ballistic-Engine`, NOT the worktree above — that is pre-merge, do not touch). **RW1 (body migration) DONE through chunk 46 (43 RW1.1+RW6 / 44 RW1.2 / 45 RW1.3 / 46a+46b RW1.4):** ALL `Draw*Section` component-preview bodies + ALL asset-inspector bodies are out of `InspectorPanel.cs` and into their registered `IComponentPreview` (ComponentPreviews.cs) / `IAssetInspector` (AssetInspectors.cs) shims; curve/gradient sub-editors were already extracted to EditorWidgets/terminal-drawers in Phase B. InspectorPanel: 2914 → **2578 lines** (the ~800–1000 estimate was over-optimistic — the residual is CORE inspector machinery RW1 never targeted: entity chrome, transform, the DrawMemberList layout driver, asset slots, pickers, add-component, multi-asset). **NEXT = chunk 47 (RW2: type-scale + drawer-row redesign, Phase E core — editor LAUNCH, GPU-hang safety in force).** See §9.
**Method:** 5 parallel read-only探查 agents over the whole `BallisticEngine.Editor/` tree + key cross-checks (DockPanelHost, EditorDebugViews, ThumbnailCache), plus the strategic docs (`ai-native-engine-master-plan.md`, CLAUDE.md, DX12 endgame).

## LOCKED EXECUTION ORDER (read this first)

```
PHASE 0  (foundations — everything sits on these)
  P0.1  TypeCache (+ headless harness skeleton)        ← FIRST COMMIT
  P0.2  Property/type model  ──┐ (multi-target day-1, 2 artifacts: type-PLAN + tree-INSTANCE,
        + resolve-plan harness │  visited-set/max-depth, codec = YamlDotNet)
  P0.4  drawer determinism ────┘ (priority + stable tie, built INSIDE P0.2 resolution)
  P0.3  hot-reload ClearForReload contract (wraps ALL caches)            ← last in P0

PHASE A  (shell → self-registering windows, Rule 3)   ← can run PARALLEL to P0.2 (needs only P0.1+P0.3)
  A1 window registry · A1b maximize (native-first) · A2 frame-loop · A3 viewport · A4 input · A5 play/edit
  (A6 inversion = DEFERRED)

PHASE B  (inspector → drawer tree, Rule 1/1.5/2)      ← needs P0.2 frozen
  B0 drawer tree (replaces flat chain) · B1/B2 selection+preview+asset registry · B3 widgets · B4 converge

PHASE G  (serialization, Rule 2)                       ← needs P0.2; design WITH B
  TypeCache(done in P0) · G0 loud-drops · G1 entity-refs · G2 collections · G3 [SerializeReference] · G4 nested

PHASE F  (undo)   F3 harness FIRST → F1 command choke (≡ D1) → F2 asset-undo
PHASE D  (AI-ops) D1 command registry (≡ F1) · D2 MCP schema · D3 perception (rides C1)
PHASE E  (visual+layout)  rides A+B0 — type-scale, drawer-row redesign, in-viewport toolbar, theme, decoration
PHASE C  (DX12, GPU-isolated, PARALLEL time-boxed)  C1 thumbnail DRED · C2 debug-view compositor · C3 delete GL
```
Oracle = (a) remote scene round-trip equality · (b) undo coverage harness · (c) `Editor.BuildUI` ms baseline (capture at HEAD first). Incremental, move≠fix separate commits, GPU-hang safety, never relaunch-loop.

---

## 0. The one-sentence picture

The editor is **functionally rich and DX12-migrated in the rendering backend, but its *application shell* is a 2000-line god-object** (`EditorApplication.cs`) and its **inspector is split between an excellent attribute-driven pipeline and a 2772-line god-panel** (`InspectorPanel.cs`) that still hand-rolls ~40% of its content. The control surfaces (remote pipe / MCP) and the undo system are clean. Two concrete DX12-endgame blockers remain: **the thumbnail GPU-hang** and **the EditorDebugViews DX12 compositor port**.

---

## 0.5 ★★★ THE CENTRAL PRINCIPLE (USER DIRECTIVE 2026-06-17 — this governs the ENTIRE rework)

**"Koddan elle tanımlama ASLA olmamalı. Her şey kendini kaydetmeli (self-registration); hiçbir şey merkezde elle listelenmez."** Hardcoded, statically-pre-listed windows and hand-rolled per-type drawing are THE problem to eliminate. Three concrete rules, all the same idea (declarative discovery, never an imperative central list):

### Rule 1 — Drawing is ALWAYS by drawer discovery, NEVER a hand-written type-switch.
If something is drawn in the inspector, a `PropertyDrawer`/drawer for its type draws it — resolved from a registry by type, never an `if (x is Foo)` chain. **The `if (behaviour is Renderer/Volume/Terrain/...)` god-chain in InspectorPanel is the exact anti-pattern to delete.** A component with no custom drawer falls back to the default member-by-member pipeline; a custom component supplies a drawer that REGISTERS ITSELF (by type), it is never wired into a central switch. (Same for asset inspectors and component previews — all registry-resolved.)

### Rule 1.5 — Attributes COMPOSE via a drawer STACK/TREE (Odin model), NOT a fixed decorator list.
**User directive (2026-06-17): "attributelar birbirini bozmamalı — [SerializeField] + [ReadOnly] + [HideIf] aynı member'da. Bunu hard-coded yaparsak hayatımız kararır. Odin benzeri bir tree yolu lazım."** This is the architectural heart of R1.

**Current system is a FLAT decorator chain — it CANNOT compose, only stack fixed special-purpose hooks.** Verified in `Panels/Inspector/DrawerPipeline.cs`:
- `CreateDefault` hardcodes a fixed list `[ConditionalDecorator, ReadOnlyDecorator, HeaderSpaceDecorator]` (`:22-26`). A NEW cross-cutting attribute (`[InlineEditor]`, `[BoxGroup]`, `[Indent]`, a new conditional) = WRITE a new decorator class AND hand-add it to this fixed list. That IS the "hard-coded → hayatımız kararır."
- Decorators expose only a few FIXED hooks (`Visible()`/`BeforeRow()`/`Enabled()`, `:30-44`) run in sequence. **They cannot NEST/wrap each other** — there is no `CallNextDrawer()`. `[HideIf]` returning false and `[ReadOnly]`'s disable are coordinated by LIST ORDER, not composition → order-dependent fragility (CLAUDE.md admits the predecessor "used to drift").
- The terminal is a SINGLE type drawer (`registry.Resolve(ValueType)`, `:45`); if unresolved → `gui.Unsupported()` (`:50`). **Not recursive** — a nested `Pair` can't be wrapped/expanded. (This is WHY R2 fails today: the decorator model structurally can't recurse.)
- "One pipeline" is even half-true: `Conditions.cs:7-8` notes the component path calls conditions DIRECTLY (`InspectorPanel.DrawMemberList`), NOT through the decorator — so the component side is still partly hardcoded + can drift from the volume side.

**THE FIX — an Odin-style drawer stack (this is the CORRECT implementation of R1+R2, they unify here):**
- **Each attribute declares its own drawer** (`[ReadOnly]`→`ReadOnlyAttributeDrawer`, `[HideIf]`→`HideIfAttributeDrawer`, `[Range]`→`RangeAttributeDrawer`, …) — registered by the attribute type, NOT a fixed list.
- **All drawers applicable to a member form a STACK**, ordered by priority/`[PropertyOrder]`.
- **Each drawer wraps the next via `CallNextDrawer()`:** `HideIfDrawer` returns early (subtree never drawn) when hidden; `ReadOnlyDrawer` does `BeginDisabled()` / `CallNext()` / `EndDisabled()`; `RangeDrawer` turns the next into a slider; the terminal is the type drawer (int/float/`Pair`/…). Attributes can't break each other because each only WRAPS — order is priority-driven, not a brittle fixed sequence.
- **Recursive:** when the terminal drawer is a nested type (`Pair`, a `[SerializeReference]` instance, a `List<T>` element), it rebuilds the SAME stack for each child member → this IS the R2 recursive value pipeline. R1 (drawer-by-type) + R1.5 (drawers compose) + R2 (recursion) are ONE mechanism: a composable, recursive, attribute-driven drawer tree with ZERO hardcoded lists.
- **Adding an attribute = adding one `[AttributeDrawer]` that self-registers.** The pipeline is NEVER edited by hand. (Phase B builds this; it REPLACES the flat decorator chain, it doesn't extend it.)

### Rule 1.75 — A general `TypeCache` is the shared substrate ALL registries query (USER-FLAGGED).
**User directive (2026-06-17): polymorphic serialization is CRITICAL — `[SerializeReference] IFoo field` must show a DROPDOWN of concrete types implementing IFoo; pick one → instantiate → edit its fields (recursively). "typecache gibi bir şeye ihtiyacımız olabilir."** Correct, and it's bigger than one feature.

**What exists vs what's missing:**
- `ComponentRegistry` (`ToolKit/Reflection Utilities/Component Registry/ComponentRegistry.cs`) ALREADY scans every assembly at bootstrap for concrete types — but ONLY for 4 fixed base types (`Behaviour`/`SceneBehaviour`/`VolumeComponent`/`DataAsset`). You can't ask "all concrete types implementing arbitrary `IFoo`."
- `DrawerRegistry` (`Panels/Inspector/ITypeDrawer.cs:15`) resolves a drawer for a type (`CanDraw` linear scan, last-wins) — but has NO "list all concrete types deriving from T" capability.
- **The missing piece = a general `TypeCache`: given any interface/abstract T, return all concrete instantiable (public ctor) types deriving from it.** Neither registry provides it.

**DECIDED (user 2026-06-17):**
- **Lives in the ENGINE** (`ToolKit`/`Engine/Reflection`), alongside/under `ComponentRegistry` — it GENERALIZES ComponentRegistry's existing scan (ComponentRegistry can even consume it). The SERIALIZER (`Engine/Serialization`) needs it to resolve `[SerializeReference]` type tags HEADLESSLY (bal/runtime have no editor) — so it CANNOT live in the editor. Same lifecycle as ComponentRegistry: one scan at bootstrap, rebuilt on ALC hot-reload.
- **Polymorphic scope = concrete types + CLOSED generics only** (Unity/Odin parity). `[SerializeReference] IModifier` → dropdown of CritMod/PoisonMod/...; `[SerializeReference] IModifier<float>` → concrete types implementing that CLOSED type; an OPEN generic field (`IModifier<T>`, T unbound) is NOT a supported field type.

**TypeCache is the SUBSTRATE under the whole Central Principle** — every "give me all types matching X" query routes through it:
- **R3** window discovery → `TypeCache.GetMethodsWithAttribute<MenuItemAttribute>()`.
- **R1.5** attribute-drawer discovery → `TypeCache.GetTypesWithAttribute<...>()` / drawer-for-attribute.
- **R2 / [SerializeReference]** → `TypeCache.GetTypesDerivedFrom<IFoo>()` for the dropdown.
- **B1/B2** component/asset preview registries → derived-type queries.
Today each registry hand-rolls its own assembly scan (or is hardcoded). TypeCache UNIFIES them into one scan + a query API. **This is Phase G's G-prereq AND the backbone of A/B's self-registration** — build it early (it's small: ComponentRegistry's scan, generalized + cached by query). The `[SerializeReference]` dropdown then = TypeCache query + the recursive drawer tree (R1.5/R2) for the chosen type's fields. ALSO: the agent-introspection win — a queryable TypeCache means an agent can ask "what implements IFoo / what windows exist / what drawers are registered."

### Rule 1.9 — The inspector draws the SELECTION via a drawer too; there is ALWAYS a fallback drawer (USER-FLAGGED).
**User directive (2026-06-17): "materyali seçince inspector nasıl olacak? Sahneden obje seçince entity-drawer devreye girmeli. Ayrıca özel drawer'ı olmayan bir tür için 1-2 data veren DEFAULT bir fallback drawer olmalı."** This extends R1 from "member values" up to "the whole selected thing," and mandates a never-blank fallback.

**What the inspector shows is resolved by a drawer, by the SELECTION's type — never an `if (selected is X)` switch:**
- Select a scene Entity → an **EntityDrawer** (name/active/tag/layer + its component list, each component drawn by the drawer tree).
- Select a Material asset → a **MaterialDrawer** (the material inspector).
- Select any asset/scene-object → its registered drawer.
- **Select a type with NO custom drawer → a generic FALLBACK drawer** that reflects the object's public members through the SAME recursive drawer tree (R1.5/R2) and renders them — so a brand-new component/asset/struct is NEVER a blank or `(TypeName)` dead-end. It always shows SOMETHING editable (its serializable members) the moment it exists, zero wiring. This is Unity's default-inspector behavior, and it's the safety net that makes "zero hardcoded drawing" actually usable: you only write a custom drawer when the fallback isn't good enough.
- This is the SAME resolution as everything else: `SelectionDrawerRegistry.Resolve(selection.GetType())` → fall back to `ReflectionFallbackDrawer` when none registered. The component-preview (B1) / asset-inspector (B2) registries are just this rule applied to components and assets respectively — they UNIFY into one selection-drawer registry with a reflection fallback. (Today these are the hardcoded `is Renderer/Volume/...` and `is Material/Texture/...` chains — both deleted.)
- **Fallback richness:** even the fallback should be decent (E-aesthetics applies) — header with type name + icon, then the reflected members. A truly opaque type (no serializable members) shows a minimal "type + instance id" card, never nothing.

### Rule 2 — Serialization + drawing are ONE RECURSIVE, COMPOSABLE pipeline (Unity semantics).
A `[SerializeField]` member is serialized AND drawn by recursively applying the same rules to its type, down to primitives that have their own drawer. Concretely (user's example):
```csharp
struct Pair { public int x, y; }              // plain struct, no special marker needed
class Foo : Behaviour {
    [SerializeField] Pair offset;              // → offset auto-serializes (nested YAML)
                                                //   AND draws as a foldout whose x, y each
                                                //   render via the int drawer — RECURSIVE
}
```
The SAME single "serialize-a-value / draw-a-value" recursion covers ALL of: nested struct/class (`Pair`), `[SerializeReference]` polymorphic plain-C# classes (pick concrete type), collections (`List<T>`/array/dict), AND entity/component refs. They are not four features — they are one recursive value pipeline that bottoms out at type-registered drawers/serializers. **There is no type-switch anywhere in it.** (This unifies Phase B's drawer work with Phase G's serialization work — they are the same pipeline seen from two ends.)

### Rule 3 — Editor windows SELF-REGISTER via attributes; NOTHING is hardcoded in the shell.
A window marks itself; the editor discovers it by reflection and places it automatically. Unity's `[MenuItem]` model:
```csharp
[MenuItem("Tools/Curve Editor")]               // attribute on a static parameterless method
static void OpenMenu() => EditorWindow.Open<CurveEditorWindow>();
```
**DECIDED (user, 2026-06-17):**
- **Scope = EVERYTHING through the registry (pure).** Hierarchy, Inspector, AssetBrowser, Console, Scene view — EVERY window self-registers. `EditorApplication` knows NO window by name; it only draws the registry. There are no privileged "built-in" panels. (The default-layout builder names keys to place them, but the windows themselves are still self-registered — placement ≠ ownership.)
- **Placement attribute carries the MENU PATH ONLY** (`Tools/xxx`, `Window/xxx`). A window opens FLOATING; the user docks it where they want; ImGui's `.ini` remembers the layout. No default-dock-zone in the attribute (the simplest Unity `[MenuItem]` behavior). Reflection scans for the attribute at bootstrap, auto-populates the top menu bar by path, and `EditorWindow.Open<T>()` (or the menu invoke) spawns/focuses the instance through the existing `DockPanelHost` mechanism (which already does factories + unique ids + singleton + maximize routing — it becomes the registry's backing store).

**Why this is the spine, not a feature:** every other phase serves these three rules. The shell decomposition (Phase A) EXISTS to remove the hardcoded window list so Rule 3 can hold. The inspector decomposition (Phase B) EXISTS to remove the type-switch so Rule 1 holds. The serialization work (Phase G) EXISTS to make the value pipeline recursive so Rule 2 holds. The whole rework is "make the editor fully declarative / self-registering." This also directly serves the AI-native goal: a self-describing registry is introspectable by an agent (list windows, list drawers, list commands) far better than a hardcoded shell.

The rework is therefore **NOT a rewrite** — the good parts (drawer pipeline, DockPanelHost mechanism, remote/undo, ImGui DX12 backend, gizmo math core) are kept; the rework is **(a) dissolving the two god-objects into focused units, (b) finishing the DX12 endgame, (c) closing the AI-operability gaps** the project's own north-star asks for.

---

## 1. Codebase map (sizes that matter)

| File | Lines | Verdict |
|---|---|---|
| `Panels/InspectorPanel.cs` | **2772** | God-panel. ~40% hand-rolled per-component/per-asset sections. SPLIT. |
| `EditorApp/EditorApplication.cs` | **1997** | God-object. ~15 responsibilities, hardcoded frame loop + panel wiring. DECOMPOSE. |
| `Panels/AssetBrowserPanel.cs` | 1577 | Large but cohesive. Thumbnail-coupled (blocked on hang). |
| `Panels/HierarchyPanel.cs` | 839 | Cohesive (two trees + drag-drop). OK, minor split candidate. |
| `Remote/RemoteHandlers.cs` | 579 | Clean. String-switch dispatch (scaling smell, not a bug). |
| `Gizmo/TransformGizmo.cs` | 556 | Solid. Shares `GizmoMath` core. Keep. |
| `Panels/BuildPanel.cs` | 536 | Cohesive. Worker-thread + determinate progress. Keep. |
| `Windows/CurveEditorWindow.cs` | 524 | Cohesive standalone window. Keep. |
| `Windows/UnityImportWindow.cs` | 419 | Cohesive. Volatile-progress smell (benign). Keep. |
| `Panels/BEventEditor.cs` | 356 | Cohesive specialized editor. Keep. |
| `ImGuiBackend/ImGuiController.cs` | 337 | Solid DX12-ready. Keep. |
| `Panels/VolumeProfileEditor.cs` | 311 | Uses the drawer pipeline correctly — the MODEL to copy. Keep. |
| `Panels/Dx12EditorPreview.cs` | 304 | Full DX12 preview impl — GATED OFF (hang). Fix, don't rewrite. |
| `ImGuiBackend/ImGuiDx12Renderer.cs` | 220 | Solid. Keep. |
| `Panels/Inspector/**` (12 files) | ~647 total | **The clean pipeline. The crown jewel. Keep & extend.** |

83 `.cs` files total. The mass is concentrated in two files.

---

## 2. What is GOOD (keep & build on — do not touch the foundations)

1. **The inspector drawer pipeline** (`Panels/Inspector/**`): `IProperty` (MemberProperty + VolumeParamProperty) → `DrawerPipeline` (decorator chain: Conditional → ReadOnly → HeaderSpace) → `DrawerRegistry.Resolve(type)` → `ITypeDrawer` → `IInspectorGui` (ImGuiComponentGui + ImGuiVolumeGui adapters). **One drawer serves `float`, `[Range] float`, `FloatParameter`, `ClampedFloatParameter` identically.** Headlessly testable (no ImGui dep in core — "24/24 headless PASS"). This is the extension model the whole inspector should converge onto.
2. **`DockPanelHost`**: already a clean registry+factory (per-KIND factory, singleton flag, unique ImGui ids, maximize routing). The mechanism is good — it's just **under-used** (only "extra" instances go through it; primaries are hardcoded fields). The rework EXTENDS this, it doesn't replace it.
3. **Remote control surface** (`RemotePort` accept-loop + `RemoteCommandQueue` lock-and-drain + `RemoteHandlers`): clean three-layer design, every mutation pushes EditorUndo + marks dirty → **remote edits are byte-indistinguishable from human edits**. Survives hot-reload. This is the AI-native backbone — keep it, formalize it.
4. **Undo system** (`EditorUndo` whole-scene/scoped/callback + `InspectorUndo` deferred-commit): one undo entry per logical drag, scoped-entity restore avoids scene-wide bake re-fires. Solid.
5. **ImGui DX12 backend** (`ImGuiController` + `ImGuiDx12Renderer`): mature, no GL API, font/vtx/idx/swapchain all clean.
6. **Gizmo math core** (`GizmoMath`): single source of truth for world↔screen, Y-flip, ray-picking. Mutually-exclusive IsInteracting flags. Camera decoupled via `IViewProjectionProvider`.
7. **Profiler + Build integration**: ring-buffer profiler chains to Tracy; build is worker-threaded with determinate progress. Both cohesive.

**Invariant: none of these get rewritten. The rework routes new work THROUGH them.**

---

## 3. What is the PROBLEM (the rework's actual targets)

### 3.1 `EditorApplication.cs` — the application-shell god-object (1997 lines)
Owns ~15 responsibilities, the worst being **VERY TIGHT** coupled:
- Panel lifecycle + storage (6 hardcoded fields + DockPanelHost extras + floating windows) — **adding a panel touches ~7 non-contiguous sites** (field, ctor, `show*` bool, Window-menu item, BuildUI draw call, MaximizedPanelStillAvailable switch, default-layout).
- The per-frame loop & render order, hardcoded across `OnUpdate` + `OnRender` (102-line render method). Order is correctness-critical (UI build → scene render → present, so gizmo drags don't lag) but **implicit in method bodies**.
- Viewport rendering split Scene/Game (copy-paste resolution/aspect logic).
- Input routing scattered (global Ctrl+Z/S/R in one place; W/E/R gizmo + F/copy/paste in SceneTabContents; game-view pointer in GameTabContents) — **no central dispatcher; remap = hunt 2-3 sites**.
- Play/edit transition with no state machine (side-effects inline in toolbar-draw).
- Asset-import orchestration reactive across 4 sites.
- 40+ mutable fields; ctor ~216 lines; EditorState ↔ EditorApplication bidirectional dependency.

### 3.2 `InspectorPanel.cs` — the inspector god-panel (2772 lines)
The pipeline is clean, but InspectorPanel is the **integration god-object around it**:
- ~40% is **hardcoded per-component preview sections** (`if (behaviour is Renderer/Volume/Terrain/AudioSource/Animator/...) DrawXxxSection(...)` — 10+ instanceof chains).
- Asset-inspector dispatch is the same `if (selected is Material/Texture2D/Shader/...)` anti-pattern.
- Curve editor, gradient editor, BEvent slot, asset slot — all hand-rolled, **bypassing the pipeline** (they predate it).
- Entity chrome + transform rows + component headers/menus + undo glue all in the same file.

**The fix is the SAME pattern the project already loves:** a registry. `IComponentPreview` (Type → custom section) mirrors `ITypeDrawer`/`VolumeComponent` discovery. `IAssetInspector` (asset Type → editor) likewise. Pulls ~1000 lines out into focused, registry-discovered units.

### 3.3 DX12 endgame blockers (concrete, from the docs)
- **Thumbnail GPU-hang**: `ThumbnailCache.Get()` returns 0 on DX12 (gated). `Dx12EditorPreview` (304 lines, full impl) hangs the GPU (`DXGI_ERROR_DEVICE_HUNG`) under load — **NOT root-caused**. Suspects (from backend agent): `matSrvHeap` ring-allocator descriptor overwrite of live SRVs; synchronous readback stalling on in-flight GPU work. **Must be diagnosed via DRED, fixed in its own minimal commit, NEVER relaunch-looped** ([[gpu-hang-launch-safety]] — a TDR hard-crashed the PC before).
- **EditorDebugViews DX12 compositor**: `Install()` is a no-op on DX12; the AO/Lit-no-post/SSGI-isolate extra views need an HLSL fullscreen pass + `HDRenderer.EditorDebugComposite` re-wire. Dropdown UI is intact; the compositor is gone with GL.
- After both: **delete `OpenGL/` + OpenTK.Mathematics + OpenAL** (the irreversible last step — separate, deliberate).

### 3.4 ★ Undo/Redo — "opt-in, must-remember" coverage holes (USER-REPORTED first-class problem)
**User report (2026-06-17): undo is "aşırı aşırı kopuk ve problemli — bazı şeyler register oluyor bazıları olmuyor."** The first analysis pass mis-called this "clean/solid" — that judged the *core mechanics*, not the *coverage*. The user is right: the system is structurally OK but **architecturally opt-in**, which is exactly why edits silently fail to register.

**Root cause — undo is MUST-REMEMBER, not CAN'T-FORGET.** Two unequal worlds:
- `InspectorUndo.Track(label, changed)` IS can't-forget (ImGui activation-state deferred-commit: one entry per drag, none for aborted edits, auto-scoped). This is the GOOD model. But it ONLY covers inspector widgets wrapped in `Track`.
- **Everything else = manual `EditorUndo.Push()`** at the call site. **94 manual calls across 13 files** (verified count): TransformGizmo(2), ColliderHandles(1), WheelHandles(1), TerrainTool(1), HierarchyPanel(22), AssetBrowserPanel(3), BEventEditor(6), PrefabInstanceOps, RemoteHandlers(7), EditorApplication(6), CurveEditorWindow(2). **Every new mutation path must remember to call Push() itself — the ones that forget are silently un-undoable.** That IS the "bazıları register olmuyor."

**Concrete coverage holes already visible:**
- **Asset edits (material / mesh / terrain) have NO undo at all** — they `AssetDatabase`-save directly, bypassing EditorUndo entirely (confirmed in the inspector-pipeline analysis). Edit a `.mat`, Ctrl+Z does nothing.
- **Gizmo drags** each hand-roll their own Push at drag-start; a missing one = that handle isn't undoable (e.g. a wheel/terrain/collider handle that forgot).
- **Hierarchy's 22 scattered Pushes** — reparent/delete/rename/duplicate/create each push independently; one missing branch = that op doesn't undo.
- **No coverage check exists** — nothing tests whether an action registered undo, so a regression (someone deletes a Push) ships silently.

**Three inconsistent undo PATHS** (`Push` whole-scene YAML / `PushEntity` scoped / `PushCallback` for non-scene assets) and WHICH to use lives in the call site's head → neighbors disagree (one full-snapshots → selection thrash + IrradianceVolume re-bake, the other scopes). The `.volume` profile uses the callback path with its own before/after capture (fragile); curve editor likewise.

**The FIX (the can't-forget principle, project-consistent):** route ALL mutations through ONE choke point so undo is captured automatically, not remembered. Options to design in the plan:
- A **mutation/command layer**: every editor edit (human, gizmo, remote, asset) goes through a single `EditorActions`/command API that snapshots BEFORE applying + auto-picks scope (entity vs scene vs asset). Manual `Push()` at call sites disappears. This ALSO unifies with the **remote parity** goal — RemoteHandlers already pushes undo per mutation; if every mutation is a command, remote/human/agent edits share ONE undo path by construction.
- **Asset undo** brought into the same model (material/mesh/terrain edits become undoable commands, not direct saves).
- A **coverage harness** (mirrors the 24/24 inspector test): enumerate every mutating action, assert each leaves exactly one undo entry. Makes "did it register?" a test, not a hope.

This is a FIRST-CLASS rework workstream (its own phase below), NOT smaller debt. It interlocks with the command-registry idea in Phase D (one command layer serves undo AND remote parity AND AI-operability).

### 3.45 ★ Serialization / reference backend is VERY LIMITED (USER-FLAGGED first-class problem)
**User report (2026-06-17): "Unity'de bir SerializeField ile herhangi bir Object türü (GameObject/Component/asset) atanabiliyor; bende serialize backendi çok sınırlı. Ayrıca [SerializeReference] ile plain-C# class atama bende yok."** VERIFIED against the code — the user is right, and it's deeper than the first analysis assumed. This is an ENGINE data-layer gap, not just an editor-UI gap (lives in `Engine/Serialization/`, surfaced by the editor). Evidence below is file:line-backed.

**What WORKS today:** asset refs only. A `BObject`-derived ASSET (Material/Texture2D/Texture3D/Shader/VolumeProfile/Mesh) serializes as `guid:<hex>` (`SceneSerializer.cs:170`) and the inspector gives it a drag-drop + searchable picker slot (`InspectorPanel.cs:1383`, `DrawAssetSlot`). Plus primitives, enums, Vector2/3/4/Quaternion, AnimationCurve, ColorGradient, BEvent.

**What is MISSING (each a Unity-core capability):**
1. **Entity / Component references — COMPLETELY ABSENT.** `Entity` and `Behaviour` ARE `BObject`s, but they have no `AssetDatabase` GUID (they're runtime scene objects, not file assets). So `AssetDatabase.TryGetAssetGuid(entity)` fails → the serializer **silently drops the reference to null** (`SceneSerializer.cs:171`). The inspector shows a dead gray `(Entity)`/`(Behaviour)` label (`InspectorPanel.cs:1407`) — non-interactive. **You cannot wire "spawn next to this entity" / "damage the thing I collided with" by an inspector-assigned field.** This is Unity's single most-used inspector feature.
   - *The pattern already half-exists:* `BEventYaml` stores a target by `InstanceId` and resolves it at runtime — so an `EntityRef { Guid InstanceId }` + a scene-entity picker + an InstanceId resolver is a KNOWN, proven shape, just not generalized to fields.
2. **`[SerializeReference]` / polymorphic plain-C# members — COMPLETELY ABSENT.** No `$type` tag in the YAML members schema, no abstract/interface member handling in `DeserializeValue` (`SceneSerializer.cs:347-377`). An `public IConstraint c;` field can't round-trip a derived type or be assigned a concrete class from the inspector. (Grep confirms: zero `SerializeReference` anywhere in the repo.)
3. **Collections (`List<T>`, arrays, `Dictionary`) — COMPLETELY ABSENT.** No collection dispatch in the serializer; a `List<Material>` deserializes to **null** (round-trip lost) and shows gray text in the inspector. Blocks spawner pools, quest-step lists, ref arrays — fundamental.
4. **Nested struct/class members — DON'T ROUND-TRIP.** Pass-through dumps them to YAML but deserialize returns null.

**The silent-failure trap (why it FEELS broken):** an unsupported-type member is **dropped with NO error/warning** — it just shows gray in the inspector and vanishes on save/load. No exception, no log. This silent loss is exactly the "çok sınırlı / problemli" feeling.

**★ RESOLVED, not an open fork (2nd review): the traversal/property model is OURS (P0.2); we rent only the text codec → Candidate A (extend YamlDotNet). This is a PHASE-0 ENTRY CONDITION, not "G's first decision."** The user flagged "serializer her şeye hazır olmalı; dışarıdan alabiliriz" — evaluated, and the reframe settles it: because P0.2 designs the recursive value-traversal independent of the codec and plugs a flat text codec underneath, that structure IS Candidate A by construction. A bought serializer would bring its OWN traversal (separate from the drawer tree) = two pipelines = defeats R2; AND the polymorphism/collection logic a library would give lives INSIDE the traversal we must own. So building P0.2 correctly LOCKS Candidate A; leaving "buy" open would prevent P0.2 from committing to the single-traversal assumption. The only residual choice is which text codec is cleanest (YamlDotNet's converters vs a thinner emitter) — a small, late, reversible pick, NOT an architecture fork. **(The historical evaluation that led here is kept below for the record; the decision is made.)**

Before committing to extend the home-grown YAML serializer, EVALUATE adopting a mature library so it's "ready for everything" (polymorphism, refs, collections, nested, versioning) out of the box rather than hand-building each gap. Candidates + the hard constraints any choice must satisfy:
- **Constraints (non-negotiable, from this engine):** human-readable + diff-friendly + git-mergeable text (the project's whole data doctrine is "YAML-source + derived-index" — a binary serializer breaks scene-diff/merge); must round-trip `BObject` GUID asset refs + the NEW `EntityRef`/`[SerializeReference]` `$type` tags; must run HEADLESS (bal/runtime, no editor); must integrate with the existing `ComponentReflection` member rule + the attribute-driven inspector; must NOT regress existing `.scene`/`.volume`/`.asset`/`.mat` files (or provide a migration).
- **Candidate A — keep YamlDotNet, extend it** (current). Pros: zero migration, already wired, text/diff-friendly. Cons: WE build polymorphism/refs/collections (the §3.45 work). This is the default if buy doesn't clearly win.
- **Candidate B — a mature C# serializer with built-in polymorphism** (e.g. a JSON-based one with `$type` discriminators, or a YAML lib with richer type handling). Pros: polymorphism/refs/collections may come for free; less hand-rolled edge-case code. Cons: migration cost; must still bolt on engine-specific ref resolution (asset GUID / entity InstanceId — no off-the-shelf lib knows these); risk of a less diff-clean format.
- **Decision criterion:** buy ONLY if it removes MORE hand-rolled work than the migration + ref-integration costs, AND keeps the text/diff/headless/attribute constraints. The ref-resolution layer (asset GUID, entity InstanceId, `[SerializeReference]` registry via TypeCache) is OURS regardless — no library provides it — so "buy" never eliminates the ref work, only the polymorphism/collection plumbing.
- **★ REFRAME (external review, 2026-06-17 — this nearly settles the question): the decision is NOT "build vs buy a serializer," it's "the traversal/property model is OURS; we may rent only the text codec."** R2's premise — "serialize-a-value ≡ draw-a-value, ONE recursion" — REQUIRES a single property/traversal model (P0.2) that BOTH the serializer and the drawer tree walk. A bought serializer brings its OWN traversal, separate from the drawer tree's → you get TWO pipelines kept in sync via a schema, NOT "one pipeline." That defeats R2. AND the polymorphism/collection "plumbing" a library would give you lives INSIDE that traversal model — the part you can't outsource without losing R2. So: **the recursive value model is built in-house (P0.2); YamlDotNet stays as the flat text emit/parse codec UNDERNEATH it.** That is Candidate A, and it's the near-certain answer once framed this way. The remaining open part is small: whether YamlDotNet's converters are the cleanest codec or a thinner one is — NOT whether to own the traversal. **So G-decision shrinks to "confirm Candidate A + pick the text codec," resolved at G's start, not an open architecture fork.**

**The FIX (the building blocks already exist — this is additive subsystems, NOT a serializer rewrite):**
- **Scene-object refs**: an `EntityRef`/`ComponentRef` value type holding `InstanceId`; serializer writes/reads it (reuse the `BEventYaml` InstanceId resolver pattern); inspector gets an entity/component picker (list scene entities, drag a Hierarchy row onto the slot — the hierarchy drag infra already exists). Highest impact.
- **`[SerializeReference]` polymorphism**: add a `$type` tag (registry key, like components already use) to the member's YAML; a concrete-type registry for the member's abstract/interface type; an inspector "pick implementation" dropdown. Recurses into the same member pipeline.
- **Collections**: recursive serialize/deserialize of list/array items through the SAME value pipeline + a YamlDotNet converter; an inspector array drawer (add/remove rows — registers as one `ITypeDrawer`, fits the existing pipeline).
- **Make unsupported types LOUD, not silent**: at minimum, log/flag a member that serialized to null so the loss is visible (cheap, immediate win independent of the above).
- All of these slot into the EXISTING attribute-driven inspector via `ITypeDrawer`s + the existing reflection member discovery — they EXTEND the crown-jewel pipeline, they don't fight it.

This is a FIRST-CLASS workstream (its own phase below). It's an ENGINE change (`Engine/Serialization/`), so it lands in the editor worktree but touches engine code — keep it isolated + incremental like everything else.

### 3.5 Smaller debt (real, not blocking)
- RemoteHandlers string-switch dispatch + manual MCP param mapping (no schema validation at the boundary — malformed JSON can NRE the editor).
- `BuildProgress` non-atomic volatile triple (torn reads — cosmetic).
- Profiler drops worker-thread zones silently.
- No live runtime-introspection UI, no agent-facing G-buffer/asset-preview surface (north-star frontier work, blocked partly by the thumbnail hang).

---

## 4. Strategic alignment (what the rework MUST serve / MUST NOT break)

From `ai-native-engine-master-plan.md` + CLAUDE.md — these are **invariants**, not preferences:

**MUST serve:**
- **AI-operable parity**: every non-cosmetic human action has a remote handler; the editor is a *client* of the same API agents use. A feature that can't be pipe-driven is debt. (A panel-registry + a formalized command registry make this enforceable.)
- **Progressive disclosure**: simple by default, depth opt-in. The editor is a convenience layer, not Unity-parity-for-its-own-sake.

**MUST NOT break:**
- **Edit/Play split** + pre-play YAML snapshot restore + live-reload-during-play round-trip.
- **OnAttach/OnDetach fire in BOTH modes** (render registration lives there, never OnEnabled — else viewport goes black).
- **Undo = whole-scene YAML snapshots** pushed BEFORE the interaction (not per-member diffs).
- **Inspector = attributes + DrawerRegistry** — NEVER hand-roll a new type-switch. New UI = a `[Attribute]`, an `ITypeDrawer`, or an `IPropertyDecorator`.
- **`Input.Enabled` master gate**; **UI build → scene render → present** order.

**★ PERFORMANCE is a first-class CONSTRAINT (USER-FLAGGED 2026-06-17): "bu kadar şey yapacağız ama performansı killememeli — editör sırf GUI yüzünden yan yatmamalı."** Everything in this rework (reflection-driven recursive drawer tree, DrawList decoration, TypeCache queries, self-registration scans) must NOT make the editor's per-frame GUI cost balloon. ImGui is immediate-mode — the inspector redraws EVERY frame — so naive reflection/allocation per frame is a direct frame-time hit. Hard rules:
- **Resolve reflection ONCE, cache it.** Member lists, attribute lookups, drawer resolution per (Type), the drawer STACK for a member, TypeCache derived-type queries — all computed once and cached by Type/MemberInfo, NEVER per frame per row. (This is the project's standing rule [[pref-no-reflection-render-hotpath]] applied to the editor: zero `GetCustomAttribute`/`GetType().GetProperties()` in the per-frame draw path — bake it at first-encounter into a cached "compiled drawer plan" per type.)
- **The drawer tree compiles to a cached plan — but the cache BOUNDARY is subtle (external review, 2026-06-17).** "Cache by Type" is too simple and is exactly where the perf constraint cracks. Split explicitly:
  - **STATIC, cached by DECLARED type (resolve once, keep forever):** the ordered drawer stack for a member, member reflection (the `MemberInfo` set), per-member terminal-drawer choice for non-polymorphic types, decoration. This is the bulk — resolved at first encounter, never re-resolved.
  - **DYNAMIC, re-evaluated per-frame OR cached by RUNTIME instance/actual-type (NOT declared type):** `[HideIf]`/`[ShowIf]` conditions (live values, every frame — but cheap: a field read + compare, no reflection); the concrete type behind a `[SerializeReference] IFoo` (known only at runtime → resolve its plan lazily, keyed by ACTUAL type, and cache THAT); `List<T>` element count + per-element plans (instance-dependent). 
  - **The rule:** declared-type plan is static; anything whose shape depends on the live VALUE (conditions, polymorphic actual type, collection length) is resolved against the instance and cached by actual-type where possible, re-read where it must be live. Get this boundary wrong → either slow (re-resolve every frame) or WRONG (a stale declared-type plan draws the wrong concrete type for a polymorphic field). The plan MUST state, per concern, which side of the line it's on.
- **Zero per-frame allocation in the draw path.** No per-row `new`, no LINQ, no boxing in the hot loop — they trigger GC stutter at 60fps. Cache strings (prettified labels), reuse buffers.
- **The editor already renders ON-DEMAND** (`forceFrames`/viewport-dirty — only redraws when something changed), and ImGui culls off-screen/collapsed content — LEVERAGE both: a collapsed component / off-screen row should cost ~nothing (don't build its plan until expanded/visible).
- **DrawList decoration is cheap but not free** — a few `AddRectFilled`/`AddLine` per row is fine; a gradient/shadow per row at scale is not. Budget decoration; prefer style-driven over per-row DrawList where equivalent.
- **Measure:** the editor has a built-in profiler (`EditorProfilerBackend`, `Window>Profiler`) + the `Editor.BuildUI` zone already exists. Keep a GUI frame-time budget; if a rework step regresses `BuildUI` ms on a heavy scene (many entities/components), that's a regression to fix, not ship. This is the EDITOR's verification oracle for the perf constraint (complements the visual oracle).

**Process invariants (from the renderer plan's hard-won discipline):**
- **Incremental, isolated-worktree, milestone-committed — NEVER big-bang.** (Memory: "editor-rework = messy, don't build on it" — the OLD editor-rework branch was a big-bang and rotted. This new branch must stay disciplined.)
- **GPU-hang safety**: any GPU-touching step → DRED-diagnose, never relaunch-loop, commit-safe-first.
- **One intent per commit** (a structural MOVE and a behavior FIX are separate commits).

---

## 5. Proposed rework shape (the plan FOUNDATION — user refines on top)

Mirrors the renderer plan's posture: **two phases, incremental, each step independently verifiable, the good foundations untouched.**

> **★ EXTERNAL REVIEW INTEGRATED (2026-06-17).** A second-opinion analysis of this doc surfaced 6 real improvements, all integrated below. The biggest structural fix: **TypeCache + the shared property model are a Phase 0, not sub-items of G** (everything downstream consumes them). The dependency graph is: Phase 0 (TypeCache + property/type model) → {A window-registry, B drawer-tree, G serializer} → {E/maximize ride A·B, refs/collections/undo ride B·G}. C (DX12) is a GPU-isolated independent track. The other five fixes (serializer reframe, cache-boundary precision, undo F3-first, three cross-cutting traps, two over-claims) are folded into the relevant phases + a new §5.6 traps section.

### PHASE 0 — Shared foundations (BEFORE A/B/G — they all consume these) ★ NEW, from external review
The review's #1 fix: TypeCache and the property model were buried as "G-prereq" while ALSO being "the backbone of A and B" — a contradiction. They are not part of G; they come BEFORE everything. Build + freeze these contracts ONCE, or A/B/G each re-invent + drift them (the exact `Conditions.cs` disease this whole rework fights).

**★ PRECISE dependency edges (2nd review — "P0 → A" over-serializes; don't idle Phase A while P0.2 is built):** A1/A1b/A2-A5 touch NO value/property model — A1 needs only **P0.1** (`TypeCache.GetMethodsWithAttribute<MenuItem>`) + **P0.3** (window-registry hot-reload invalidation). P0.2 (property model) + P0.4 (drawer determinism) do NOT block A. Only **B and G** wait on P0.2. So the real edge is **P0.1 → A**, and **A can run in PARALLEL with P0.2** (the riskiest, longest P0 item). Schedule: land P0.1 + P0.3 → then A and P0.2 proceed concurrently → B/G start once P0.2 freezes. (P0.2 not being a serial gate in front of A is a real scheduling win.)

**P0 internal micro-order:** P0.1 TypeCache FIRST (P0.2 queries derived types from it) → P0.2 property model, written ALONGSIDE its headless "resolve-plan-for-type" harness (the harness IS the contract's definition + the oracle — concurrent, not after) → P0.4 is NOT a separate step, it's priority+stable-tie-break built INTO P0.2's resolution → P0.3 last, the reload-invalidation contract wrapping ALL caches (TypeCache, type-plans, window/command registries), extending the existing `ClearForReload` list. **First commit = P0.1 + harness skeleton** (smallest, least-risky, everything sits on it; satisfies move≠fix).
- **P0.1 `TypeCache`** (Rule 1.75) — engine-side "all concrete types deriving from T / methods carrying attribute A" query, generalizing `ComponentRegistry`'s scan. Concrete + closed-generic only. Consumed by A1 (window discovery), B0 (drawer/attribute-drawer discovery), G3 ([SerializeReference] dropdown), D1 (command discovery). **Must be hot-reload-aware** (see P0.3).
- **P0.2 The shared PROPERTY / TYPE MODEL** (the review's deepest catch) — for R2 ("serialize-a-value ≡ draw-a-value, ONE recursion") to be LITERALLY one pipeline, the serializer AND the drawer tree must both walk ONE property model (Unity's `SerializedProperty` / Odin's `InspectorProperty`). This single recursive value-traversal contract is the ground B0, G1-G4, and E3 all stand on. Design it ONCE here; both ends consume it. **This also pre-answers the serializer build-vs-buy (see §3.45 update): the traversal model is OURS; only the text codec is rentable.**
  - **★ DAY-1 REQUIREMENT: MULTI-TARGET is structural, NOT E-phase polish (2nd review, VERIFIED).** Both reference models are multi-target AT THE PROPERTY LEVEL: Unity's `SerializedObject` wraps N targets + `hasMultipleDifferentValues`; Odin's `ValueEntry` holds N weak targets. **And multi-target ALREADY WORKS today** — hand-woven inside InspectorPanel (`DrawMixedMarker` :61, `ApplyMember` broadcasts to N entities :1306-1310, `MultiTransforms` :452, the N-selected banner :93). A single-target P0.2 would (a) contradict the very models being copied and (b) be a REGRESSION of existing behavior. So P0.2's contract: **every property addresses N targets; mixed-value detection is first-class in the model** (not re-hand-rolled per drawer). The ad-hoc `ApplyMember`/`DrawMixedMarker` logic MOVES INTO the model and disappears from InspectorPanel.
  - **★ NAME THE TWO ARTIFACTS to forbid per-frame tree rebuild (2nd review — the concrete form of the §4 cache boundary).** Two distinct things, distinct lifetimes:
    1. **Compiled TYPE-PLAN** — static, cached by `Type`: the resolved drawer stack + member reflection + ordering + per-member terminal-drawer choice. Resolved once, kept forever.
    2. **Property-TREE INSTANCE** — dynamic, cached by OBJECT(s): holds the live N-target values; invalidated/rebuilt ONLY when a polymorphic concrete type or a collection count changes (NOT every frame). 
    Odin is exactly this (plan compiled once, tree instance lives + rebuilds only on type/count change). If P0.2 is designed as "rebuild the InspectorProperty tree every frame," the perf constraint breaks on day 1. State this two-artifact split in the P0.2 contract.
  - **Traversal carries a visited-set + max-depth from day 1** (for trap 3, §5.6 — retrofitting cycle-safety later is painful).
- **P0.3 Hot-reload contract for ALL reflection caches** (cross-cutting trap #1, VERIFIED real) — `ReloadGameScripts` already maintains a "ClearForReload" list (`NetworkReplicationRegistry`/`SceneReplicationRegistry`/`InputRegistry` carry scar-comments: "add to the list in ReloadGameScripts or the first hot-reload breaks"). EVERY new reflection cache (TypeCache, compiled drawer plans, window registry, command registry) MUST register into that same invalidate+rebuild list, and open windows must re-resolve their drawer plans after reload. Make this a ONE-LINE rule each new cache obeys — stale-cache-after-reload is the worst bug class to debug.
- **P0.4 Deterministic drawer resolution** (cross-cutting trap #2, VERIFIED real) — `DrawerRegistry` today is "last-registered-that-CanDraw wins" (`ITypeDrawer.cs:13`, no priority). With self-registration across assemblies, registration order = assembly-load order = NONDETERMINISTIC → which drawer wins varies by machine/build. Replace with an explicit `priority` + stable tie-break (priority, then type name). Applies to every self-registering registry (drawers, attribute-drawers, windows, commands).

### PHASE A — Application-shell decomposition (dissolve `EditorApplication.cs`) — IMPLEMENTS Rule 3
Lowest-risk-first, behavior-preserving moves (the renderer's "move ≠ fix" discipline). This phase EXISTS to satisfy the Central Principle's Rule 3 (self-registering windows, zero hardcoded list).
- **A1. Self-registering EditorWindow registry** (Rule 3) — every window (incl. Hierarchy/Inspector/AssetBrowser/Console/Scene — the "pure" scope the user chose) marks itself via a `[MenuItem("Tools/...")]`/`[EditorWindow]` attribute; reflection scan at bootstrap auto-populates the menu bar by path; `EditorWindow.Open<T>()` spawns/focuses through `DockPanelHost` (which becomes the registry's backing store — it ALREADY does factories+ids+singleton+maximize). **`EditorApplication` ends up knowing NO window by name.**
  - VERIFIED current state (the Rule-3 target): `EditorApplication.cs:611-617` AND `:679-685` hand-call `settings.Draw()/CurveEditorWindow.Draw()/UnityImportWindow.Draw()/...` BY NAME, TWICE (fullscreen + normal paths). `:666-674` hardcode each core panel with its own `showXxx` bool + `DrawDockPanel`. No menu attribute exists yet (`Engine/Attributes/EditorAttributes.cs` has inspector attrs like `[Button]`, not a `[MenuItem]`) — the attribute is NEW. `DockPanelHost` (`:124-134`) already proves the factory/registry mechanism, currently only for "extra" instances — A1 generalizes it to ALL.
  - NUANCE to resolve in the plan: the Scene/Game VIEWPORT windows are special (they back onto the one renderer target, marked `singleton` in DockPanelHost) and are coupled to the frame loop's viewport-render step. They still self-register, but their Record/draw is the viewport-compositing path, not a generic panel — keep that coupling explicit (A3 ViewportRenderer) while still routing their open/placement through the registry.
- **A1b. FIX the maximize/fullscreen system (USER-FLAGGED: "aşırı aşırı bozuk — açılınca kapanamayan, layout'u kökten bozan").** Double-click a window's tab → fullscreen; double-click / Esc → restore. The current system is broken BY ARCHITECTURE: maximize is a SHADOW MODE parallel to ImGui's docking, hand-synchronized in THREE places that drift out of sync:
  - **Root cause:** `maximizedPanel` (one string, `EditorApplication.cs:57`) makes `BuildUI` bypass the ENTIRE dockspace (`:590` `if (maximizedPanel is not null) { ...; return; }`) and draw only that one panel with `NoDocking|NoMove|NoResize` (`:1406`). So fullscreen draws a SECOND, parallel render path.
  - **The 3 sync points that rot:** (1) `DrawMaximizedPanel` re-routes panel CONTENTS via its OWN `if/else if (name == Entities/Inspector/...)` chain (`:1410-1414`) — a panel not in this chain hits the dead-end `"This panel can't be shown fullscreen"` (`:1417`, the code ADMITS it). (2) `MaximizedPanelStillAvailable` hand-maps each panel to its `showXxx` bool (`:1385-1392`) — a panel missing here can get STUCK maximized (state set, never "available", never cleared) = "açılınca kapanamayan". (3) the `showXxx` bools themselves. Forget one of the three for a new panel → broken fullscreen.
  - **Symptoms confirmed in the code's own scar-comments:** "tagsLayers was previously missing here, so Tags & Layers disappeared in fullscreen" (`:610`), "the user couldn't find a way out" (`:1288`), "maximized the Scene view by mistake" (`:1439`), "previously these hit the can't-be-shown-fullscreen dead-end" (`:1398`). FIVE redundant exit paths (Esc `:568`, floating button `:1289`, re-double-click `:1409`, context-menu Restore `:1362`, stale-drop guard `:574`) — that many exits = proof none is reliable.
  - **Geometric hit-test fragility** (`:1330-1341`): can't use `IsWindowHovered` (a docked tab strip belongs to the parent dock-node), so it hand-computes a mouse-Y band + clamps it off the toolbar — every new panel layout can re-break this.
  - **THE FIX = SINGLE-SOURCED by A1 (not "free" — external review caught the over-claim).** Once every window draws through ONE registry path (`kind.Draw(panel)` — DockPanelHost already has this at `:100`/`:140`), maximize becomes ONE piece of registry state ("which instance is maximized") and the SAME draw call renders it fullscreen — this REMOVES the second content-route, the `MaximizedPanelStillAvailable` hand-list, and the `showXxx` triple-sync. That's the structural win A1 delivers. **But it is NOT free:** the geometric tab hit-test fragility (the mouse-Y band, `:1330-1341`) and the choice of mechanism — ImGui's NATIVE dock-node maximize vs a custom single-path full-rect draw — are SEPARATE real work that still needs doing during A1b. Budget A1b as genuine work that A1 makes TRACTABLE, not as a freebie. (Decide native-vs-custom during A1; native dock-maximize is cleaner if it composes with the registry.)
- **A2. Frame-loop as an explicit ordered pass list** — extract `OnRender`'s 102 lines into named editor passes (ImportPump, RemotePump, BuildUI, ViewportRender, IdleThrottle) in a declared order. (Conceptually the same move the renderer just did to `DX12HDRenderer`.) Preserves the exact current order; makes it legible + injectable.
- **A3. ViewportRenderer extraction** — fold Scene/Game render into one `ViewportRenderer` (kill the copy-paste; one place to add a third view).
- **A4. Input dispatcher** — `IInputHandler` priority chain (global / scene-view / game-view) so hotkeys are declarative + remappable + conflict-checkable.
- **A5. Play/Edit mode controller** — explicit enter/exit hooks; side-effects (cursor, save guard, focus, selection clear) become mode-transition handlers, not inline toolbar code.
- (A6 optional, higher-risk) — invert EditorState↔EditorApplication via an `IEditorController` notification seam.

### PHASE B — Inspector decomposition (dissolve `InspectorPanel.cs`) — IMPLEMENTS Rule 1 + 1.5 + 2
- **B0. Replace the flat decorator chain with an Odin-style composable drawer STACK/TREE** (Rule 1.5 — the architectural core). Each attribute → a self-registering `[AttributeDrawer]`; applicable drawers form a priority-ordered stack; each wraps the next via `CallNextDrawer()`; the terminal type drawer recurses into nested members. **This REPLACES `DrawerPipeline`'s fixed `[Conditional,ReadOnly,HeaderSpace]` list, it does not extend it.** This is the foundation B1-B4 sit on — do it first. Unifies the component path + volume path onto ONE tree (kills the `Conditions.cs` direct-call drift). Keep the existing `ITypeDrawer`s as terminal drawers (they still work — they become the leaf of the stack).
- **B1. `IComponentPreview` registry** — move the 10+ hardcoded `is Renderer/Volume/Terrain/...` sections into per-type preview classes, self-registered by type (Rule 1). InspectorPanel drops to a thin component-list driver.
- **B2. `IAssetInspector` registry** — same for the `is Material/Texture/Shader/...` asset dispatch.
- **B3. Reusable `EditorWidgets`** — curve editor, gradient editor, audio/animator scrubbers become shared widgets (currently inline in InspectorPanel).
- **B4. (converge)** wrap BEvent/BObject/curve/gradient as terminal drawers in the stack so the tree covers them too (Rule 2). Unify volume-profile undo into the dirty-flag path.
- **Interlock with G:** B0's recursive terminal + G's recursive serializer are the SAME pipeline (draw-a-value / serialize-a-value) seen from two ends — design the recursion contract ONCE, both consume it.
- Result: InspectorPanel ~800 lines (entity header + transform + component-list driver), ~1000 lines moved to registry-discovered units; the drawer tree has ZERO hardcoded attribute list.

### PHASE C — DX12 endgame (unblock the frontier)
- **C1. Thumbnail hang** — DRED-diagnose `Dx12EditorPreview`, fix the descriptor/readback hazard, re-enable `ThumbnailCache.Get` on DX12. (Own minimal commit; GPU-hang safety protocol.)
- **C2. EditorDebugViews DX12 compositor** — HLSL fullscreen pass for AO/Lit/SSGI-isolate; re-wire `HDRenderer.EditorDebugComposite`.
- **C3. Delete GL** — remove `OpenGL/` + OpenTK.Mathematics + OpenAL once parity holds (grep-verifiable zero GL refs). Deliberate, irreversible, last.

### PHASE D — AI-operability hardening (north-star frontier)
- **D1. Formalize the command registry** — attribute-registered remote handlers (`[RemoteCommand("entity.create")]`) auto-generating help + (optionally) MCP schemas; replaces the string-switch + manual MapTool.
- **D2. MCP boundary schema validation** — reject malformed params with a clean error before the editor sees them.
- **D3. Agent-facing perception surfaces** — once C1 lands, expose asset previews + G-buffer + live runtime introspection to the pipe/MCP (the master-plan's frontier list).

### PHASE G — Serialization / reference backend (USER-FLAGGED first-class; see §3.45)
Close the Unity-core data-layer gaps. ENGINE change (`Engine/Serialization/`), additive subsystems, slots into the existing attribute-driven inspector via the drawer tree (B0).
- **G-prereq. `TypeCache`** (Rule 1.75) — general engine-side "all concrete types deriving from T / carrying attribute A" cache, generalizing ComponentRegistry's scan. Build EARLY — it's the substrate for G3's dropdown AND A/B self-registration. Concrete + closed-generic only.
- **G-decision. BUILD-vs-BUY the serializer FIRST** (§3.45 — user-flagged) — evaluate adopting a mature serializer ("ready for everything") vs extending YamlDotNet, against the text/diff/headless/ref/attribute constraints. The ref-resolution layer (asset GUID / entity InstanceId / [SerializeReference] via TypeCache) is OURS either way. This answer reshapes G1-G4.
- **G0. Make silent type-drops LOUD** — log/flag any member that serialized to null (cheap immediate win; surfaces existing data loss).
- **G1. Entity/Component references** — `EntityRef`/`ComponentRef` value type (InstanceId-backed, reuse the `BEventYaml` resolver pattern); inspector picker listing scene entities + Hierarchy-row drag onto slot. HIGHEST impact.
- **G2. Collections** — recursive list/array/dict serialize through the same value pipeline + a YamlDotNet converter; an array `ITypeDrawer` (add/remove rows) in the inspector.
- **G3. `[SerializeReference]` polymorphism** — `$type` tag in member YAML + concrete-type registry for abstract/interface members + inspector "pick implementation" dropdown. Recurses through the same pipeline.
- **G4. Nested struct/class members** — recurse the value pipeline + a foldout drawer.

### PHASE F — Undo/Redo unification (USER-FLAGGED first-class; see §3.4)
The "kopuk, bazıları register olmuyor" problem. Convert undo from MUST-REMEMBER (94 scattered manual `Push()`) to CAN'T-FORGET (one choke point).
**★ ORDER FIX (external review, 2026-06-17): F3 FIRST, then F1, then F2 — NOT F1→F2→F3.** F1 touches 94 manual Push() across 13 files; converting them in one go IS the big-bang this whole rework forbids. The ONLY way to stay incremental: build the coverage harness FIRST as the safety net, THEN migrate call-sites to the command layer ONE AT A TIME with the harness asserting nothing regressed. Without the harness, migration is "hope"; with it, "test."
- **F3 (FIRST). Coverage harness** — enumerate every mutating action, assert each produces exactly one undo entry (mirrors the 24/24 inspector test). Run RED against today's holes (it should FAIL on the asset-edit / forgotten-Push cases — that's the harness proving it works), then it's the green-gate for every F1 migration step.
- **F1 (SECOND, incremental). Mutation/command choke point** — every editor edit (human UI, gizmo, remote, asset) routes through one `EditorActions` API that snapshots BEFORE applying + auto-picks scope (entity/scene/asset). Migrate call-sites incrementally; the command layer COEXISTS with manual `Push()` during migration (NOT a flag-flip — gradual, harness-gated). Manual `Push()` disappears site-by-site.
- **F2 (THIRD). Asset edits become undoable** — material/mesh/terrain edits go through the command layer instead of direct `AssetDatabase` saves (close the biggest hole the harness flagged).
- **★ Interlock raised to SPINE-level: F1 ≡ D1.** F1's command layer IS Phase D's command registry — ONE command = undo + remote + agent-invokable, BY CONSTRUCTION. This is what enforces the "every human action has a remote handler" parity invariant AT THE CODE LEVEL (not by discipline). Design F1 and D1 as the SAME layer from the start. (External review flagged this as the doc's best idea — promoted accordingly.)

### PHASE E — Visual/UX overhaul (DECIDED: rework includes look, not just structure)
**UI framework decision (2026-06-17): STAY on Hexa.NET.ImGui** — DX12 backend is proven+committed; immediate-mode + reflection-driven inspector is load-bearing for the AI-native zero-wiring goal; "ugly" is ~90% a theme/layout problem, not a framework limit. Retained-mode (Avalonia/WPF/custom) was REJECTED (months of rework, throws away the proven backend + inspector pipeline + remote-driven UI). So the rework includes a deliberate visual pass — but ON TOP of ImGui.

**KEY DIAGNOSIS (two visual/layout audit agents, 2026-06-17): the theme effort IS real (Inter font, Lucide icons, graphite palette, careful rounding in `ImGuiController.cs:200-315`) — it reads flat because it's the LAST 20% missing: no type hierarchy, bare label+box drawer rows, sparse DrawList decoration, under-deployed accent. AND the information architecture is INVERTED — scene tools live in the top bar instead of the viewport.**

**E-AESTHETICS — make it not "düz kutu + düz label" (user-flagged):**
- **E1. Centralized `EditorTheme`** — one palette + rounding/spacing/padding source. TODAY every panel hand-calls `PushStyleColor` (~10 panels). Collapse into one theme + scoped overrides. (Depends on A/B; theme over a god-object re-scatters.)
- **E2. TYPE SCALE (the "font boyutu anlamsız" fix — HIGH IMPACT).** Root cause: ONE font size for everything (`ImGuiController.cs:101`, `size=16.5*scale`; Bold is same SIZE, only heavier weight). Load 3-4 semantic sizes (Display/Header/Body/Caption) → headers read as headers. This is the #1 flatness fix.
- **E3. Inspector drawer-row redesign (the #1 ugliness complaint).** Today a row = `TextDisabled(label) | stock widget`, transparent bg, no rhythm, no affordance (`ImGuiComponentGui.cs:38`, `InspectorPanel.RowWithTooltip:2623`). Fixes: enable `TableRowBgAlt` for scannability, per-type icon affordance before the label, DrawList left-accent bar on hover, richer per-type widgets (color SWATCH preview, slider with FILLED track, asset-slot thumbnail). Generalize the EXISTING `AxisVec3` axis-colored chips (`:1425`, proof the approach works) to all types. **All of this lands as drawers in the B0 drawer tree — decoration is per-drawer, not per-panel hacks.**
- **E4. Component cards with depth** — bodies get a faint card bg + subtle border + wider accent stripe (header chrome at `:1167` is good but the body is invisible/flat).
- **E5. `EditorDecoration` DrawList library** — a tidy set of primitives (row-hover accent, card bg, focus ring, color swatch, section divider with depth, badges/pills, gradient header) — the tasteful "az dekorasyon" the user wants; reused everywhere, never re-hand-rolled per panel.
- **E6. Accent everywhere it's earned** — accent is defined but only on a few buttons; deploy it in row hover, focus rings, section underlines, the mixed-value dash (replace hardcoded orange `:1352`).

**E-LAYOUT — fix the INVERTED information architecture (user: "scene-view şeyleri üst bar'da, icon yerleştirmeleri rezalet"):**
- **E7. Scene-view IN-VIEWPORT toolbar (the core IA fix).** Move scene-manipulation tools OUT of the cramped top bar INTO a Unity-style in-viewport overlay: gizmo mode (Move/Rotate/Scale, `:1068`), pivot/center (`:1087`), gizmo space World/Local (`:1241`), an INTERACTIVE snap toggle (today a passive Ctrl-only text readout `:1227`), and a right-side visibility menu (grid `:1253`, gizmos, GI-debug group). These belong where the user manipulates the scene, not divorced in the top bar.
- **E8. Game-view declutters** — the shading/debug-mode dropdown (`:1174`) is editor-only (AO/Lit/Luminance) and is wrongly shown in BOTH Scene AND Game bars; remove from Game view.
- **E9. Icon fixes** — the THREE probe/gizmo toggles all use the SAME 📍 pin icon (`:1250/:1263/:1272`) = indistinguishable; give each a distinct glyph. Iconify text-only toolbar controls (pivot/snap), add tooltips to cryptic icon-only ones, group the two GI-probe toggles together.
- **E10. Menu fixes** — "Rebuild Scripts" is in the FILE menu (`:711`) but it's an Assets/Scripts action; move it. Help menu is hover-text-only — make shortcuts discoverable.
- **E11. Target IA map** — a concrete "what goes where" spec (menu bar / top toolbar = app+play+undo only / scene-view overlay = all scene tools / per-panel toolbar / context menu). The layout contract for the rework. (Panel internal toolbars — Hierarchy/Assets/Console — were audited as ALREADY coherent; the problem is the top-bar/viewport split.)
- **Ordering:** E rides on A (window registry — viewport is a self-registered window whose overlay is its own draw) + B0 (drawer tree — E3's row aesthetics ARE drawers). The structural cleanup is what makes consistent theme + IA tractable. Do NOT theme-paint the god-objects.

**Ordering rationale:** A & B are pure CPU-side refactors (safe, parallelizable, no GPU risk) → do first / interleave. C is GPU-risky (hang protocol) → isolate. D rides on C1. A and B are independent of each other and of C — could even be separate sub-worktrees if parallelized.

---

## 5.5 Proposed enhancements (CLAUDE's ideas — user invited "detay eklemekte özgürsün"; opt-in, separate from user directives)

These are MY suggestions on top of the user's directives — pick what's worth it. Grouped by area. None contradict the Central Principle; most ride the same registries/pipeline.

**★ SCOPE-CREEP GUARD (external review, 2026-06-17): this list is large; only TWO items genuinely belong ON the spine** (nearly free once the registries exist + directly serve the AI-native north-star): the **command palette (Ctrl+P)** and the **introspection panel** (the editor describing itself — for humans AND as the agent-facing self-description). Treat these as spine extensions. **Everything else here is explicitly LOW-PRIORITY / opt-in — do NOT let it onto the critical path.** Ship them only after the structural spine (Phase 0 + A + B + G) lands, or they balloon the scope.

### Inspector (beyond the drawer-tree mechanics)
- **Search box in the inspector** — filter a component's members by name (huge components like a material or a big config become navigable). Trivial on top of the member loop.
- **Per-component "..." menu unification** — Reset / Copy / Paste Component / Paste Values / Remove / Move Up-Down / Copy as YAML — all components get the SAME context menu from the registry (today partly hand-rolled). "Copy as YAML" doubles as an AI-debug affordance.
- **Multi-edit clarity** — when multiple entities are selected, the mixed-value dash is there, but add a "N selected, editing all" banner + a per-field "apply to all" so multi-edit is obvious, not silent.
- **`[InlineEditor]`-style attribute** — drawing an asset ref expands the referenced asset's drawer inline (edit a Material straight from the entity that uses it) — pure drawer-tree composition, very Odin.
- **`[Button]` rows already exist** — extend with `[Button]` parameters (a button that takes a small arg form) and progress feedback for long ops (bake).
- **Component reordering by drag** — drag a component header to reorder; Unity-parity, helps when order matters (execution/render).
- **"Add Component" search-first popup** — type-to-filter with fuzzy match + recently-used at top (TypeCache already lists them).
- **Live value pulse** — during PLAY, a member whose value changed this frame briefly tints — makes runtime behavior visible in the inspector (serves the AI runtime-introspection frontier too).

### Hierarchy
- **Type/component filter** ("show only entities with a Light", "t:Rigidbody") mirroring the asset browser's `t:` search — find things in a big scene.
- **Icons per entity by dominant component** (camera/light/mesh/empty) for scannability — TypeCache + a small icon map.
- **Multi-select drag-reparent, isolate/solo, hide-in-viewport toggle per entity** (eye icon) — standard scene-editing QoL.
- **Breadcrumb / "frame in hierarchy" when selecting in viewport** — pick in scene → hierarchy scrolls to + highlights it (and vice-versa).

### Asset browser (the "dosya arama" logical gaps the user flagged)
The panel is already capable (folder tree + grid/list + `t:Type` search + thumbnails). The logical gaps:
- **Sort options** — no name/date/size/type sort is exposed; add a sort dropdown (default name).
- **Recent / Favorites / a "scene's used assets" view** — fast paths a file tree lacks; agents would query these too.
- **Reverse-dependency surfacing** — "what references this asset?" inline (the `bal assets refs` data already exists headless — surface it in the UI).
- **Search scope toggle** (current folder vs whole project) + persistent last-search.
- **Drag multiple assets, multi-select operations, in-place rename consistency** — verify these all work (rename-outside-strands-meta is a known footgun; keep rename in-panel).
- **Breadcrumb is present** — add forward/back nav history (browser-style) and a path-typing jump.
- **Thumbnails are gated off (DX12 hang, Phase C1)** — re-enabling is the single biggest visual upgrade here; until then the colored-box fallback should at least encode type+extension legibly.

### Console
- **Click a log line → jump to the `Assets/...cs:line`** source (the stack already carries it — make it a clickable link).
- **Collapse-duplicate counter, per-category counts in the toolbar, regex search, log-to-file** — standard console QoL.

### Viewport / scene-editing
- **Selection outline / highlight in the viewport** (if not already crisp) — the single most-felt "pro editor" cue.
- **Measure/ruler tool, snap-to-surface placement, drag-asset-from-browser-into-scene** (drop a model/material into the viewport at the hit point — uses the GpuSceneQuery raycast that already exists).
- **Camera bookmarks + "align view to selection" + a small navigation cube** (orientation gizmo exists; make it clickable to snap to axis views).
- **Stats overlay → expandable** (per-pass GPU ms already in `RenderStats`).

### Cross-cutting / AI-native (these ALSO serve the agent surface — high leverage)
- **A command palette (Ctrl+P)** — fuzzy-search EVERY registered action/window/menu (the registries from A1/D1 make this nearly free) — fastest way to reach anything; also the human mirror of the agent command registry.
- **An "Editor RPC / introspection" panel** — list all remote commands + windows + drawers (TypeCache-backed) — the editor describing itself, for humans AND as the agent-facing self-description (D-phase frontier).
- **Theme/density settings** — compact vs comfortable spacing, light/dark, accent picker (the accent picker exists; round it out).
- **Persisted per-project window layout presets** ("Layout: Default / Lighting / Scripting / Debug") — one-click workspace switching.

## 5.6 Cross-cutting traps no single phase owns (external review, 2026-06-17 — assign an owner)

The plan is strong per-phase but three issues cut ACROSS phases and would fall through the cracks. Each needs a named owner.
- **Trap 1 — Hot-reload / ALC cache invalidation** (VERIFIED real, see P0.3). Owner: **Phase 0**. Every reflection cache joins the `ReloadGameScripts` ClearForReload list; open windows re-resolve drawer plans post-reload. The existing scar-comments prove this bug class already bit the networking registries.
- **Trap 2 — Drawer-resolution determinism** (VERIFIED real, see P0.4). Owner: **Phase 0**. `DrawerRegistry` is last-registered-wins with no priority; self-registration order = assembly-load order = nondeterministic. Add explicit priority + stable tie-break to EVERY self-registering registry.
- **Trap 3 — `[SerializeReference]` shared-refs + cycles** (the review's catch the code can't yet show, because the feature doesn't exist). Owner: **Phase G (G3) + Phase 0 property model**. The recursive pipeline assumes a TREE. But `[SerializeReference]` allows the SAME instance referenced twice (shared ref) and cycles (A→B→A). Naive recursive SERIALIZE copies the shared instance (identity lost on load); naive recursive DRAW infinites on a cycle. **DECISION NEEDED:** tree-only + cycle-guard (Unity's practical behavior — a back-reference is null/duplicated, the drawer stops at a depth/visited guard), OR a real object graph with an id/ref-table. "Unity/Odin parity" → almost certainly **tree-with-cycle-guard**, but it MUST be stated in the P0.2 property-model contract (the traversal needs a visited-set + max-depth from day one, retrofitting it later is painful).

## 6. Open questions for the user (decide before the plan locks)

**ANSWERED (2026-06-17):**
- ✅ **Visual/UX redesign vs structural** → rework INCLUDES a full visual + layout overhaul (Phase E), not just architecture.
- ✅ **UI framework** → STAY on Hexa.NET.ImGui (theme/layout problem, not a framework limit).
- ✅ **Window-registry scope** → PURE (every window self-registers, no privileged built-ins).
- ✅ **TypeCache location** → engine-side (serializer needs it headless).
- ✅ **[SerializeReference] scope** → concrete + closed-generic only.

**✅ ALL LOCKED (user 2026-06-17: "review'a uy, kalanına sen karar ver"). The plan is now decided — execution order is set.**
1. ✅ **Scope/priority order = code-health leads: Phase 0 → A → B.** GPU-safe, parallelizable, makes C/D safer after. **C1 (thumbnail hang) = a time-boxed, isolated, PARALLEL DRED session** — doesn't block A/B, doesn't defer forever.
2. ✅ **Verification oracle = the triple (NOT screenshot-diff, which is smoke-only):** (a) remote-driven scene serialize round-trip equality, (b) the undo coverage harness (F3), (c) an `Editor.BuildUI` ms baseline captured NOW at HEAD on a fixed N-entity/M-component scene + committed. Drawer tree stays headless-testable (resolve-plan-for-type, no ImGui).
3. ✅ **Strictly incremental = YES.** Each phase = committed milestones; move≠fix = separate commits.
4. ✅ **Thumbnail hang = one time-boxed DRED session, early + parallel; defer C behind A/B if no root-cause in the box.** Never relaunch-loop.
5. ✅ **Serializer = Candidate A (extend YamlDotNet); traversal/property model is OURS (P0.2).**

**REMAINING decisions, DECIDED BY CLAUDE (user delegated "kalanına sen karar ver"):**
6. ✅ **Trap 3 ([SerializeReference] cycles/shared-refs) = TREE-ONLY + CYCLE-GUARD.** Unity's practical behavior, consistent with the "Unity/Odin parity" goal. A real object-graph (id/ref-table) would heavily complicate BOTH serializer and drawer AND hurt YAML diff-cleanliness — rejected. P0.2's traversal carries a visited-set + max-depth from day 1; a back-reference/cycle stops at the guard (drawn as a collapsed "→ already shown" node, serialized as null/duplicate per Unity).
7. ✅ **Codec = keep YamlDotNet (extend it), do NOT write a thinner emitter.** Already wired; every existing `.scene/.volume/.asset/.mat` uses it; zero migration. Since P0.2 owns the traversal, the codec is just flat text emit/parse — no reason to take on emitter risk.
8. ✅ **Maximize (A1b) = try ImGui NATIVE dock-node maximize FIRST; fall back to the custom single-registry-path full-rect draw if native doesn't compose with the registry.** Native is cleaner + less code; a ~30-min spike during A1 settles it.
9. ✅ **A6 (EditorState↔EditorApplication inversion) = DEFERRED, off the critical path.** High-risk, and A1-A5 already dissolve EditorApplication — A6 doesn't deliver the core god-object win. Revisit as a standalone step AFTER Phase A only if still needed; not in initial scope.

---

## 8. EXECUTION — chat-by-chat (one CHUNK per chat; end every chat with a HANDOFF prompt)

**The plan is built piece by piece across SEPARATE chats, ONE CHUNK per chat** (same model as the renderer pass-graph plan's chunks). Each chat resumes from the previous chat's handoff prompt. **Every chat MUST end by emitting the next-chat handoff prompt** (template below) — no exceptions, even if the chunk only half-finished. The handoff is how the next chat knows the exact resume point + the worktree + the gotchas without re-deriving them.

**Chunk list (one chat each; do in the LOCKED ORDER above):**
- **Chunk 0** — P0.1 TypeCache + headless harness skeleton  ← FIRST CHAT
- **Chunk 1** — P0.2 property/type model (multi-target, 2-artifact type-PLAN+tree-INSTANCE, visited-set/max-depth, codec=YamlDotNet) + resolve-plan harness
- **Chunk 2** — P0.4 drawer determinism (folded into P0.2 resolution: priority + stable tie) [may merge into Chunk 1 if small]
- **Chunk 3** — P0.3 hot-reload ClearForReload contract (wraps ALL caches)
- **Chunk 4+** — Phase A (A1 window registry → A1b maximize → A2…A5) — can START once Chunk 0+3 land, PARALLEL to Chunk 1
- then Phase B (B0 drawer tree → B1/B2 → B3 → B4), Phase G (G0→G4), Phase F (F3→F1→F2), Phase D, Phase E, Phase C (parallel, time-boxed). Each = ≥1 chunk; split a long one and the handoff carries where it stopped.
- A separate one-off chunk any time: capture the `Editor.BuildUI` ms baseline at HEAD on a fixed scene + commit (oracle prerequisite).

A chunk that runs long is fine — stop, commit what's clean, the handoff carries the partial state. Never cram two chunks into one chat to "finish"; the chat boundary IS the rollback boundary.

### Handoff prompt template (emit at the END of every chat, in a copy-paste block)

```
Editor rework — continue from chunk <N>.
WORKTREE: e:\Unity Projects\Ballistic-Engine-editor   (branch editor-rework-2026, off dx12-renderer @ c8380f2e)
  ⚠ This is a SEPARATE worktree from the main repo (e:\Unity Projects\Ballistic-Engine). Do ALL editor-rework work here.
  ⚠ The main worktree has ACTIVE renderer pass-graph work — do NOT touch it. Do NOT use the rotted old `editor-rework` branch.
PLAN: Docs/Plans/editor-rework-analysis.md  (in THIS worktree — read it first; also recall memory editor-rework-analysis-2026-06-17)

DONE so far: <chunks completed, with commit hashes>
LAST COMMIT: <hash + subject>
THIS CHAT should do: <chunk N's exact scope, 1-3 lines from the chunk list / the relevant P0.x or phase item>

State to know before touching code:
- <any half-done / uncommitted state / wrinkle discovered last chat>
- LOCKED decisions in force: Candidate A serializer (extend YamlDotNet), [SerializeReference]=tree-only+cycle-guard,
  property model is multi-target + 2-artifact (type-PLAN cached by Type / tree-INSTANCE cached by object), self-registering
  registries need priority+stable-tie, every new reflection cache joins ReloadGameScripts ClearForReload, perf = zero
  per-frame reflection/alloc (compiled plan), maximize = native-dock-first.
- Gotchas: editor builds via `dotnet build BallisticEngine.slnx`; engine changes need the ROOT csproj rebuilt not just the exe;
  GPU-touching steps (only Phase C) = DRED-diagnose, never relaunch-loop; move≠fix = separate commits.

VERIFY before committing: <the oracle for this chunk — headless resolve-plan test / remote round-trip / BuildUI ms / build-clean>
END this chat by emitting the next handoff prompt for chunk <N+1>.
```

**Handoff rules:**
- Fill EVERY `<…>` with concrete values discovered THIS chat — no placeholders left. Commit hashes mandatory (next chat verifies it resumes from the right tree).
- If the chunk did NOT finish, "THIS CHAT should do" = the *remainder*; "DONE so far" = exactly what landed (committed) vs uncommitted.
- Any new fact discovered (a real API shape, a surprise coupling, a decision that had to be made) goes into BOTH the handoff AND the memory file `editor-rework-analysis-2026-06-17.md`.
- The plan file is the source of truth for the *method*; the handoff carries *position + freshly-learned state*, never re-explains the plan.
- **ALWAYS restate the worktree warning** (separate worktree, don't touch main/renderer, don't use old branch) — it's the easiest thing to forget across chats.

## 7. What was NOT changed

No code touched. New worktree `editor-rework-2026` created off `dx12-renderer @ c8380f2e`; the active renderer work in the main worktree is untouched. This doc is the only artifact.

---

## 9. REMAINING-WORK PLAN (post-merge — the "last 20%")  ★ ACTIVE

**Where we are:** the SPINE (chunks 1–42) landed and was MERGED into `dx12-renderer` (merge `b7f474f1`). That dissolved the god-object SHELL (Phase A window registry), unified the inspector DISPATCH (Phase B0/B1/B2: DrawerStack + ComponentPreviewRegistry + AssetInspectorRegistry — the `if (x is Foo)` chains are GONE), and built serialization/undo/AI-ops/registry substrate (G/F/D). But "spine done" hid two real gaps the SPINE explicitly punted:

1. **Phase B left the bodies behind (the "later chunk" contract).** B moved *dispatch* to registries; the section *bodies* (`Draw*Section`, the curve/gradient/material editors) physically still live INSIDE `InspectorPanel.cs` (~3300 lines). Behaviour is byte-identical; this is pure structural debt — the panel is still a god-panel, just no longer a type-switch.
2. **Phase E (visual + layout) was NEVER built.** `EditorTheme.cs` / `EditorDecoration.cs` do not exist; this is the real source of "çiğ/standart görünüyor" (clean arch, untouched visual layer).
3. C1 thumbnail still GPU-gated (DXGI_DEVICE_HUNG); C2 debug-compositor no-op. (GPU-isolated, time-boxed, DRED-first, never relaunch-loop.)

**Now run on `dx12-renderer` DIRECTLY** (main worktree `e:/Unity Projects/Ballistic-Engine`). The `editor-rework-2026` worktree/branch is PRE-merge — do NOT touch it. One chunk per chat; commit per chunk; `git add -A` FORBIDDEN (working tree is dirty — stage only your own files).

### LOCKED EXECUTION ORDER (RW)
```
RW1  body migration (Rule 1.5 debt) — move Draw*Section + sub-editor bodies OUT of InspectorPanel into
     their registered IComponentPreview / IAssetInspector shims; pure structural, byte-identical render.
       RW1.1  Renderer (DrawSubMeshMaterials+Row) + 2 simplest (Health, TrailRenderer)        ← chunk 43 ✅
       RW1.2  Animator / AnimatorController / LightAnimator / Spawner / ParticleSystem      ← chunk 44 ✅
       RW1.3  Volume / Terrain / AudioSource / UIDocument / TrailRenderer-residue
       RW1.4  asset-inspector bodies (material editor, texture import, curve/gradient sub-editors)
       (RW1 DONE = InspectorPanel ~800–1000 lines; THEN update §0 + memory index.)
RW2  type-scale + drawer-row redesign (Phase E core)                          ← chunk 47 ✅ (GPU launch OK, no TDR)
RW3  in-viewport toolbar + theme (EditorTheme.cs)                             ← GPU: editor LAUNCH, hang-safe
RW4  decoration polish (EditorDecoration.cs)                                  ← GPU: editor LAUNCH, hang-safe
RW5  C1 thumbnail DRED root-cause (BILINEN ÇÖKERTEN — single seat, commit-safe-first, gate+defer if box fills)
RW6  CLAUDE.md GL-drift doc cleanup (doc-only)                               ← chunk 43 ✅ (rode with RW1.1)
RW7  D2/D3 MCP schema completion + perception (rides C1)
```

### ORACLE (every RW1 chunk)
- (a) `dotnet run --project BallisticEngine.Tests.Reflection` → ALL suites GREEN (esp. `[ComponentPreview registry (B1)]`, `[AssetInspector registry (B2)]`, `[UndoCoverage (F3)]`).
- (b) `dotnet build BallisticEngine.Editor/BallisticEngine.Editor.csproj` → 0 error (the full `.slnx` may fail ONLY on a `BallisticEngine.Mcp.exe` file-lock if an MCP server is running — that is environmental, not a compile error).
- (c) RW1 = MOVE not FIX: render output byte-identical. Watch for **stray control bytes** baked into existing string literals (e.g. a U+009D `302 235` after the em-dash in `DrawSubMeshMaterials`' `"{label} — none"` and its `info —` comment) — preserve them byte-exact (splice via `python3` surrogateescape, do NOT retype) or the rendered text changes.
- (d) GPU-hang safety: RW1 is HEADLESS (no editor launch). RW2–RW5 launch — DRED-first, never relaunch-loop.

### Chunk 43 (RW1.1 + RW6) — DONE
Moved `DrawSubMeshMaterials`+`DrawSubMeshMaterialRow` → `RendererPreview` (now private statics inside it), `DrawHealthSection` → `HealthPreview`, `DrawTrailRendererSection` → `TrailRendererPreview`, all in `BallisticEngine.Editor/Panels/Inspector/Preview/ComponentPreviews.cs`. Enablers in `InspectorPanel.cs`: `Row(string)` widened `static`→`internal static` (relocated body calls it; `BeginGrid` was already internal static); added `internal void MarkViewportDirty()` so a moved body's `state.MarkViewportDirty()` becomes `ctx.Panel.MarkViewportDirty()` (private `state` reach). InspectorPanel dropped ~3357→~3300 lines. RW6: CLAUDE.md GL-drift banner (stack line, Renderer-pipeline section, embedded-shader gotcha) marked HISTORICAL/DX12. Build 0-err, harness ALL GREEN.

### Chunk 44 (RW1.2) — DONE
Moved the bodies of `DrawAnimatorSection` → `AnimatorPreview` (the 2 `animatorPreviewPlaying/Time` statics moved INTO the preview class; `EditorWidgets.AnimatorScrubber` reaches via parent-namespace nesting, `state.MarkViewportDirty` → `ctx.Panel.MarkViewportDirty`), `DrawAnimatorControllerSection` → `AnimatorControllerPreview`, `DrawLightAnimatorSection` → `LightAnimatorPreview` (the 2 `lightAnimPreview/Clock` statics moved in too), `DrawSpawnerSection` → `SpawnerPreview`, `DrawParticleSystemSection` → `ParticleSystemPreview` — all in `ComponentPreviews.cs`. No new InspectorPanel accessors needed beyond RW1.1's `MarkViewportDirty()` (no grid/Row use in these 5). Byte-exact check: the 3 em-dashes in the moved bodies (AnimatorController surface comment, click-jumps comment, LightAnimator `"Add one — …"` string) are clean U+2014 (`e2 80 94`) with NO trailing U+009D — preserved verbatim; the 2 pre-existing U+009D bytes in `ComponentPreviews.cs` are from RW1.1's `RendererPreview` (unchanged). InspectorPanel `3267→3096` lines. Build 0-err (Editor csproj alone), full reflection harness ALL GREEN (B1 16/16, B2 17/17, F3 22/22). HEADLESS — no editor launch.
**RW1.2 of `Docs/Plans/editor-rework-analysis.md` §9 chunk list marked DONE.** NEXT = chunk 45 (RW1.3: Volume / Terrain / AudioSource / UIDocument bodies). InspectorPanel still ~3096 lines → RW1 not done yet (target ~800–1000; §0 status + memory index update happens at RW1.4 end, not now).

### Chunk 45 (RW1.3) — DONE
Moved 4 section bodies out of `InspectorPanel.cs` into their registered shims in `Inspector/Preview/ComponentPreviews.cs`:
- `DrawTerrainBrushSection` → `TerrainPreview` (private static; self-contained — was already `internal static`; uses `TerrainTool`/`TerrainSculpt`).
- `DrawAudioSourceSection` → `AudioSourcePreview`. **COUPLING FOUND:** the `audioPreviewVoice`/`audioPreviewTime` statics are ALSO used by `DrawAudioClipAsset` (L~2400, the `.wav` asset-clip preview — an RW1.4 asset-inspector body still inline in InspectorPanel). So they could NOT move into the preview class; instead made them `internal static` on InspectorPanel and reached as `InspectorPanel.audioPreviewVoice` / `ref InspectorPanel.audioPreviewTime` (ref-to-other-class-static is legal). `state.MarkViewportDirty` → `ctx.Panel.MarkViewportDirty`.
- `DrawVolumeProfileSection` → `VolumePreview` (FULL move: the 2 `volumeUndoBefore`/`volumeUndoLastClean` statics + the private static `CreateProfileAsset` helper all moved in — only used here). The undo closures capture `InspectorPanel panel = ctx.Panel;` so `state.MarkViewportDirty()` → `panel.MarkViewportDirty()`. Uses `VolumeProfileEditor`/`EditorCommands.EditAsset`/`AsyncAssetImport`/`VolumeProfileLoader`.
- `DrawUIDocumentSection` → `UIDocumentPreview`; the `DrawPathDropField` helper came along as a `private static` taking an `InspectorPanel panel` param (for `panel.MarkViewportDirty()`). The shared `AcceptGuidDrop` (used by 8 sites incl. asset slots / curve editor — RW1.4 territory) STAYS on InspectorPanel; made `internal static` and called as `InspectorPanel.AcceptGuidDrop`.
- ComponentPreviews.cs added `using System.Linq;` (`.Any()` in DrawPathDropField) + `using BallisticEngine.AssetPipeline.Loaders;` (`VolumeProfileLoader`).
- Byte-exact: 2 em-dashes in MOVED rendered/comment text (Terrain comment, Audio `"... machine — preview is silent"`) are clean U+2014 (`e2 80 94`), NO trailing U+009D — verified. The 2 pre-existing U+009D bytes in ComponentPreviews stay 2 (RW1.1 RendererPreview, untouched). Cleaned up an orphaned RW1.1 Health comment that had detached above the UIDocument body.
- ORACLE: Editor csproj build 0-err; full reflection harness ALL GREEN (B1 16/16, B2 17/17, F3 22/22, + all others). Move-not-fix: `git diff --stat` symmetric (+220 ComponentPreviews / −205 InspectorPanel). InspectorPanel `3096→2914` lines. HEADLESS — no editor launch.
**RW1.3 marked DONE.** NEXT = chunk 46 (RW1.4: asset-inspector bodies — material editor / texture import / curve+gradient sub-editors → `AssetInspectors` registry, NOT ComponentPreviews). InspectorPanel still ~2914 lines → RW1 not done yet; §0 status + memory index update happens at RW1.4 end. NOTE for RW1.4: `audioPreviewVoice`/`audioPreviewTime` (now `internal static` on InspectorPanel) + `AcceptGuidDrop` (now `internal static`) are ready for the asset-inspector shims to consume; `DrawAudioClipAsset` is the audio asset-inspector body that shares those statics.

### Chunk 46 (RW1.4) — DONE  ★ RW1 (body migration) COMPLETE
Moved EVERY asset-inspector body out of `InspectorPanel.cs` into its `[AssetInspector(".ext")]` shim in `Inspector/AssetInspectors/AssetInspectors.cs` (namespace `BallisticEngine.Editor.Inspector.AssetInspectors`). Split into two commits (one intent each):
- **chunk46a (`514d13fe`)** — the BIG/riskiest one alone: `DrawMaterialEditor` + `DrawMaterialPreview` (+ its preview-cache fields `materialPreviewGuid/Hash/Tex/Dx12`, `MaterialPreviewSize`, `IsDx12`) + `ReferenceToPath` / `ApplyLiveMaterial` / `LoadSlot` → `MaterialAssetInspector`. The material-preview thumbnail cache moved from per-panel instance state to the registry's SINGLE shared inspector instance (same single-cache lifetime; preview only renders on GL, DX12 returns early, cache keyed by guid+hash so sharing is safe). Body reaches `InspectorPanel.BeginGrid/.Row/.AcceptGuidDrop` (internal static) + `panel.MarkViewportDirty()`. The DX12-disabled comment's em-dash preserved byte-exact (clean U+2014, no U+009D).
- **chunk46b** — the rest: `DrawTextureImportSettings`→TextureAssetInspector, `DrawVolumeProfileAsset`→VolumeProfileAssetInspector, `DrawSceneAssetActions`(inlined `SceneCommands.Open`, dead `OpenScene` removed)→SceneAssetInspector, pyscene/text hints→their shims, `DrawPrefabInspector`→PrefabAssetInspector, `DrawDataAssetInspector`(+`dataAssetPath`/`dataAssetInstance` fields + `LoadDataAsset`/`SaveDataAsset`)→DataAssetInspector, `DrawAudioClipAsset`→AudioClipAssetInspector, `DrawAnimationClipAsset`→AnimationClipAssetInspector. `+196/-196` symmetric (move-not-fix).
- **Enablers on InspectorPanel:** `DrawMemberList` widened `private`→`internal` (DataAsset shim calls `panel.DrawMemberList`); added `internal void Select(Entity)` passthrough (prefab shim's `panel.Select(root)`, mirrors `MarkViewportDirty`). `audioPreviewVoice`/`AcceptGuidDrop` were already internal-static (RW1.3) — reached as `InspectorPanel.X`.
- **AssetInspectors.cs usings added:** `BallisticEngine.AssetPipeline` / `.Loaders` / `BallisticEngine.Serialization` (DataAssetSerializer) / `Hexa.NET.ImGui` + SysVec aliases. Bare-`BallisticEngine` engine types (Audio/Material/Texture2D/VolumeProfile/PrefabAsset/DataAsset/RenderBackendSelector/IAudioVoice/AnimationClip) resolve via enclosing-namespace lookup; editor types (EditorIcons/ImGuiController/EditorApplication/Dx12EditorPreview/MaterialPreviewRenderer/EditorCommands/SceneCommands/AsyncAssetImport/VolumeProfileEditor) via parent `BallisticEngine.Editor`.
- **Byte-exact:** AssetInspectors.cs has ZERO U+009D bytes (only clean em-dashes I authored); the 2 pre-existing U+009D in ComponentPreviews.cs untouched (RW1.4 did not touch that file). The 2 U+009D in InspectorPanel (DrawBatchTextureType comment, AcceptGuidDrop comment) are in code that STAYS — untouched.
- **ORACLE:** editor csproj 0 CS errors (the only MSB error was a bin-copy file-lock from a LIVE editor PID + Rider debugger holding the .exe/.dll — environmental, NOT a compile error, exactly the documented `.slnx` lock caveat); full reflection harness EXIT=0 ALL GREEN (AssetInspector B2 17/17, ComponentPreview B1 16/16, UndoCoverage F3 22/22 — the harness rebuilds the editor DLL into its OWN bin so it validated my code loads+resolves). InspectorPanel `2914→2578`. HEADLESS — no editor launch.
- **RW1 SCOPE COMPLETE:** `grep "void Draw\w*Section"` in InspectorPanel = 0 matches; all component-preview + asset-inspector bodies relocated; curve/gradient already in EditorWidgets/terminal-drawers. The ~800–1000-line target was an over-estimate — residual 2578 lines is CORE machinery (entity inspector/transform/component headers+menus/DrawMemberList layout driver/asset slots/pickers/add-component/multi-asset/layout helpers) that RW1 never scoped to move. §0 status + memory index updated this chunk.
**RW1 marked DONE.** NEXT = chunk 47 (RW2: type-scale + drawer-row redesign, Phase E core). ⚠ RW2 LAUNCHES the editor → GPU-hang safety in force (DRED-first, NEVER relaunch-loop; a TDR once crashed the whole PC).

### Chunk 47 (RW2) — DONE  ★ Phase E core STARTED (type-scale + drawer-row redesign)
First Phase-E visual work. EditorTheme.cs DID NOT EXIST before (plan §9 "real source of çiğ/standart görünüyor"); created it as the single-source for TYPOGRAPHY + drawer-row style. RW2 is **intentionally NOT byte-identical** (it's the first deliberate visual change) but behaviour (harness) is unchanged.
- **NEW FILE `BallisticEngine.Editor/ImGuiBackend/EditorTheme.cs`** — semantic TYPE SCALE (`Display`/`Header`/`Body`/`Caption` `ImFontPtr` handles, assigned by ImGuiController on every atlas (re)build; default to the default font so callers can always PushFont) + scale multipliers (`DisplayScale 1.62` / `HeaderScale 1.12` / `CaptionScale 0.84` off `BodySize`) + drawer-row palette (`RowLabel` legible label color replacing the dead TextDisabled grey, `RowCaption` for hints/badges, `RowHoverFill`/`RowHoverBar`/`RowAccentBarWidth` for the hover affordance).
- **`ImGuiController.LoadFont` (rewritten)** — bakes the 3 extra semantic sizes into the atlas: Caption = Inter-Regular @ BodySize*0.84; Header = Inter-SemiBold @ *1.12; Display = Inter-SemiBold @ *1.62; each merged with the icon glyphs via a new `LoadSizedWithIcons` helper. Falls back to the body/Bold font if the .ttf is missing. `EditorTheme.BodySize` set here. PERF: resolved ONCE per atlas build (NOT per frame), same as the existing Bold/LargeIcons.
- **`InspectorPanel.Row` / `RowWithTooltip` (redesigned)** — label now drawn with `EditorTheme.RowLabel` (PushStyleColor + TextUnformatted, was `TextDisabled`); `(?)` badge uses `RowCaption`; both call the new `RowChrome()` right after `TableNextRow()`. `RowChrome` is hover-gated drawer-row decoration: when the mouse is over the current row's screen band it paints a faint accent row-bg (`TableSetBgColor RowBg0`) + a left accent sliver (one `AddRectFilled`). PERF (plan §4): hover-only, one bg-color + one rect, no per-row gradient/alloc/reflection. Since ALL member rows route through Row/RowWithTooltip (component members via `ImGuiComponentGui.BeginRow`→`host.RowWithTooltip`, plus every shim `Row`), the whole inspector picks up the look in one place — no per-panel widget hand-rolling (attribute-driven mandate held).
- **`InspectorPanel.DrawEntityHeaderCard`** — the entity NAME field now PushFont(`EditorTheme.Header`) so the top-of-hierarchy title reads as a title; the "N components" meta line uses Caption font + RowCaption color. Card height recomputed from the header-font frame height (`row1H = max(frameH, headerFrameH)`) so the bigger name field never clips; row-2 Y offset uses `row1H`.
- **ORACLE:** (a) editor csproj build 0 CS errors (`ImFontPtr.FontSize` resolves fine). (b) full reflection harness EXIT=0 ALL GREEN (B1 16/B2 17/F3 22 + all others — RW2 is visual-only, behaviour unchanged). (c) VISUAL: launched the DX12 editor headless TWICE (once `BALLISTIC_SCREENSHOT`+exit, once background + remote-pipe `select Car` then `screenshot`) — **NO device-removal/TDR/hang both times**, clean exit; the captured inspector shows the bigger "Car" header title, recessive "3 components" caption, legible row labels, the `(?)` badges, and the hover accent bar on the Transform/Rigidbody header rows. GPU-hang safety held (single launches, no relaunch-loop). Lingering editor PID was killed after.
- GOTCHA for RW3: `EditorTheme` is the seed of the eventual centralized theme (plan §5 E1/E5) — RW3 should EXTEND it (add a palette block + the in-viewport toolbar reading from it), not create a parallel theme file. The accent comes from `EditorPrefs.Current.Accent` (SysVec4). Editor LAUNCH still required for RW3 visual verify → GPU-hang safety stays in force.
**RW2 marked DONE.** NEXT = chunk 48 (RW3: in-viewport toolbar + theme — move scene-manipulation tools out of the cramped top bar into a Unity-style in-viewport overlay; continue EditorTheme.cs with a palette block). ⚠ RW3 LAUNCHES the editor → GPU-hang safety in force.
