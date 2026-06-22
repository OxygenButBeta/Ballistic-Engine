# DX12 Pass-Graph — PHASE 3 DONE: the authored `[RenderFeature]` layer (acceptance + sign-off)

**This is the ACCEPTANCE doc (chunk 23) — the LAST chunk of the DX12 pass-graph migration.** It is
the by-the-book sign-off that the phase-3 Definition of Done (`dx12-passgraph-phase3-design.md` §7) is
met item-by-item with commit SHAs and oracle evidence, AND that the whole migration — **phase 1
(pluggable pass list) + phase 2 (true frame graph) + phase 3 (authored render-feature layer)** — is
DONE. There is no chunk 24: phase 3 is the last piece the user asked for.

> Plan of record (method): `C:\Users\suley\.claude\plans\silly-leaping-fog.md`.
> Phase-3 design + chunk roadmap: `Docs/Plans/dx12-passgraph-phase3-design.md` (§6 chunks, §7 DoD).
> Phase-2 sign-off: `Docs/Plans/dx12-passgraph-phase2-done.md`. Memory: `dx12-passgraph-plan-2026-06-17.md`.
> Branch `dx12-renderer`. Golden substrate pin (R-NEW-6): AMD Radeon RX 9070 XT, driver 32.0.31019.2002,
> Win 10.0.26200, D3D12SDKLayers 10.0.26100.8521, 1920x1080. Golden set `Docs/Validation/dx12-golden-set.json`,
> GBV baseline `Docs/Validation/dx12-gbv-baseline.json`.

---

## 0. TL;DR

Phase 3 is COMPLETE and ACCEPTED. A game/project can author a `RenderFeature` subclass in
`GameScripts.dll` against the engine library ONLY (zero `BallisticEngine.DX12` reference), decorate its
params with the existing `[Range]/[Tooltip]/[ShowIf]…` attributes, and the engine + DX12 backend +
editor pick it up: **discovered → authored → serialized → scheduled in the graph → pixel-neutral when
absent.** This mirrors the Volume framework seam exactly (engine-side config + zero-ImGui attributes +
ONE backend bridge + editor-interprets-attributes), now applied to the render graph instead of post-fx.

The whole pass-graph migration is done: the `DX12HDRenderer` god-object became a thin orchestrator over
a `Dx12RenderGraph` of `IRenderPass`es (phase 1), the graph gained a real DAG / cull / transient
aliasing / auto-derived batched barriers compiler (phase 2, V1→V3; V4 async-compute consciously
declined — no measured headroom, chunk 17 `05286da9`), and an authored Unity-style render-feature layer
sits on top (phase 3). Lumen untouched throughout; golden byte-identical; GBV 0-NEW.

---

## 1. Phase-3 Definition of Done (`dx12-passgraph-phase3-design.md` §7) — item by item

The §7 DoD is five numbered claims. Each below = the claim, the chunk/commit that delivered it, and the
oracle evidence.

### DoD item 1 — DISCOVERED by `ComponentRegistry.Build`

**Claim:** a `RenderFeature` subclass appears as a `RenderFeatureMenu` entry and is resolvable by
type-name (`ResolveFeature` / `FeatureNameOf`), exactly like a `VolumeComponent`.

| | |
|---|---|
| **Delivered by** | Chunk 19 (`6c25ce6e` — engine-side scaffold). |
| **How** | `ComponentRegistry.cs` gained a parallel `featureByName`/`featureMenu` pair + `RenderFeatureMenu`/`ResolveFeature`/`FeatureNameOf` + a `RegisterFeature` branch in `Build`'s type loop that reads `[RenderFeature]`/`HideFromAddMenu` — the same code shape as the existing `VolumeComponent` branch. The `[RenderFeature]` attribute lives in `Engine/Attributes/RenderFeatureAttribute.cs` (plain `System.Attribute`, primitive args, zero ImGui/GL/DX12). The abstract `RenderFeature` base + a discoverable sample `SceneColorTintFeature` ship in `Engine/Rendering/RenderFeatures/`. |
| **Oracle** | Chunk-19 scratch harness `bal-feature-test` **16/16 PASS**: menu-entry presence, `DisplayName`/`Menu`, `ResolveFeature` round-trip + unknown/null→null, `FeatureNameOf` round-trip, abstract base excluded from the menu, `Active` defaults true, `Event` defaults `PostProcess`, the engine `RenderPassEvent` enum = 16 members 0..750 textually identical to `Dx12RenderPassEvent` (diff empty). slnx 0-err incl. editor. |

### DoD item 2 — AUTHORED into a scene (reorderable list + attribute-driven inspector + undo)

**Claim:** a feature is added/removed/reordered/enabled through the `RenderFeatures` SceneBehaviour's
list, edited through the existing attribute-driven `DrawerPipeline`, with `EditorUndo`.

| | |
|---|---|
| **Delivered by** | Chunk 19 (`6c25ce6e` — the `RenderFeatures` SceneBehaviour, D2) + Chunk 22 (`fc4be698` — the editor reorderable-list widget). |
| **How** | `Engine/Rendering/RenderFeatures/RenderFeatures.cs` is a `SceneBehaviour` (`static Active`, `List<RenderFeature> Features`) — the renderer/scene-wide container, the `SceneLighting`/`Skybox` analogue (design D2). The editor widget lives in `BallisticEngine.Editor/Panels/InspectorPanel.cs` `DrawRenderFeatureList(RenderFeatures)`: one collapsible card per feature (Active checkbox → graph rebuild via the chunk-20 bridge, ^/v reorder, red trash remove — all structural mutations deferred past the per-feature loop, each through `EditorUndo` + `MarkViewportDirty`), an "Add Feature" search popup over `ComponentRegistry.RenderFeatureMenu` (DUPLICATES ALLOWED — URP parity, D1), and per-feature params drawn via the shared `DrawMemberList(feature.GetType(), feature)` (attributes free — no per-feature widget code). `Features` gained `[HideInInspector]` so the generic member list skips the polymorphic list and the dedicated widget owns it — serialization is UNAFFECTED (it drives off `SerializableMembers`, which ignores `[HideInInspector]`). |
| **Oracle** | Chunk 22 editor-only → golden untouched by construction (the only engine-side delta is the inert `[HideInInspector]` attribute, which the renderer never reads). Golden **30/30** SHA==golden (the 15-row matrix under BOTH `default` AND `GRAPH=1`). Chunk-21 round-trip harness re-run after the `[HideInInspector]` add: **18/18 PASS** (the `features:` list still byte-stable, feature-free scene still has no `features:` key). Per gpu-hang-safety the live editor was NOT loop-launched; the widget follows the proven `DrawAddComponentPopup` idiom verbatim. |

### DoD item 3 — SERIALIZED by type-name + members through scene YAML (ordered, unknown→warn-skip, round-trip stable)

**Claim:** the feature list serializes via `ComponentReflection`, ordered, unknown type warns + skips,
the round-trip is stable.

| | |
|---|---|
| **Delivered by** | Chunk 21 (`0c30b259` — serialization round-trip, D3). |
| **How** | INLINE in `SceneSerializer` (NOT a parallel JSON loader, NOT a member-name special-case): the polymorphic `List<RenderFeature>` is handled generically by ELEMENT TYPE in `SerializeValue`/`DeserializeValue` (`IsRenderFeatureList`), alongside the existing `BObject`/`BEvent`/`AnimationCurve`/`ColorGradient` inline special cases — the only place that can round-trip a `List<abstractType>` (the generic `ComponentReflection` member-writer has no type discriminator). On-disk shape: an ORDERED list of `{type: <FeatureNameOf>, active: <bool>, members: {<reflected props/fields>}}`; `active` is a top-level key (mirrors `ComponentDocument.Enabled`, excluded from `members`); `members` omitted when empty; nested members reuse `ApplyMembers` (asset refs as guids included); unknown type → `Debugging.LogWarning` + SKIP with surviving order preserved (Volume-loader parity). |
| **Oracle** | Chunk-21 in-process harness `bal-feature-rt-test` **18/18 PASS**: a 2-feature host saves as the `{type,active,members}` list; save→load→save reproduces it byte-for-byte (order + Vector3 + float + `Active=false` all preserved); an unknown type between two real ones warns+skips without throwing (2 of 3 survive, order kept); a feature-free scene has NO `features:` key. Plus the positive serialized proof: a throwaway `BistroInterior_FeatureTest` scene authoring `RenderFeatures`→`SceneColorTintFeature` rendered a DIFFERENT SHA (`b21a042db0ed320164`) from golden with the expected magenta direction — the seam runs from a SERIALIZED feature, env door unused (throwaway scene deleted, kept OUT of golden). |
| **Known caveat (pre-existing, orthogonal)** | A `SceneBehaviour`'s own `id:` is not restored on deserialize (`DeserializeCore` never sets `behaviour.InstanceId` for scene components; only ENTITY components do). This churns the host's `id:` line on save→load→save but affects EVERY `SceneBehaviour` equally and is orthogonal to the feature list, which is byte-stable. Out of scope; flagged for a future chore. |

### DoD item 4 — SCHEDULED by the DX12 graph (adapter maps event/Active/declared-IO/Record; participates in V1 cull / V2 alias / V3 barriers)

**Claim:** the backend bridge builds an `IRenderPass` adapter mapping `feature.Event`→`Dx12RenderPassEvent`,
`Active`→`Enabled`, declared reads/writes→`Declare`, and drives `Record` through a backend-agnostic
`IFeaturePassRecorder` — so the graph compiler treats it like a built-in (opt-in, opaque-by-default).

| | |
|---|---|
| **Delivered by** | Chunk 20 (`c58ef059` backend bridge + adapter + recorder + proof feature; `f4c8474f` proof door; `eaf6f042` design-doc DONE mark). |
| **How** | 5 new files in `BallisticEngine.DX12` (the ONLY assembly that knows DX12 types): `Dx12RenderFeatureBridge.cs` — the ONE bridge (mirrors `VolumePostProcessing.Apply`): `RenderFeatureManager.Gather()`, rebuild the graph's feature SEGMENT only when the active set changes (`graph.SetFeaturePasses`→re-Build+Compile), no-op for feature-free scenes, called once in `BeginRender` after the volume bridge BEFORE `graph.Execute`. `Dx12FeaturePassAdapter.cs` — `IRenderPass` wrapping ONE feature: `Event=(Dx12RenderPassEvent)(int)feature.Event`, `Enabled=feature.Active`, `Declare`→runs `feature.Declare` through `Dx12FeatureIOBuilder`, `Record`→drives `feature.Record`. `Dx12FeatureIOBuilder.cs` — `IFeatureIOBuilder` impl: string handle names → `Dx12PassBuilder.Read/Write/ReadWrite` against the canonical graph handle of the SAME name (so `"SceneColor"` shares identity with the built-ins → a real DAG edge); `RequestScratch` namespaced; `AllowCulling`→builder opt-in. `Dx12FeaturePassRecorder.cs` — `IFeaturePassRecorder` impl. `Dx12FeatureBlitter.cs` — the proof feature's GPU work. `Dx12RenderGraph` gained `MarkCoreBoundary()` (snapshots the built-in count) + `SetFeaturePasses(features)` (truncate to boundary, append adapters, re-Build+Compile — empty list = the exact built-in graph). A feature that declares nothing = an opaque node (never culled, manual barriers — the safe escape hatch, D6). The whole engine↔backend seam is the abstract `IFeaturePassRecorder`/`IFeatureIOBuilder` (string-handle verbs, NO `Dx12*` type leaks to the engine), so a game authors a feature without referencing DX12 (design §3). |
| **Oracle (pixel-neutral default)** | Chunk-20 golden **15/15** SHA==golden under default / `GRAPH=1` / `GRAPH+GRAPH_BARRIERS`; GBV CornellBox+BistroInterior alias off+on = exit 0, 0 NEW (0 error-class). |
| **Oracle (positive — the seam WORKS + is cleanly removable, the phase-3-specific test §4(d))** | Chunk-20: with the proof door `BALLISTIC_DX12_FEATURE_TINT_TEST=1` (engine-side, default OFF), one `SceneColorTintFeature` (magenta 1.0/0.25/0.6) injects end-to-end → BistroInterior tints magenta (per-channel R×1.0 / G×0.25 / B×0.6; meanAbsDiff 10.50/255), proving the verb set drives real GPU work. Door OFF → byte-identical to golden (`40a68b28de4aa294fb`). GBV with the feature ACTIVE on BistroInterior+CornellBox = exit 0, 0 NEW. Chunk 21 re-proved this from a SERIALIZED feature (no env door). |
| **Note** | Graph participation (V1 cull / V2 alias / V3 auto-barriers) is the EXISTING phase-2 compiler — the adapter declares against canonical named handles so the SAME compiler schedules a feature like a built-in. NO new graph plumbing was needed (design §2b conclusion, verified in code). |

### DoD item 5 — PIXEL-NEUTRAL by default (no feature ⇒ byte-identical to golden, all door states + GBV 0-NEW; a feature changes pixels ONLY where it acts; removing it returns to golden)

**Claim:** the whole phase-3 layer is INERT until a `RenderFeatures` SceneBehaviour with ≥1 feature
exists (manager early-outs on empty exactly like `VolumeManager.Update`, D5) — so the golden scenes
(which have none) are untouched across all 4 graph door states + GBV.

| | |
|---|---|
| **Delivered by** | Every phase-3 chunk inherits this gate (the whole-phase invariant, design §4). Enforced by the manager early-out (chunk 19 `RenderFeatureManager.Gather`) + the bridge no-op for feature-free scenes (chunk 20). |
| **Oracle (chunk 23, this chunk — re-confirmed fresh on the pinned substrate)** | Built DX12 (0-err) → Runtime (0-err) → copied the fresh `BallisticEngine.DX12.dll` into BOTH Cli and Runtime bins (verified by size + timestamp). `bal render` (forces `BALLISTIC_DETERMINISTIC=1`): **CornellBox default `6e3ee554…` == golden; CornellBox `GRAPH=1` `6e3ee554…` == golden (graph-neutral); BistroInterior_Wine default `40a68b28…` == golden; BistroInterior_Wine `GRAPH=1` `40a68b28…` == golden.** GBV (Runtime.exe direct, `BALLISTIC_DX12_DEBUG=1 BALLISTIC_DX12_GBV=1 BALLISTIC_DX12_BREAK_ON_ERROR=1`, CornellBox): **exit 0, `8 message(s): 8 known(baseline), 0 NEW (0 error-class)`**, BMP produced, no device-removal. |
| **Cumulative oracle (the full matrix)** | Across the phase-3 chunks the full coverage matrix was held: golden 15/15 (chunk 19/20/21) and 30/30 (chunk 22, both `default` + `GRAPH=1`) SHA==golden, GBV 0-NEW on CornellBox + BistroInterior alias off+on. The positive+removability pair (item 4) supplies the phase-3-specific oracle the golden set alone cannot (golden = no-feature only). |
| **Regime-(b) temporal** | Not exercised in phase 3: no phase-3 chunk touches history/TAA/FSR (no shipped feature injects a temporal pass — the proof `SceneColorTintFeature` is a stateless in-place post tint). The boiling band (`dx12-noise-floor.json`, BistroInt 29.961352 ±0.5%) therefore stays at the phase-1 level by construction. The regime-(b) gate would only re-arm once a future authored feature injects a temporal pass — flagged for that feature's own verification, not phase 3's. |

**All five DoD items: MET.** ✅

---

## 2. The optional example feature — CONSCIOUSLY DECLINED (and why that is correct)

Design §6 chunk-23 says: write this doc AND **"OPTIONALLY ship one genuinely useful example feature …
its own move/fix-split commits, golden-neutral when absent."** The plan-of-record handoff for this chunk
is explicit: *"optional example feature gerçekten opsiyonelse ve eklemek pixel-neutral değilse EKLEME —
golden'ı bozma"* (if the optional example feature is genuinely optional and adding it isn't pixel-neutral,
do NOT add it — don't break golden).

**Decision: do NOT add a new example feature. Reason:**

1. **The verb set is already validated on real GPU work.** The whole point of the optional feature is "to
   validate the verb set on real work" (§6 chunk-23). That validation is ALREADY DONE end-to-end by the
   shipped `SceneColorTintFeature` (chunk 20–21): it is a real `RenderFeature` that drives the
   `IFeaturePassRecorder.BlitFullscreen` verb through a real DX12 PSO (`Dx12FeatureBlitter` +
   `Shaders/SceneColorTint.hlsl`), proven both env-door-driven (chunk 20, magenta tint, meanAbsDiff
   10.50/255) AND serialized-from-a-scene (chunk 21, SHA `b21a042db0…`). The seam, the bridge, the
   adapter, the recorder, the IO-builder, and the one verb the design specced (D4) are all exercised by a
   genuine feature. A second example would re-prove an already-proven path.

2. **The proof feature is golden-neutral when absent — the example's only hard requirement.** The §6
   constraint on the example is "golden-neutral when absent." `SceneColorTintFeature` is discoverable but
   placed in NO golden scene, the door defaults OFF, and the manager/bridge early-out for feature-free
   scenes — so golden is byte-identical (verified 15/15 → 30/30 across chunks 19–22 and re-confirmed 4/4
   this chunk). The requirement is satisfied without adding anything new.

3. **Adding a NEW always-on feature would be pixel-changing or zero-value.** Any genuinely-useful example
   (outline, screen-space tint, …) only earns its keep if it actually runs in a scene — which by
   definition changes pixels in that scene. To stay golden-neutral it would have to ship OFF/unplaced,
   i.e. exactly the inert posture `SceneColorTintFeature` already occupies. So a new example is either (a)
   pixel-changing (breaks the golden contract this chunk must hold) or (b) inert and therefore redundant
   with the existing proof feature. Neither buys anything; (a) is forbidden by the chunk's own gate.

4. **Subtract-complexity doctrine.** The verb set grows per concrete feature demand, never speculatively
   (design §3, D4). Shipping a speculative second feature to "demonstrate" would add a maintained surface
   for zero proven need — against the engine's own automation/subtraction doctrine.

The example feature is therefore **optional, declined, golden preserved** — the doc + sign-off is the
deliverable, and the seam is already validated on real work by the chunk-20/21 proof feature. If a real
game later needs a custom pass, the authoring path is open and proven; that feature's own verification
(its own move/fix-split commits, golden-neutral when absent) is its concern, not phase 3's.

---

## 3. The whole pass-graph migration — phases 1 + 2 + 3 — DONE

| Phase | What it delivered | Frozen at / DoD evidence |
|---|---|---|
| **Phase 1** — pluggable pass list | `DX12HDRenderer` god-object → thin orchestrator over a `Dx12RenderGraph` of `IRenderPass`es, stably ordered by `Dx12RenderPassEvent`, mutable `Dx12FrameContext` (with `SceneColor`), MINIMAL + kill-switch preserved, Lumen untouched. | Chunk 11 step-G `65ee31cf` (collapse to ONE `graph.Execute(ctx)`); **GOLDEN FREEZE** `4537de95` (`dx12-golden-set.json`, substrate-pinned, determinism floor EXACTLY 0). |
| **Phase 2** — true frame graph | Passes `Declare` reads/writes; the graph COMPILES a DAG (V1 `f6f07d02`), culls, derives order, then a transient RT pool + lifetime aliasing (V2 `18cf17f0`), then auto-derived BATCHED boundary barriers retiring the manual head transitions (V3 `4bd39194`…`6a6ead2b`). V4 async-compute consciously declined — no measured headroom (chunk 17 `05286da9`). Cross-frame history imported never aliased; `ExecuteSyncImmediate` points modeled as hard nodes; Lumen untouched. | Pixel-neutral vs the frozen golden across the full matrix under BOTH oracle regimes (deterministic SHA + regime-(b) boiling) + GBV 0-NEW. See `dx12-passgraph-phase2-done.md`. |
| **Phase 3** — authored render-feature layer | Engine-side `[RenderFeature]` + `RenderFeature` base + `RenderFeatureManager` + `IFeaturePassRecorder`/`IFeatureIOBuilder` + `RenderFeatures` SceneBehaviour (chunk 19 `6c25ce6e`); the ONE backend bridge + adapter + recorder + proof feature (chunk 20 `c58ef059`/`f4c8474f`); serialization round-trip (chunk 21 `0c30b259`); editor reorderable list (chunk 22 `fc4be698`). Discovered / authored / serialized / scheduled / pixel-neutral-by-default. | This doc, §1 — all five §7 DoD items met; golden 4/4 SHA==golden re-confirmed this chunk; GBV 0-NEW. |

**The end goal the user asked for is realized:** *"a render-feature / custom-pass system like Unity"*
AND *"a real (advanced) render graph — yedirelim aynı plana."* Both shipped, incrementally, without a
big-bang rewrite, on the same plan: a true URP-RenderGraph/Frostbite-FrameGraph-class compiler (DAG /
cull / transient aliasing / auto-derived batched barriers) with a Unity-URP-`ScriptableRendererFeature`-
style authored layer on top.

---

## 4. Sign-off

**Is the phase-3 DoD met?** YES — all five items (§1), each with a delivering commit + oracle evidence.

**Is the whole pass-graph migration (phase 1 + 2 + 3) done?** YES — §3. There is no further chunk in the
plan: V4 was the only remaining optional sub-layer and it was consciously declined (chunk 17). The
authored layer is the last named deliverable.

**Carried constraints, all honored:** Lumen GI algorithm untouched (DDGI/screen-probe/world-cache/DxrGi
are imported opaque nodes the graph schedules around); one-intent commits (move vs fix never combined);
GPU-hang safety absolute (RT_GI/RT_SHADOWS NOT exercised — pre-existing headless device-removal at the
readback, orthogonal, EXCLUDED from golden per `dx12-golden-set.json`; never relaunch-looped); golden +
GBV gates carried from phases 1+2.

**Chunk-23 verification run (this chunk, fresh, pinned substrate RX 9070 XT / driver 32.0.31019.2002):**

| gate | run | result |
|---|---|---|
| Build/DLL dance | `dotnet build DX12 → Runtime`, copy `BallisticEngine.DX12.dll` → Cli + Runtime bins | 0-err, fresh DLL in both bins (size+timestamp verified) |
| Deterministic golden (default) | `bal render` CornellBox / BistroInterior_Wine | `6e3ee554…` / `40a68b28…` — **== golden** |
| Deterministic golden (`GRAPH=1`) | `bal render` CornellBox / BistroInterior_Wine | `6e3ee554…` / `40a68b28…` — **== golden** (graph-neutral) |
| GBV 0-NEW | Runtime.exe direct, `DEBUG=1 GBV=1 BREAK_ON_ERROR=1`, CornellBox | exit 0, **`8 known, 0 NEW (0 error-class)`**, BMP produced, no device-removal |

Chunk 23 is doc-only — no renderer/engine/editor code changed — so golden is byte-identical BY
CONSTRUCTION; the 4/4 SHA==golden + GBV 0-NEW above is the confirming run the chunk mandates, not a
regression risk surface.

**PHASE 3 — and the entire DX12 pass-graph migration — is DONE and ACCEPTED.**
