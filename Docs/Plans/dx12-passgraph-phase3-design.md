# DX12 Pass-Graph — PHASE 3 DESIGN: the authored `[RenderFeature]` layer

**This is a DESIGN doc (chunk 18), not an implementation.** Phase 1 (pluggable pass list) and phase 2
(true frame graph — DAG/cull/aliasing/auto-barriers) are DONE (see `dx12-passgraph-phase2-done.md`).
The graph is now solid: a thin orchestrator + `Dx12RenderGraph` of `IRenderPass`. Phase 3 is the LAST
piece the user asked for — **"a render-feature / custom-pass system like Unity"** — and per the master
plan (`silly-leaping-fog.md` §Architecture intro lines 22-23) it is a **FEATURE**, not a refactor, so the
first deliverable is this design + a chunk-by-chunk sub-plan, NOT a big-bang implementation.

> Plan of record (method): `C:\Users\suley\.claude\plans\silly-leaping-fog.md`. Memory:
> `dx12-passgraph-plan-2026-06-17.md`. Branch `dx12-renderer`. Substrate pinned (golden): AMD RX 9070 XT,
> driver 32.0.31019.2002, Win 10.0.26200, golden tree the phase-1 freeze (`Docs/Validation/dx12-golden-set.json`).

---

## 1. Goal (what "a render-feature system like Unity" means here)

Unity URP's `ScriptableRendererFeature` / `ScriptableRenderPass`: a game/project authors a feature class,
the feature is **discovered + serialized + editor-reorderable**, it declares an injection point
(`renderPassEvent`) and resource reads/writes, and the renderer slots it into the frame at that event with
the rest of the built-in passes. **No engine recompile, no renderer edit** to add a custom pass.

Phase 3 delivers the same on this engine's now-solid DX12 graph: an authored `RenderFeature` (engine-side,
zero-backend-reference) that the DX12 backend adapts into an `IRenderPass` and `graph.Add`s alongside the
14 built-ins — declaring its event + reads/writes so the existing V1/V2/V3 compiler schedules, culls,
aliases and auto-barriers it exactly like a built-in. Authored features carry overridable parameters that
render through the existing attribute-driven inspector and serialize by type-name — **a direct mirror of
the Volume framework** (the precedent the plan names).

**The pixel-neutral gate (the whole-phase invariant): with NO feature registered, today's pipeline is
byte-identical to the frozen golden set, under all 4 graph door states + GBV 0-NEW.** Every phase-3 chunk
must hold this — phase 3 ADDS an opt-in authoring surface; it must not perturb the proven default frame.

---

## 2. The two precedents this mirrors (verified in code, chunk 18)

### 2a. The Volume framework — the AUTHORING + DISCOVERY + SERIALIZATION + EDITOR shape to copy

| Volume framework piece | File | Role | Phase-3 analogue |
|---|---|---|---|
| `Volume : Behaviour` (`[Component]`) | `Engine/Rendering/Volumes/Volume.cs` | entity component, `Profile` guid ref, registers in `OnAttach`/`OnDetach` | `RenderFeatureSet : Behaviour` OR a `SceneBehaviour` holding the ordered feature list (see §5 decision D2) |
| `VolumeProfile : BObject` | `Engine/Rendering/Volumes/VolumeProfile.cs` | shareable `.volume` JSON asset, list of components, ≤1 per type | feature list = ordered, MULTIPLE-of-a-type allowed (URP lets you add the same feature twice) — a list, not a set |
| `VolumeComponent` (abstract) | `Engine/Rendering/Volumes/VolumeComponent.cs` | reflection-discovers `public readonly VolumeParameter` fields by `MetadataToken`; `Active` master switch | `RenderFeature` (abstract, engine-side) — reflection-discovered overridable params; `Active`/`Enabled` switch + `Event` + declared reads/writes |
| `VolumeParameter<T>` (+ Clamped/Color/Enum) | `Engine/Rendering/Volumes/VolumeParameter.cs` | typed overridable value (`Overridden` flag, `Interp`) | feature params are PLAIN members with `[Range]`/`[Tooltip]` — NO blending needed (a feature is on or off; it does not cross-fade like a post-fx grade). **This is the key divergence** — features don't blend, so they DON'T need the `VolumeParameter` wrapper; they use plain decorated members like a normal Behaviour. |
| `VolumeManager` (static) | `Engine/Rendering/Volumes/VolumeManager.cs` | blends all active volumes → one `VolumeStack` per frame, stable insertion sort by priority | `RenderFeatureManager` (engine-side) — gathers active features in authored order; NO blend, just the ordered active set the backend consumes |
| `VolumePostProcessing.Apply` | `Engine/Rendering/Volumes/VolumePostProcessing.cs` | the ONE stack→`PostFX` bridge (engine→backend boundary) | `Dx12RenderFeatureBridge` (backend-side) — the ONE place that turns engine-side features into `IRenderPass`es and `graph.Add`s them. The Volume framework's bridge proves the pattern: engine produces config, backend consumes it through one method. |
| `ComponentRegistry.Build` | `ToolKit/.../ComponentRegistry.cs` | reflection-discovers `VolumeComponent` subtypes → `VolumeMenu`, `ResolveVolume`, `VolumeNameOf` | add a parallel `RenderFeatureMenu` / `ResolveFeature` / `FeatureNameOf` (one more branch in `Build`'s type loop — exact same code shape as the volume branch) |
| `VolumeProfileLoader` | `AssetPipeline/Loaders/VolumeProfileLoader.cs` | `.volume` JSON ⟷ profile by TYPE-NAME + per-param name, legacy remaps, unknown→warn-skip | feature-list serialization by type-name + decorated members (reuse `ComponentReflection`, the scene serializer already does this for Behaviours — see §5 D3) |
| `[Range]`/`[Tooltip]`/`[ShowIf]`… | `Engine/Attributes/EditorAttributes.cs` (+ `ConditionalAttributes.cs`) | plain `System.Attribute`, ZERO ImGui/GL/DX12, editor-only interpretation | features decorate params with the SAME attributes — they render through the existing `DrawerPipeline` for free |
| `DrawerRegistry` / `IInspectorGui` / decorator chain | `BallisticEngine.Editor/Panels/Inspector/` | ONE attribute-driven drawer pipeline; new value type = one `ITypeDrawer` | feature params draw through it unchanged; the feature-LIST editor adds a reorderable-list wrapper (URP's feature list UI) — the only new editor widget |

**The load-bearing lesson from the Volume framework: engine-side config is plain reflectable C# with
zero-ImGui attributes; exactly ONE bridge method crosses to the backend; the editor interprets attributes
but the engine never references the editor.** Phase 3 copies this seam verbatim.

### 2b. The DX12 graph seam — the MOUNT POINT an authored feature plugs into (verified in code)

- `Dx12RenderPassEvent` (`BallisticEngine.DX12/Resources/Dx12RenderPassEvent.cs`) — enum spaced by 50,
  **explicitly designed** (its own comment) so "a feature/custom pass can slot at `Event + 1`". An authored
  feature's injection point maps 1:1 onto this enum. This is THE reason the spacing exists.
- `IRenderPass` (Event/Name/Enabled/Resize/Record/`Declare`) — the contract the backend adapter implements
  on behalf of an authored feature. `Declare(builder)` is the phase-2 bridge a feature uses to participate
  in cull/alias/auto-barrier.
- `Dx12FrameContext` (by-ref mutable; `SceneColor` mutable mid-frame) — the per-frame state a feature reads.
  **R8 already protects it**: read-only fields are `init`-only, only `SceneColor`/`GiMode`/etc. are settable
  — so an authored feature physically cannot reassign `ctx.View`. This was designed FOR phase 3.
- `Dx12RenderGraph.Add(pass)` — registration-order = stable tiebreak. Built-ins are added in
  `DX12HDRenderer` ctor (lines 558-620: deferred, ssao, sky, ap, fog, transparents, gi, reflections, taa,
  fsr, composite, cullProbe). A feature is just one more `graph.Add` BEFORE `graph.Build()` — the compiler
  treats it identically.
- `Dx12PassBuilder` — `Read/Write/ReadWrite/Touch/AllowCulling/DeriveBarriers/Use`. An authored feature
  declares against CANONICAL named handles (`Resource("SceneColor")`) so a DAG edge forms with the built-ins.

**Conclusion: the graph needs ZERO new plumbing to host a feature.** Phase 3 is purely (1) an engine-side
authoring layer and (2) one backend bridge that builds an `IRenderPass` adapter from each authored feature
and `graph.Add`s it. The hard 80% (the graph) is already done.

---

## 3. The engine-vs-backend seam (DECISION — the load-bearing architecture call)

**Decision: the `[RenderFeature]` attribute + the `RenderFeature` base class + its param/event/declared-IO
model live ENGINE-SIDE (a new `Engine/Rendering/RenderFeatures/` folder + the attribute in
`Engine/Attributes/`), with ZERO reference to `BallisticEngine.DX12`. The DX12 backend INTERPRETS them.**

Rationale (mirrors the Volume + EditorAttributes + HDRenderer precedents exactly):
- `Engine/Attributes/*.cs` are plain `System.Attribute` with primitive args, zero ImGui/GL/DX12 — "so the
  engine source stays free of editor/renderer dependencies" (file header). `[RenderFeature]` joins them.
- The Volume framework is entirely engine-side; only `VolumePostProcessing.Apply` knows the renderer's
  `PostProcessSettings`. The HDRenderer abstraction (`Abstraction/Rendering/Renderer/HDRenderer.cs`) is the
  engine↔backend contract; DX12HDRenderer is the backend impl. Phase 3's bridge is the analogue.
- A GAME must be able to author a render feature **without referencing `BallisticEngine.DX12`** (game
  scripts compile into `GameScripts.dll` against the engine library only — see CLAUDE.md "Game scripting").
  So the authoring surface CANNOT live in the DX12 assembly.

**The seam, concretely:**
```
Engine (BallisticEngine.csproj — what a game references):
  Engine/Attributes/RenderFeatureAttribute.cs        [RenderFeature("Name", Menu=…)]  — plain System.Attribute
  Engine/Rendering/RenderFeatures/RenderFeature.cs    abstract base: Active, Event(enum), declared reads/writes
  Engine/Rendering/RenderFeatures/RenderPassEvent.cs  ENGINE-SIDE event enum (mirrors Dx12RenderPassEvent values/order)
  Engine/Rendering/RenderFeatures/RenderFeatureManager.cs  gathers the active authored set per frame (no blend)
  Engine/Rendering/RenderFeatures/IFeaturePassRecorder.cs  the abstract recording surface a feature's Record() calls
        — backend-agnostic command verbs (Blit/SetRenderTarget/DrawFullscreen/Dispatch…); the engine never
          sees a DX12 type. (URP's CommandBuffer analogue; the abstraction that keeps the feature portable.)

Backend (BallisticEngine.DX12 — interprets the engine config):
  Resources/Dx12RenderFeatureBridge.cs   the ONE bridge: for each active RenderFeature, build a
        Dx12FeaturePassAdapter : IRenderPass and graph.Add it (mirrors VolumePostProcessing.Apply).
  Resources/Dx12FeaturePassAdapter.cs     IRenderPass impl: maps RenderFeature.Event→Dx12RenderPassEvent,
        Enabled→feature.Active, Declare→feature's declared reads/writes against canonical handles, and
        Record→drives the feature.Record(IFeaturePassRecorder) through a Dx12 impl of IFeaturePassRecorder.

Editor (BallisticEngine.Editor — interprets the attributes, never referenced by engine):
  the existing DrawerPipeline draws feature params; ONE new reorderable-list widget for the feature list.
```

**Why an abstract `IFeaturePassRecorder` and not "the feature gets the Dx12FrameContext":** if a feature
received `Dx12FrameContext` it would have to reference `BallisticEngine.DX12` → a game couldn't author one,
defeating the whole point. The recorder is the backend-agnostic verb surface (the URP `CommandBuffer`
role). The first chunks ship a DELIBERATELY MINIMAL recorder (just enough for a "blit/tint SceneColor"
proof feature); the verb set GROWS per concrete feature need, never speculatively (subtract-complexity
doctrine). This is the single biggest design risk and is sequenced FIRST (chunk 19) precisely so the seam
is proven on a trivial feature before any real one is built.

---

## 4. The whole-phase invariant (the gate every chunk inherits from phases 1+2)

**PIXEL-NEUTRAL DEFAULT = "no `[RenderFeature]` registered ⇒ today's pipeline byte-identical to the frozen
golden set."** This is the same posture phase 2 used (phase 2 flipped from "free to change the look" to
"pure architecture, must not move a pixel"). Phase 3 adds an opt-in surface; the default frame must not
change. Verification per chunk that lands renderer/engine code:

- **(a) Deterministic golden gate** — `bash e:/tmp/chunk15/matrix.sh "<door env>" <tag>` → 15 rows
  SHA==golden, run under ALL 4 door states (default-off, `GRAPH=1`, `GRAPH+GRAPH_BARRIERS`, `+GRAPH_ALIAS`).
  `bal render` is the golden oracle (forces DETERMINISTIC; the deterministic HALF only — R-NEW-9).
- **(b) GBV 0-NEW** — Runtime.exe direct, `BALLISTIC_DX12_DEBUG=1 BALLISTIC_DX12_GBV=1
  BALLISTIC_DX12_BREAK_ON_ERROR=1`, CornellBox + BistroInterior, alias off+on → exit 0 + "0 NEW
  (0 error-class)" vs `Docs/Validation/dx12-gbv-baseline.json`. (NEVER GBV+FSR together — 18GB hang.
  NEVER set `ID3D12Resource.Name` — changes GBV signature.)
- **(c) regime-(b) boiling** — ONLY for a chunk that touches history/TAA/FSR (no phase-3 chunk should, until
  a feature explicitly injects a temporal pass): motion-dump + `Docs/Validation/dx12-boiling-metric.py`,
  BistroInt frozen band 29.961352 within 0.5%.
- **Once a real feature exists**, ADD a positive test: register a trivial feature (the proof "tint" feature)
  and assert (i) it visibly changed the frame in the expected region (the feature WORKS), and (ii) removing
  it returns byte-identical to golden (the feature is CLEANLY removable / pixel-neutral when off). This is
  the phase-3-specific oracle the golden set alone can't give (golden = no-feature only).

**One commit = one intent** (move vs visual fix separate); GPU-hang safety absolute (never relaunch-loop;
RT_GI/RT_SHADOWS off — pre-existing device-removal, not phase-3's to fix). Build/DLL dance if code lands:
`dotnet build DX12 → Runtime → Cli`, copy `BallisticEngine.DX12.dll` into BOTH Cli AND Runtime bins.

---

## 5. Open design decisions (resolved here so the sub-chunks are unambiguous)

- **D1 — feature LIST vs set.** URP allows the same feature type added MULTIPLE times (e.g. two blur passes
  at different events). So the authored container is an ORDERED LIST, not a ≤1-per-type set like
  `VolumeProfile`. Serialize as an ordered array of `{type-name, members, enabled}`.
- **D2 — where the feature list lives.** **Decision: a `SceneBehaviour` (`RenderFeatures`/the scene's
  "Renderer" config), not an entity component.** Rationale: render features are a renderer/scene-wide
  concern (like `SceneLighting`/`Skybox`), not per-entity; `SceneBehaviour` already has its own registry
  (`ComponentRegistry.SceneMenu`) + the editor's "Scene" tab + `static Active` read-per-frame pattern (the
  same pattern the renderer uses today). The feature list is read once per frame by the manager, exactly
  like the renderer reads `Skybox.Active`. (URP keeps features on the Renderer asset; a `SceneBehaviour` is
  this engine's closest analogue. Revisit only if per-camera feature sets become a need.)
- **D3 — feature param serialization.** **Reuse `ComponentReflection` + the scene YAML path** (it already
  serializes Behaviour public props/fields, asset refs as guids — CLAUDE.md "Scenes & components"). A
  feature is reflection-shaped just like a Behaviour, so its members serialize for free; the feature-LIST
  is an ordered list of `{type-name (via `FeatureNameOf`), members}`. Do NOT invent a parallel JSON loader
  unless a feature needs the asset-sharing the `.volume` path gives (it doesn't — features are scene-local
  per D2). Legacy-rename map kept available for future renames (the Volume loader's pattern).
- **D4 — what the FIRST verb set is.** Minimal: `BlitFullscreen(sourceHandle, destHandle, materialOrShader)`
  + `SetRenderTarget(handle)` + read access to `SceneColor`. Just enough for the chunk-19 proof feature
  (tint/invert SceneColor). Every later verb is added on a concrete feature's demand, logged in this doc.
  - **SHIPPED (chunk 19) — split into TWO backend-agnostic surfaces** (the declare side is NOT the same as
    the record side): `IFeaturePassRecorder` = { `string SceneColor {get;}`, `SetRenderTarget(string)`,
    `BlitFullscreen(string src, string dst, string shaderOrMaterial=null)` } (the RECORD-time verbs); and a
    NEW `IFeatureIOBuilder` = { `Read(string)`, `Write(string)`, `ReadWrite(string)`,
    `RequestScratch(string roleName)→string`, `AllowCulling(bool)` } (the DECLARE-time verbs `RenderFeature.Declare`
    uses). Reason for the split: the design (§3) said `Declare` must be engine-agnostic too (string handle
    names, NOT `Dx12PassBuilder`) — so it needs its own backend-neutral builder interface, parallel to the
    recorder. Both are string-handle-keyed; the chunk-20 DX12 adapter implements both and maps names→graph
    handles / `Dx12PassBuilder` reads-writes.
- **D5 — default `Active`/removability.** A feature defaults `Active=true` when added (URP parity), but the
  WHOLE layer is inert until a `RenderFeatures` SceneBehaviour with ≥1 feature exists in the scene — so the
  golden scenes (which have none) are untouched. The manager early-outs on empty exactly like
  `VolumeManager.Update` early-outs on `volumes.Count == 0`.
- **D6 — culling/aliasing/barriers for an authored feature.** A feature's adapter declares reads/writes →
  it participates in V1 cull (only if it opts in via `AllowCulling`, default OFF — same safety default), V2
  aliasing (its scratch targets can be pooled if it requests transient ones), V3 auto-barriers (it can opt
  into `DeriveBarriers`/`Use`, else its adapter emits manual head transitions). DEFAULT: opaque-ish — a
  feature that declares nothing is an opaque node (never culled, manual barriers) — the safe escape hatch,
  identical to an un-migrated built-in. Aggressive participation is opt-in per feature.

---

## 6. Sub-chunk plan (chunk 19 onward — one chat each, same handoff discipline)

Each sub-chunk = ONE intent, ends with a handoff prompt, gated by §4. Ordered safest→riskiest; the seam is
proven on a trivial feature BEFORE any real feature is built (the chunk-18 risk call).

- **Chunk 19 — engine-side scaffold (PIXEL-NEUTRAL, no backend wiring yet). ✅ DONE.** Add the engine-side
  authoring surface ONLY: `[RenderFeature]` attribute (`Engine/Attributes/`), `RenderFeature` abstract base +
  `RenderPassEvent` enum + `RenderFeatureManager` + `IFeaturePassRecorder` (`Engine/Rendering/RenderFeatures/`),
  the `RenderFeatures` SceneBehaviour (D2), and the `ComponentRegistry` branch (`RenderFeatureMenu`/
  `ResolveFeature`/`FeatureNameOf`). NOTHING calls into the backend yet; no `IRenderPass` is built. Gate:
  the engine compiles, `ComponentRegistry.Build` discovers a sample feature type, the slnx builds 0-err,
  editor 0-err, and **golden 15/15 + GBV 0-NEW are untouched** (no renderer code changed → trivially holds,
  but RUN it to prove the new types didn't perturb bootstrap). Commit. (Pure additive engine scaffold — the
  Volume-framework analogue of "the classes exist, nothing renders yet".)
  - **WHAT SHIPPED (8 new files + 2 edits):** `Engine/Attributes/RenderFeatureAttribute.cs`
    (`[RenderFeature("Name", "Menu")]`, `HideFromAddMenu`; mirrors `ComponentAttribute` — `Menu` is a ctor
    POSITIONAL arg, NOT a named arg: getter-only props can't be named attr args → CS0617, the design's
    `Menu=…` shorthand was illustrative). `Engine/Rendering/RenderFeatures/`: `RenderPassEvent.cs`
    (16 members, 0..750, **textually identical** to `Dx12RenderPassEvent` — verified by diff; INVARIANT to
    keep in lock-step), `RenderFeature.cs` (abstract: `Active` def-true, `virtual Event`=PostProcess,
    `virtual Declare(IFeatureIOBuilder)` default-empty=opaque escape hatch, abstract `Record(IFeaturePassRecorder)`;
    params are PLAIN decorated members — NO VolumeParameter, the §2a divergence), `IFeaturePassRecorder.cs`
    (D4 minimal verbs: `string SceneColor {get;}` + `SetRenderTarget(name)` + `BlitFullscreen(src,dst,shaderOrMaterial?)`),
    `IFeatureIOBuilder.cs` (engine-agnostic declare surface — `Read/Write/ReadWrite/RequestScratch/AllowCulling`,
    string handle names, NOT `Dx12PassBuilder`; the backend adapter translates these → `Dx12PassBuilder` in
    chunk 20), `RenderFeatureManager.cs` (static; `Gather()` returns the active-in-order count, early-outs on
    no/empty/inactive `RenderFeatures.Active`; `Reset()` ALC-reload hook), `RenderFeatures.cs` (SceneBehaviour,
    `static Active`, `List<RenderFeature> Features`), `Builtin/SceneColorTintFeature.cs` (the discoverable sample
    — Tint/Strength plain members, NOT placed in any scene). **Edits:** `ComponentRegistry.cs` (parallel
    `featureByName`/`featureMenu` + `RenderFeatureMenu`/`ResolveFeature`/`FeatureNameOf` + `RegisterFeature`
    reading `[RenderFeature]`/`HideFromAddMenu`), `EngineBootstrap.ReloadGameScripts` (`RenderFeatureManager.Reset()`
    next to `VolumeManager.ResetStack()`).
  - **D4 verb set as of chunk 19** (grow per concrete feature, log here): RECORDER = { `SceneColor` (read
    accessor), `SetRenderTarget(handleName)`, `BlitFullscreen(src,dst,shaderOrMaterial?)` }; IO-DECLARE =
    { `Read`, `Write`, `ReadWrite`, `RequestScratch(roleName)→name`, `AllowCulling(bool)` }. (NEW DESIGN FACT:
    the engine-agnostic DECLARE surface is split into its own `IFeatureIOBuilder` so `RenderFeature.Declare`
    never names `Dx12PassBuilder` — the §3 seam needs a backend-neutral declare verb just as it needs a
    backend-neutral record verb.)
  - **VERIFIED:** slnx 0-err (editor incl.); scratch harness `bal-feature-test` 16/16 PASS (menu entry +
    DisplayName/Menu, `ResolveFeature` round-trip + unknown/null→null, `FeatureNameOf` round-trip, abstract base
    excluded, `Active` def-true, `Event`=PostProcess, enum 16@0..750, `Gather()`==0 with no host); enum diff
    vs `Dx12RenderPassEvent` empty; golden 15/15 × {default, GRAPH=1, GRAPH+BARRIERS}; GBV CornellBox
    `8 known, 0 NEW (0 error-class)`.

- **Chunk 20 — the backend BRIDGE + adapter + the PROOF feature (the seam test).** Add
  `Dx12RenderFeatureBridge` + `Dx12FeaturePassAdapter` + a DX12 `IFeaturePassRecorder` impl (the minimal D4
  verb set). Wire the bridge into `DX12HDRenderer` (one call after the built-in `graph.Add`s, BEFORE
  `graph.Build()` — gated so it's a no-op when no `RenderFeatures` SceneBehaviour exists). Add ONE trivial
  built-in proof feature (a "SceneColor tint/invert at PostProcess-1"). Verify: (i) with NO feature in the
  scene → golden 15/15 + GBV 0-NEW (the pixel-neutral default); (ii) with the proof feature added → the
  frame visibly tints in the expected region AND removing it returns byte-identical to golden (the positive
  + removability oracle, §4). This is THE risky chunk (engine↔backend seam); it's the first that runs the
  graph with a feature. If the verb surface proves too thin for even the tint, GROW it minimally + log here.

- **Chunk 21 — serialization round-trip (D3).** Make the `RenderFeatures` SceneBehaviour's feature list
  serialize/deserialize through the scene YAML (reuse `ComponentReflection`): `FeatureNameOf` for the type,
  members via reflection, ordered list preserved, unknown-type warn-skip (Volume-loader parity). Verify: a
  scene with the proof feature saved→loaded reproduces it; a scene with NO features is byte-identical YAML
  to today; golden 15/15 + GBV 0-NEW (no feature in golden scenes). Commit.

- **Chunk 22 — editor: the reorderable feature-list UI.** The feature PARAMS already draw via the existing
  `DrawerPipeline` (attributes free). Add the ONE new widget: a reorderable list on the `RenderFeatures`
  SceneBehaviour's inspector (add/remove/reorder/enable-toggle features), pushing `EditorUndo` like every
  other editor mutation, marking the viewport dirty. Reorder changes registration order → re-`Build`/`Compile`
  the graph. Verify in-editor (per gpu-hang safety: GBV-off, no RT) + golden unaffected (editor-only). Commit.

- **Chunk 23 — phase-3 DoD + acceptance doc + (optionally) one REAL example feature.** Write
  `dx12-passgraph-phase3-done.md`: the seam is proven, a feature is authorable without referencing DX12,
  discovered/serialized/editor-reorderable, participates in the graph (declare→cull/alias/barriers), and the
  no-feature default is byte-identical to golden under all gates. OPTIONALLY ship one genuinely useful
  example feature (e.g. a custom outline or a screen-space tint volume-driven) to validate the verb set on
  real work — its own move/fix-split commits, golden-neutral when absent. Then phase 3 (and the whole
  pass-graph migration) is DONE.

A sub-chunk that runs long stops, commits what's clean, and the handoff carries the partial state — the
chat boundary is the rollback boundary. Verb-set growth (D4) and any new design fact are recorded in THIS
doc + the memory file as discovered.

---

## 7. Definition of done (phase 3)

A game/project can author a `RenderFeature` subclass in `GameScripts.dll` (engine reference ONLY — no
`BallisticEngine.DX12`), decorate its params with the existing `[Range]/[Tooltip]/[ShowIf]…` attributes,
and have it:
1. **Discovered** by `ComponentRegistry.Build` (a `RenderFeatureMenu` entry, resolvable by type-name).
2. **Authored** into a scene via the `RenderFeatures` SceneBehaviour's reorderable list (add/remove/reorder/
   enable), edited through the existing attribute-driven `DrawerPipeline`, with `EditorUndo`.
3. **Serialized** by type-name + members through the scene YAML (`ComponentReflection`), ordered, unknown→
   warn-skip, round-trip stable.
4. **Scheduled** by the DX12 graph: the backend bridge builds an `IRenderPass` adapter that maps the
   feature's event→`Dx12RenderPassEvent`, `Active`→`Enabled`, declared reads/writes→`Declare`, and drives
   `Record` through a backend-agnostic `IFeaturePassRecorder` — so V1 cull / V2 aliasing / V3 auto-barriers
   apply to it exactly like a built-in (opt-in, opaque-by-default escape hatch).
5. **Pixel-neutral by default**: with no `RenderFeatures`/feature present, the pipeline is byte-identical to
   the frozen golden set across all 4 graph door states + GBV 0-NEW + regime-(b) where applicable. Adding a
   feature changes pixels ONLY where the feature acts; removing it returns to golden.

The engine↔backend↔editor seam is the Volume-framework seam (engine-side config + zero-ImGui attributes +
ONE backend bridge + editor interprets attributes), now applied to the render graph instead of post-fx.
Lumen untouched; GPU-hang safety + one-intent-commits + golden-set + GBV gates carried from phases 1+2.
