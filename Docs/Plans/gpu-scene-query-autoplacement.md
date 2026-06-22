# GPU Scene-Query Layer & Deterministic Auto-Placement

**Status:** DESIGN / PLANNED (not yet implemented). Captured 2026-06-15.
**Why this doc exists:** the engine's thesis is *comfortable development + easy good graphics + AI-operability*,
for a Windows/PC indie/newcomer audience. Manual spatial authoring chores (light-probe placement, reflection-
probe placement, lightmap UVs, occlusion cells, navmesh) are exactly the kind of Unity/Unreal friction we
refuse to inherit. This doc records HOW we eliminate/automate them correctly — and why the naive approach fails.

---

## 1. The category insight (the framing that drives everything)

Auto-placement is **not an AI problem.** Assigning it to an LLM/agent is a category error — it's slow,
non-deterministic, and worse than a good algorithm. Every "annoying authoring chore" sorts into three tiers,
and we ask them **in order**:

1. **ELIMINATE** — change the runtime so the cache/artifact isn't needed at all. *(Best.)*
   - Light probes / APV / lightmaps exist only because real-time GI used to be too expensive. They are a
     2010s precompute cache. We already compute GI in real time (voxel cone tracing default-on, mesh-SDF,
     GPU JFA, SSGI, DXR RT GI). **So "probe placement" should not exist in this engine by default.** Do NOT
     import Unity's probe model just because Unity has it.
2. **ALGORITHMICALLY AUTO-DERIVE** — deterministic code, GPU-accelerated, *invisible and tuning-free*.
   - When a cheap GI fallback IS needed (low-end / no-RT GPUs — see §5), probes are placed by an ALGORITHM,
     never by a human and never by the AI. Placement is a geometry problem with known solutions.
3. **AI FOR INTENT ONLY** — reserve the AI agent for tasks whose "correct" answer requires understanding
   what the human *wants* (mood/art-direction, "assemble this scene from a description", gameplay feel).

**The dividing line:**
> If the task's answer is derivable from geometry → ALGORITHM. (probes, navmesh, lightmap UVs, LODs,
> occlusion cells, audio occlusion, cover points.)
> If the task needs human intent → AI.

**The APV lesson (make it a hard rule):** APV is hated because it is *automation with a tax* — it auto-places
but still forces brick-density / dilation / sky-occlusion / bake-time knobs. If we automate, the automation
must be **invisible and have ZERO required tuning**, or we've just rebuilt APV. The day someone adds the first
"adjust probe density" slider to the front door, we've started repeating Unity's mistake. Keep auto-placed
artifacts hidden (no Add-menu entry, always auto-fit) — the current probe/reflection approach is correct
*as long as it stays tuning-free*.

---

## 2. Why naive (AABB / uniform grid) placement fails — and the right tool

AABB / uniform-grid placement **discards the very thing the problem is about: the interior structure.** An
AABB knows only the bounding box, not solid-vs-empty-vs-visible. In interiors it:
- puts probes *inside* walls/solid geometry,
- leaks light between rooms (a probe in room A interpolates into room B through the wall),
- can't distinguish open space from enclosed space,
- wastes density (uniform grid is wrong — you want density driven by geometry detail).

The fix requires **actual geometry queries**: "is this point inside solid? how far to the nearest surface?
what can it see? which other points share visibility (= same room)?" Those are answered by **GPU ray casting
+ a signed distance field** — NOT by bounding boxes.

---

## 3. The core idea: reuse what we already built

We do NOT build a new system. The acceleration structures built for *rendering* are dual-use — they are the
engine's **spatial-understanding substrate**:

- **TLAS / BLAS** (`BallisticEngine.DX12/Resources/Dx12SceneAS.cs`, built for RT GI/shadows/reflections):
  answers "cast a ray from P in direction D — what/where does it hit?" → visibility, occlusion, leak detection,
  open-vs-enclosed classification.
- **SDF — global distance field / GPU JFA** (the Lumen work, `BALLISTIC_LUMEN_JFA`): answers "for point P,
  distance to nearest surface + inside/outside" → instant occupancy test and surface-nudge.

**Plan: wrap TLAS + SDF in one thin "GPU Scene Query" API and route ALL placement/occupancy/visibility tasks
through it.** Build once, use everywhere.

---

## 4. The placement recipe (perf-conscious ordering)

1. **SDF cheap cull (first):** sample the SDF at every candidate position in a compute pass. Reject candidates
   inside solid (SDF < 0); nudge candidates too close to a surface outward along the SDF gradient. This culls
   millions of candidates in ~ms so we never waste rays on dead positions.
2. **Ray refine (second, only on survivors):** cast short rays (TLAS) from survivors to:
   - **classify space** — most rays hit nearby ⇒ tight/interior; most miss ⇒ open ⇒ lower density.
   - **prevent leaks** — build *visibility clusters* (≈ rooms): two points that can't see each other must not
     be interpolation neighbours.
3. **Adaptive density:** more probes where the SDF gradient changes fast (corners, room boundaries, detailed
   geometry), fewer in open/uniform space. Computed from the SDF on GPU. Kills the uniform-grid waste.

**Even better (consider): surface seeding instead of a grid.** Place probes/surfels *on* surfaces (from the
G-buffer or by rasterizing the scene), so the probe set follows geometry by construction and the "inside a
wall" problem cannot occur — the DDGI / Lumen-surface-cache approach.

---

## 5. Hardware reality (Windows/PC, but PC is not one GPU)

The "eliminate probes via dynamic GI" answer is only valid where dynamic GI runs acceptably. Dev hardware is
a bleeding-edge RX 9070 XT (RDNA4) — **far above the audience floor** (indie/newcomer ≈ GTX 1660 / RTX 3060,
many with no hardware RT). Therefore:
- **Default (capable GPU):** dynamic real-time GI ⇒ no probes to place ⇒ chore eliminated (Tier 1).
- **Fallback (low-end / no-RT):** a cheap baked/probe GI path whose probes are placed by §4 — **invisible,
  algorithmic, tuning-free** (Tier 2). The GPU Scene Query layer is what makes that fallback actually work in
  interiors. Design the fallback *now*, not in a later panic.
- Test on a modest GPU early; do not let the dev card become a golden-path trap.

---

## 6. Dual-use leverage (why this layer is worth it)

The same GPU Scene Query layer (TLAS + SDF) serves, beyond GI probes:
- reflection-probe placement
- **audio occlusion** (muffle sound through walls)
- navmesh / AI cover-point sampling
- occlusion-culling cells
- "where can the player stand / what can be seen from here"
- **the AI agent's "eyes"** — when the agent needs spatial intent ("put cover here", "light this room warmly")
  it queries the *same* layer. So this substrate powers BOTH deterministic auto-placement (Tier 2) AND gives
  the AI 3D-structure awareness (Tier 3). One build, double leverage.

---

## 7. Practical constraints (carry forward)

- **It's a bake / async / incremental step, NOT per-frame.** Placement happens at scene-load / scene-edit, so
  the per-candidate ray budget is generous (thousands of rays). For the "comfortable dev" promise: run it
  **async** (reuse the existing "non-blocking sky-primed bake" pattern), **incremental** (re-place only probes
  near edited geometry), and **cached**.
- **Determinism is mandatory.** The whole verify harness is byte-identical-based; GPU rays + random sampling
  are non-deterministic. Use **fixed sample patterns** (exactly like the JFA "7-ray-parity shell seeds",
  "two runs byte-identical"). The discipline was already solved once in the JFA work — reuse it.
- **Ordering = SDF-cull-then-ray** (cheap filter before expensive refine), per §4.

---

## 8. Concrete next steps (when picked up)

1. Define a `GpuSceneQuery` API over `Dx12SceneAS` (TLAS) + the global SDF: `OccupancyAt(p)` (SDF sample),
   `NudgeToFreeSpace(p)`, `Visibility(a,b)` / batched ray casts, `ClassifySpace(p)` (open/enclosed),
   `VisibilityClusters(points)` (room segmentation). All compute, deterministic sample patterns.
2. First client: the **low-end GI fallback probe placement** (SDF cull → ray refine → adaptive density →
   leak-aware visibility clusters), invisible + tuning-free, async + incremental.
3. Then fan out to reflection probes, audio occlusion, navmesh sampling — same API.
4. Expose a minimal query surface to the AI agent layer (Tier 3 intent tasks).

**Guiding rule for every future authoring chore:** ask (1) can I eliminate it? (2) can I auto-derive it
deterministically (via GPU Scene Query)? (3) does it truly need human/AI intent? — in that order. Almost all
of Unity's hated chores live in tiers 1–2; we are only forced into them by legacy + generality, neither of
which we carry.
