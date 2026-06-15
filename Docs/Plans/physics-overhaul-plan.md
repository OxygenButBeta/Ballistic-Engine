# Physics Overhaul Plan — close every v1 gap, finish with a vehicle controller

**Branch:** `physics-overhaul` (isolated git worktree at `e:/Unity Projects/Ballistic-Engine-physics-wt`)
**Constraint:** ZERO touches to `dx12-renderer`. All work stays on this branch/folder until the user says "integrate".
**Backend:** BepuPhysics 2.4.0 (already referenced) — every gap below is a feature Bepu already supports but we haven't wired.

---

## Goal

Take the physics layer from "solid v1 with known limits" to "production-complete", then ship a
**raycast (arcade) vehicle controller** demo on top of the new capabilities.

Current state (verified): BepuPhysics 2 is a real production engine; the integration is mature
(CCD, one-sided mesh, kinematic chase, lock-free contact tracking, substep solver). The gaps are
all bounded "v1" cut-lines, honestly marked in code. We close them in dependency order.

---

## The five gaps + the demo (in build order)

| # | Gap | Where | Risk |
|---|-----|-------|------|
| **P1** | **Real restitution** (Bounciness is a spring approximation) | Backend only — no API change | Low |
| **P2** | **`AddForceAtPosition` + sweep/cast queries** (raycast vehicle needs both) | Rigidbody API + `IPhysicsWorld` | Low |
| **P3** | **Precise (non-AABB) overlap queries** | `IPhysicsWorld` + backend | Med |
| **P4** | **Per-collider trigger filtering** (mixed trigger/solid on one body) | `PhysicsShapePart` + backend compound | Med |
| **P5** | **Child-entity compound colliders** + per-child contact resolution | `Rigidbody` + backend child-map | High |
| **P6** | **Joints / constraints** (Hinge, BallSocket, Fixed/Weld, Spring/Distance, motors+limits) | new `IPhysicsConstraint`, `Joint` components, backend | High |
| **P7** | **Vehicle controller demo** (raycast suspension wheels + drive/steer/brake) | new components + demo scene | Med |

Each phase ends **green on the headless test suite + a fresh A/B regression check** before the next
starts. Existing scenes must stay byte-identical where untouched (engine's never-regress rule).

---

## Phase P0 — test scaffolding FIRST (before any feature)

Per the project's hard-won lesson ("isolated correctness harness BEFORE GPU/engine integration"),
stand up verification before writing features.

- **Resurrect + extend the headless physics suite.** The 37-check suite lives at `%TEMP%/bal-phys-test`
  (a scratch console that compiles `Abstraction/Physics/*.cs` + `Physics/Bepu/*.cs` directly with a
  stubbed `Debugging`). Rebuild it in-repo-adjacent under `e:/tmp/bal-phys-overhaul/` so it survives,
  re-run the existing 37 checks to establish a green baseline, then add a numbered check per new
  feature as we go (restitution energy ratio, sweep hit distance, precise-overlap reject, per-child
  trigger, child compound mass/inertia, each joint's constraint behaviour).
- **Wire `bal simulate` smoke scenes.** Confirm the REAL-engine headless path
  (`BallisticEngine.Cli SimulateCommand` → `HeadlessRuntime` → `Physics.Advance`) drives each new
  feature end-to-end (component → backend) deterministically (two runs byte-identical).
- **Baseline capture:** record the current 37-check pass + one `bal simulate` time series on an
  existing physics scene, so every later phase diffs against a known-good.

**Exit:** 37/37 green in the new harness; baseline time series saved.

---

## Phase P1 — Real restitution (lowest risk, backend-only)

**Problem:** `Bounciness` is implemented as an undamped contact spring (frequency `30−25*b`,
damping `1−b`). It's a hack — true coefficient-of-restitution is never applied.

**Fix:**
- Add `float Restitution` to `ContactMaterial` (keep `Bounciness` as an alias that maps in, for
  back-compat with existing `.scene`/inspector values → no migration).
- In `BepuCallbacks.ConfigureContactManifold`, compute the pair restitution (`max` of the two
  materials, Unity's default combine) and apply it as a **target bounce velocity** on the contact
  (Bepu exposes per-contact `MaximumRecoveryVelocity` + the spring settings; set the spring to
  near-rigid and inject the restitution as a velocity bias rather than softening the spring).
- Keep the speculative-margin interaction correct: restitution must not reintroduce tunneling
  (test fast vertical drop at b=0.9 lands within energy tolerance, no pass-through).

**Verify:** drop a ball from height h with restitution r; rebound height must be ≈ r²·h (energy
ratio check) across r ∈ {0, 0.3, 0.6, 0.9}. Existing b-value scenes stay visually equivalent
(documented: behaviour *improves* — this is the one intentional non-byte-identical change; gated/
noted so the user can A/B it).

**Exit:** restitution energy check passes; no tunneling; existing scenes still rest correctly.

---

## Phase P2 — `AddForceAtPosition` + sweep/cast queries (vehicle prerequisites)

The arcade vehicle needs (a) force at a wheel contact point and (b) shape-casts for robust
ground-finding. Both are thin Bepu wrappers.

- **`Rigidbody.AddForceAtPosition(Vector3 force, Vector3 worldPoint)`** — accumulate into the
  pending-force path (decompose into linear + torque about COM, applied in `PrePhysicsStep` as an
  impulse like the existing `AddForce`). Also add `AddForceAtPosition`'s impulse sibling.
- **Sweep / shape-cast** on `IPhysicsWorld`:
  `bool SphereCast(origin, radius, direction, maxDistance, layerMask, out PhysicsRayHit)` and a
  general `bool ShapeCast(PhysicsShape convex, pose, direction, maxDistance, layerMask, out hit)`.
  Backend: `Simulation.Sweep<T>(...)` with a layer-filtering `ISweepHitHandler`.
- **`Physics` facade** overloads (`Physics.SphereCast`, returns `RaycastHit`) mirroring `Raycast`.

**Verify:** sphere-cast against a known plane returns the exact contact distance; force-at-position
on an off-center point induces the correct angular response (torque sign + magnitude check).

**Exit:** sweep + force-at-position checks pass; raycast path unchanged (byte-identical).

---

## Phase P3 — Precise overlap queries

**Problem:** `OverlapSphere`/`OverlapBox` are broadphase-AABB with a loose center refine — false
positives near box corners / rotated boxes.

**Fix:**
- Add a `bool precise` path (default keeps current cheap behaviour for back-compat) OR a new
  `OverlapShape(PhysicsShape, pose, layerMask, results)` that runs Bepu's narrowphase
  `Simulation.Overlap`-style convex-vs-world test (GJK/distance) to confirm true intersection.
- Sphere precise = exact closest-point ≤ radius; box precise = SAT/GJK against candidate shapes.

**Verify:** a body whose AABB overlaps the query box but whose *shape* does not is **rejected** in
precise mode and **accepted** in the legacy mode (proves both paths). Existing `Physics.OverlapSphere`
callers default to legacy → byte-identical.

**Exit:** precise reject/accept check passes; default callers unchanged.

---

## Phase P4 — Per-collider trigger filtering

**Problem:** trigger state is per-BODY; mixed trigger+solid colliders on one Rigidbody warn and
stay solid.

**Fix:**
- Add `bool IsTrigger` to `PhysicsShapePart` (per-shape).
- Backend: when building a compound, track per-child trigger flags; in
  `NarrowPhaseCallbacks.ConfigureContactManifold`, use the *child index* to decide solve-vs-overlap
  per contact (Bepu gives `childIndexA/B` — currently stubbed). Contact events tag the right child.
- `Rigidbody.CreateBody` stops collapsing to all-trigger/all-solid; remove the "stay solid" warning.

**Verify:** one entity with a solid box + a trigger sphere; a passing body gets `OnTriggerEnter`
from the sphere **and** `OnCollisionEnter` from the box in the same setup. Pure-trigger and
pure-solid bodies behave exactly as before (byte-identical).

**Exit:** mixed trigger/solid check passes; single-type bodies unchanged.

---

## Phase P5 — Child-entity compound colliders (highest structural risk)

**Problem:** `Rigidbody.CreateBody` only gathers colliders on its OWN entity; child colliders are
ignored. Real characters/vehicles want a body with colliders spread across child transforms.

**Fix:**
- `CreateBody` walks the child hierarchy (stopping at any nested Rigidbody — that's its own body),
  baking each child collider's transform **relative to the body root** into
  `PhysicsShapePart.LocalPosition/LocalRotation` (compose the ancestor chain, divide out root scale
  consistently with the existing single-entity scale handling).
- Maintain a `childColliderMap` (compound child index → originating `Collider`) so contact events
  resolve to the *actual* child collider, not just `PrimaryCollider`. Extend `BepuBody` to carry the
  map; `Physics.DispatchContactEvents` uses the child index from the contact.
- Edge cases: a child collider toggled at runtime triggers `NotifyColliderChanged` on the root body
  (rebuild preserving velocity, like the existing same-entity path); don't double-count a child that
  has its own Rigidbody.

**Verify:** an L-shaped body (box on root + box on a child offset) computes the correct combined
mass/inertia (tips the expected way under gravity); a contact on the child fires with the child's
collider as `Collision.Collider`. Single-entity bodies stay byte-identical.

**Exit:** child-compound mass/inertia + child-contact-resolution checks pass; flat bodies unchanged.

---

## Phase P6 — Joints / constraints

The big one. Bepu has all of these; we add an abstraction + components.

**Abstraction (`Abstraction/Physics/`):**
- `IPhysicsConstraint` (BodyA, BodyB, IsEnabled, RemoveSelf, parameter update).
- `IPhysicsWorld.AddConstraint(...)` / `RemoveConstraint(...)`; a world-anchor body for "joint to
  the world" (null connected body).

**Backend (`Physics/Bepu/`):**
- `BepuConstraint` wrapper over `ConstraintHandle`; map each engine joint to its Bepu struct via
  `Simulation.Solver.Add(...)`:
  - **HingeJoint** → `Hinge` (+ optional `AngularAxisMotor`, `SwingLimit`/`TwistLimit`)
  - **BallSocketJoint** → `BallSocket` (+ optional `SwingLimit`)
  - **FixedJoint** → `Weld` (rigid attach)
  - **DistanceJoint / SpringJoint** → `DistanceLimit` / `DistanceServo` (spring via servo settings)
  - **SliderJoint** → `PointOnLineServo` (+ `LinearAxisMotor`/`LinearAxisLimit`)
- Constraint lifetime mirrors bodies: removed on `Reset()`, re-added on play.

**Components (`Engine/Physics/`):**
- `abstract class Joint : Behaviour` — `ConnectedBody` (Rigidbody, null = world), `AnchorA/AnchorB`,
  break-force (optional), lifecycle creates/destroys the constraint in `OnEnabled`/`OnDisabled`
  (play-mode only, same pattern as Rigidbody). `[Component(... "Physics")]` so the editor's Add
  menu + inspector pick them up with zero wiring.
- Concrete: `HingeJoint`, `BallSocketJoint`, `FixedJoint`, `SpringJoint`, `SliderJoint`. Each exposes
  axis/limits/motor target via attributes (`[Range]`, `[ShowIf]` to hide motor fields when motor off).
- Editor gizmos (anchors + axis), reusing the existing collider-handle/gizmo patterns.

**Verify (one numbered check per joint):** hinge holds a swinging door about its axis only; ball
socket holds a pendulum at fixed distance; weld keeps two boxes rigid; spring oscillates to rest at
RestDistance; slider confines motion to the line + a motor drives it. Each is a `bal simulate` time
series (deterministic, byte-identical across two runs).

**Exit:** all five joints pass their behaviour checks; existing scenes (no joints) byte-identical.

---

## Phase P7 — Vehicle controller demo (raycast / arcade)

The payoff demo, built ENTIRELY on P1–P6 (mainly P2's force-at-position + sweep).

- **`WheelCollider` component** — raycast/sphere-cast suspension spring per wheel:
  `Radius`, `SuspensionTravel`, `SuspensionStiffness`, `SuspensionDamping`, `Friction`,
  `[NotSerialized]` runtime readouts (`IsGrounded`, `Compression`, `ContactPoint`, `RPM`). In
  `FixedTick`: cast down from the wheel mount, compute spring + damper force, apply via
  `Rigidbody.AddForceAtPosition` at the contact; lateral grip force opposes sideways slip;
  longitudinal force from drive/brake torque. Editor gizmo draws the spring + contact.
- **`VehicleController` component** — owns the chassis Rigidbody + N `WheelCollider`s (front =
  steerable, rear = driven, configurable). Reads `Input` (throttle/brake/steer), distributes motor
  torque to driven wheels, steers fronts, applies handbrake. Tunables: top speed, accel curve
  (`AnimationCurve` — already a serializable primitive in the engine), steer angle vs speed,
  downforce.
- **Demo scene** (`SampleProject/Assets/...` under the gitignored test area or a tiny self-contained
  scene): a flat ground + ramp + a 4-wheel car, drivable in play mode. A short scripted-input
  `bal simulate` run proves it drives forward deterministically (sanity, not graphics).

**Verify:** `bal simulate` scripted throttle → chassis travels forward a expected distance over N
steps, stays upright (no flip), wheels report grounded on flat ground. Visual confirm is deferred to
the user post-integration (renderer is busy; we don't launch the GL/DX editor here per the GPU-hang
safety rule).

**Exit:** vehicle drives in headless sim deterministically; all prior checks still green.

---

## Cross-cutting rules (every phase)

1. **Isolation:** never touch the `dx12-renderer` worktree. All commits land on `physics-overhaul`.
2. **Never-regress:** untouched scenes stay byte-identical; P1 restitution is the single documented
   intentional behaviour change (gated/notable so the user can A/B it).
3. **Layering:** `Physics/Bepu/` stays the ONLY place referencing BepuPhysics; engine talks through
   `Abstraction/Physics/*` only (auditable by grep, per CLAUDE.md).
4. **No per-frame reflection** in any hot path (standing pref) — resolve at body/constraint creation.
5. **Test door per feature:** the headless suite grows by ≥1 numbered check per gap closed; green
   before moving on.
6. **No editor/GL launch** for verification (GPU-hang safety + renderer is active) — use the headless
   harness + `bal simulate` exclusively. Visual confirm happens at integration time, by the user.
7. **Commit cadence:** one focused commit per phase (P0…P7), each self-contained + test-green, so the
   eventual integration into `dx12-renderer` can be cherry-picked or merged cleanly.

---

## Integration handoff (when the user says "integrate")

- Deliver as the `physics-overhaul` branch (clean, phase-by-phase commits) ready to
  `git merge`/`cherry-pick` into `dx12-renderer`.
- Provide a one-page `INTEGRATION.md`: files added/changed, the one intentional behaviour change
  (P1 restitution), the new components + Add-menu entries, and the demo scene path.
- No renderer files touched → merge conflicts limited to (at most) `CLAUDE.md` physics notes +
  `BallisticEngine.csproj` glob (both trivial).

---

## Build order summary

`P0 harness → P1 restitution → P2 force-at-pos+sweep → P3 precise overlap → P4 per-collider trigger
→ P5 child compounds → P6 joints → P7 vehicle demo`

Each gated on green headless tests + a regression A/B. The vehicle is the capstone that exercises the
new force/sweep/joint surface end-to-end.
