# Ballistic Engine — AI-Native Master Plan

**Status:** NORTH-STAR / STRATEGIC. Captured 2026-06-15. This is the umbrella doc that ties together the
engine's reason-to-exist and the concrete tracks under it. Sub-plans: [GPU Scene-Query &
Auto-Placement](gpu-scene-query-autoplacement.md), the DX12 migration (`DX12Migration.md`), the Lumen GI
roadmap. Read this first for *why*; the sub-plans for *how*.

---

## 0. The bet, in one paragraph

Build a **simple, elegant, powerful engine for individuals and newcomers**, **Windows/PC only**, whose
differentiators are **comfortable development + easy good graphics + performance + first-class AI support**.
The strategic wager: the engines that win the next decade are the ones an **AI agent can drive end-to-end**,
and that is precisely the incumbents' biggest gap (Unreal is excellent but AI-hostile; Unity is broad but
buried in legacy and "appeal-to-everyone" tax; Godot is approachable but weak at high-end 3D graphics). We
aim at the empty quadrant: **easy + great graphics + AI-operable**, for the audience that benefits most from
AI help — beginners.

---

## 1. Strategic positioning (the focusing decisions)

The whole strategy is **subtracting axes of complexity that don't serve the core.** Each subtraction is a
deliberate "no" that the incumbents never said:

- **No AAA breadth.** Most Unity/Unreal deep pipelines (Addressables, etc.) are barely touched by indie/mid
  devs. We do not chase them. "Production-ready" here = *a beginner ships a good-looking PC game with AI help*,
  not "matches Unreal feature-for-feature."
- **No cross-platform.** Windows/PC only ⇒ **DX12/DXR is the native, correct choice** (no Vulkan/Metal
  abstraction tax). Xbox is later "almost free" (DX12 = Xbox API); Steam is the distribution answer (a working
  `.exe`, no console cert). This single decision collapses a huge slice of the long-tail edge cases.
- **No human-only depth.** Depth exists but is *opt-in and AI-reachable* (see §2). Unity's sin was never
  *having* depth — it was shoving it in your face, badly defaulted, with the simple path un-carved.
- **AI is the moat, not a feature.** It must live in the architecture's DNA (it does — `bal` CLI, MCP, live
  pipe, headless sim/render). Incumbents can't bolt this on: their depth was designed for *humans to learn*,
  so it isn't agent-drivable.

**The one axis we can't fully subtract even on PC: GPU hardware variance.** Dev hardware (RX 9070 XT / RDNA4)
is far above the audience floor (≈ GTX 1660 / RTX 3060, many with no hardware RT). Every "easy good graphics"
promise must hold on a modest, no-RT GPU. The fallback path must be as good as the golden path. Test on a
modest GPU early; never let the dev card become a golden-path trap.

---

## 2. The automation doctrine (how we beat the authoring-chore tax)

Manual spatial authoring (light/reflection probes, APV, lightmap UVs, occlusion cells, navmesh) is exactly the
Unity friction we refuse to inherit. Every "annoying chore" sorts into three tiers, asked **in order**:

1. **ELIMINATE** — change the runtime so the cache/artifact isn't needed. *(Probes exist only because GI was
   once too expensive; we compute GI in real time ⇒ probe placement should not exist by default.)*
2. **ALGORITHMICALLY AUTO-DERIVE** — deterministic, GPU-accelerated, **invisible and tuning-free**. *(When a
   cheap GI fallback IS needed for low-end GPUs, probes are placed by an algorithm — never a human, never the
   AI. See [GPU Scene-Query & Auto-Placement](gpu-scene-query-autoplacement.md).)*
3. **AI FOR INTENT ONLY** — reserve the agent for tasks whose correct answer requires understanding what the
   human *wants* (mood, art direction, "assemble this scene from a description", gameplay feel).

**The dividing line:** answer derivable from geometry → ALGORITHM (probes, navmesh, UVs, LODs, occlusion,
audio occlusion, cover). Answer needs human intent → AI. Putting a geometry problem on the AI is a category
error — slower, non-deterministic, worse than a good algorithm.

**The APV anti-pattern (hard rule):** never ship *automation with a tax*. APV auto-places but still forces
density/dilation/sky-occlusion/bake knobs — so it doesn't free you, it mystifies the work. If we automate, it
is **invisible with ZERO required tuning**. The first "adjust probe density" slider on the front door = we've
started becoming Unity. Auto-derived artifacts stay hidden (no Add-menu entry, always auto-fit).

---

## 3. Data architecture (queryable WITHOUT giving up text)

**Source of truth = human/AI/git-friendly text (YAML). Derived index = DB-like, rebuildable, gitignored.**
Never conflate them; never make a binary DB the authority.

- **Why text stays the authority:** git diff/merge/code-review; one-member edit = one-line diff; human + AI
  debuggability; zero infra (no migrations/locking/corruption). And — counterintuitively — **text serves
  AI-operability BETTER**: every AI edit is an auditable one-line diff you can verify/revert. An opaque DB
  destroys that audit trail, which is the thing that makes trusting an autonomous agent possible.
- **The AI never greps strings.** Queryability is a property of the *API/index layer*, not the storage
  format. The scene loads into a typed object graph (Entity/Component) — already queryable. The agent queries
  the live model via `bal scene find/get`, reflection schema, MCP, the command port. The string is touched
  only at load/save.
- **Where DB-like fits (as a derived cache, never authority):** the existing `Library/ArtifactDB.json` is the
  right pattern (binary metadata + GUID cache, gitignored, rebuildable). For large projects, add a persistent
  **query index** over the YAML for fast cross-scene queries / reverse-refs ("all entities with component X",
  "all references to this asset") — rebuilt from the text source, never the authority.
- **Shipping build exception:** the packaged game may use a fast binary/streamable format. That's a
  *distribution* format, not the *authoring* format. Authoring stays text.

---

## 4. The spatial substrate (the engine's "understanding" layer)

Full design in [GPU Scene-Query & Auto-Placement](gpu-scene-query-autoplacement.md). Summary of why it's
central: the acceleration structures built for *rendering* are **dual-use** — they are also the engine's
spatial-reasoning substrate, and the AI's eyes.

- **TLAS/BLAS** (`Dx12SceneAS`, built for RT GI/shadows/reflections) → ray queries: visibility, occlusion,
  leak detection, open-vs-enclosed classification.
- **SDF / global distance field** (the Lumen / GPU-JFA work) → instant occupancy ("inside solid?") and
  surface-nudge.

Wrap both in ONE thin **`GpuSceneQuery`** API and route ALL spatial tasks through it. **This is the single
highest-leverage investment in the plan**, because it pays off three times:
1. deterministic auto-placement (Tier 2: probes, reflection probes, audio occlusion, navmesh, cover, occlusion
   cells) — finally works in interiors (AABB can't),
2. the AI agent's spatial perception (Tier 3 intent) — turns the agent from "screenshots and guesses" into
   "understands the 3D world",
3. one build, used everywhere.

Constraints: it's a bake/async/incremental step (not per-frame), SDF-cull-then-ray ordered, and **deterministic
via fixed sample patterns** (reuse the JFA "7-ray-parity seeds" discipline — byte-identical runs).

---

## 5. The AI operability surface — current state → frontier

**Current (strong on STRUCTURE + VERIFICATION):**
- `bal` CLI: `map`, `schema` (reflection — never guess members), `scene get/set/find/add/remove`,
  `validate/describe/import`, `assets refs`, `simulate` (real headless engine: scripts+physics, numeric time
  series, deterministic input), `render`+`imgdiff` (deterministic capture + perceptual diff).
- Live editor: named pipe / MCP (16 tools) — entity/component CRUD, select, play control, screenshot, frame,
  console tail, undo, scripts rebuild. Mutations push EditorUndo (auditable, reversible, behave like human edits).
- Verification harness: `BALLISTIC_SCREENSHOT(_PAUSED/_DETERMINISTIC)`, `BALLISTIC_IDMAP` (occlusion-aware
  "what is on screen where"), `.stats.json` sidecars, `engine.jsonl` logs.
- **The standout: the agent can verify its own work objectively** (render→diff, simulate→numbers) — most
  engines leave the AI blind. This is already real.

**Frontier (the gaps to close, prioritized):**
1. **Spatial/semantic perception (#1 leverage).** Expose `GpuSceneQuery` (§4) to the agent + raw G-buffer
   readout (depth/normal/albedo, not just the final tonemapped pixel). Turns "I see pixels and guess" into "I
   can ask the world: is the spawn inside geometry? is this light occluded? do these meshes interpenetrate?"
2. **Intent-level verbs** on top of that perception (Tier 3): "place N props naturally on this floor", "light
   this room warmly", "fix interpenetration", "assemble a scene from this description."
3. **Live runtime introspection + behavioral test harness.** Read any component's live values during play;
   watch expressions; behavioral assertions ("entity X reaches Y within N steps") so the agent verifies
   *gameplay*, not just static structure/rendering.
4. **Asset preview to the agent.** Expose ThumbnailCache / preview renderers so the agent can *see* which
   `.mat` is wooden / which model is a crate — intelligent asset selection.
5. **Structured perf query.** Per-pass GPU ms / draw counts / memory as a queryable surface (beyond
   `.stats.json`) so the agent does autonomous perf work (mandated).

---

## 6. Roadmap (sequenced; leverages the now-built DX12 foundations)

**Track A — finish the migration (active, see `DX12Migration.md`):** editor ImGui→DX12 → delete GL +
OpenTK.Mathematics→System.Numerics + OpenAL. Prerequisite for a single clean codebase.

**Track B — the AI-native frontier (this plan), built on the DX12 RT/SDF foundations that now exist:**
1. `GpuSceneQuery` API over `Dx12SceneAS` (TLAS) + the global SDF — occupancy, visibility, space-classify,
   visibility-clusters. Deterministic. *(Foundation for everything below.)*
2. First client: low-end **GI fallback probe placement** — invisible, algorithmic, tuning-free, async +
   incremental (SDF-cull → ray-refine → adaptive density → leak-aware clusters).
3. Expose `GpuSceneQuery` + raw G-buffer to the agent (perception) — frontier #1.
4. Intent verbs (frontier #2) on the perception layer.
5. Runtime introspection + behavioral assertions (frontier #3).
6. Asset preview + structured perf query to the agent (frontier #4/#5).
7. Fan the scene-query layer out to audio occlusion, navmesh, cover, occlusion cells.

**Track C — robustness for the audience (the "boring breadth" newcomers actually need):** teaching error
messages, a carved simple path, runtime game UI, a real audio mixer, animation maturity, Steam packaging —
the long tail that makes a beginner *not quit*. Driven by real usage, prioritized by what blocks shipping.

---

## 7. Doctrine (the guardrails that keep us from becoming Unity)

- **Progressive disclosure:** simple by default, depth opt-in and AI-reachable. Someone/something must say
  "no" to every feature that tries to creep onto the front door — Unity's mess accrued because nobody did.
- **Good defaults > knobs.** The art of "easy good graphics" is defaults that are both beautiful and fast,
  not exposing tuning. (Already practiced: Volume framework, collider auto-fit, GPU-driven default-on + CPU
  fallback.)
- **Source of truth = text; derived index = cache.** Never invert. AI edits stay auditable diffs.
- **Eliminate > algorithmic-auto > AI-for-intent**, always in that order.
- **Automation must be invisible & tuning-free** (the APV anti-pattern).
- **Determinism is non-negotiable** — the whole verify harness depends on byte-identical reproducibility.
  Fixed sample patterns for any GPU stochastic work.
- **Fallback path = golden path** in quality. Test on modest hardware early.
- **The agent must be able to verify its own work** — every new system ships with a query/measure surface, or
  it isn't done.

---

## 8. The litmus test for "production-ready" (for THIS engine, THIS audience)

> *Can a beginner, without learning any complex pipeline, with the AI's help, finish a good-looking PC game
> end-to-end — and can the AI verify its own work along the way?*

If yes, it's production-ready — Addressables-equivalents and AAA breadth are irrelevant. Everything in this
plan serves that one sentence.

---

## 9. Execution discipline (HOW the work chats run — applies to every track)

These rules make an autonomous /loop chat *safe*, not just productive. Every launch prompt references this.

- **Research → propose → approve, before any HARD or AMBIGUOUS design.** For a non-trivial subsystem (the GI
  surface cache, the GpuSceneQuery API shape, an auto-placement heuristic), the agent must first research the
  approach (published techniques) and **post a short design proposal + CHECK IN**, before implementing. Never
  autonomously build a large design down one path — a wrong path costs hours.
- **Milestone cadence.** Decompose each chat into small milestones (M0…Mn). Each milestone = build + verify +
  **commit** + a one-paragraph summary WITH EVIDENCE (the screenshot/diff/numbers). Granular commits = every
  milestone is a clean resume point.
- **Stop-if-ambiguous valve.** If a major design decision or a blocker is ambiguous, **STOP and ask** — do not
  guess and barrel ahead. Autonomy with a safety valve.
- **Evidence over claims — never declare "done" without the measurement.** Especially GI: a phase is "done"
  only when the GI-isolate A/B in an enclosed interior proves it. ("Lumen done" was claimed before and was
  patlak — the measurement harness exists precisely to prevent re-declaring that.)
- **Multi-session by design.** These tracks are too big for one chat (GI alone is 8 phases). Expect to launch
  continuation chats. Each milestone-commit + the plan doc + the memory note IS the handoff; update them as you
  go so the next chat resumes cleanly.
- **GPU-hang safety ([[gpu-hang-launch-safety]]):** on any device-removal STOP, commit safe, diagnose with
  DRED (`BALLISTIC_DX12_DRED=1`) WITHOUT relaunching the hanging build, verify headless. A TDR has hard-crashed
  the PC before.
- **One branch, sequential.** All renderer-side chats are on `dx12-renderer` and touch shared files (esp.
  `DX12HDRenderer.cs`) → run sequential, each starts after the previous commits. (Editor-UI-side work, e.g. the
  AI panel, may parallelize.)
