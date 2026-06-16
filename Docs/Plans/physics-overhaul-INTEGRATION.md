# Physics Overhaul — Integration Handoff

**Branch:** `physics-overhaul` (isolated worktree at `e:/Unity Projects/Ballistic-Engine-physics-wt`)
**Status:** ALL 8 phases done, every phase test-green. Ready to merge into `dx12-renderer` when you say go.
**The renderer (`dx12-renderer`) was never touched.**

## How to integrate (when you're ready)

The work is 8 clean, self-contained commits on `physics-overhaul`. From your `dx12-renderer` worktree:

```
git merge physics-overhaul          # or cherry-pick commit-by-commit if you prefer
```

Expected conflicts: **none in renderer code.** At most trivial ones in:
- `.gitignore` / `BallisticEngine.csproj` glob (only if you changed them meanwhile) — none expected.
- No shared files with the renderer: all changes are under `Abstraction/Physics/`, `Physics/Bepu/`,
  `Engine/Physics/`, plus one demo scene under `SampleProject/Assets/Default/`.

Then rebuild the engine + exes (the editor/runtime pick up the new `[Component]`s automatically via
reflection — Rigidbody's neighbours in the Add-Component menu).

## The 8 commits

| # | Commit | What |
|---|--------|------|
| plan | `b423612b` | the plan doc |
| P1 | `d355f6bc` | **real restitution** — bounce calibrated to coefficient of restitution |
| P2 | `9d19fe1c` | **shape-cast (sweep)** + **Rigidbody.AddForceAtPosition** |
| P3 | `fba099e5` | **precise (narrowphase) overlap** queries |
| P4 | `f75ddc78` | **per-collider trigger** filtering (mixed solid+trigger on one body) |
| P5 | `76331499` | **child-entity compound colliders** + per-child contact resolution |
| P6 | `50311e56` | **joints/constraints** — BallSocket / Hinge / Fixed / Spring / Slider |
| P7 | `b500872f` | **vehicle demo** — WheelCollider + VehicleController + CarDemo.scene |

## Files added

- `Abstraction/Physics/IPhysicsConstraint.cs` — constraint interface + descriptor (P6)
- `Physics/Bepu/BepuConstraint.cs` — Bepu constraint wrapper (P6)
- `Engine/Physics/Joint.cs` — `Joint` base + `BallSocketJoint`/`HingeJoint`/`FixedJoint`/`SpringJoint`/`SliderJoint` (P6)
- `Engine/Physics/WheelCollider.cs` — arcade raycast suspension wheel (P7)
- `Engine/Physics/VehicleController.cs` — drives the chassis through its wheels (P7)
- `SampleProject/Assets/Default/CarDemo.scene` — drivable demo (P7)

## Files changed

- `Abstraction/Physics/IPhysicsWorld.cs` — `ShapeCast`, `OverlapShape`, `AddConstraint`/`RemoveConstraint`
- `Abstraction/Physics/IPhysicsBody.cs` — (unchanged signature; consumed by new code)
- `Abstraction/Physics/PhysicsShape.cs` — `PhysicsShapePart.IsTrigger` (per-shape, P4)
- `Physics/Bepu/BepuPhysicsWorld.cs` — restitution wiring, sweep, precise overlap, per-child triggers, constraints
- `Physics/Bepu/BepuCallbacks.cs` — restitution spring/velocity, per-child trigger narrowphase
- `Physics/Bepu/BepuContactTracker.cs` — peak-approach restitution, per-child event keying, child indices
- `Physics/Bepu/BepuBody.cs` — restitution velocity helpers
- `Engine/Physics/Rigidbody.cs` — `AddForceAtPosition`, child-collider gather, `InternalBody`, `ColliderForChild`
- `Engine/Physics/Physics.cs` — `SphereCast`/`BoxCast`/`CapsuleCast`, `OverlapSpherePrecise`/`Box`, child-resolved contact dispatch

## New public API (for game scripts)

```csharp
// Casts (Unity's SphereCast/BoxCast/CapsuleCast)
Physics.SphereCast(origin, radius, dir, out RaycastHit hit, maxDistance, layerMask);
Physics.BoxCast(center, halfExtents, dir, orientation, out hit, ...);
Physics.CapsuleCast(origin, radius, height, dir, orientation, out hit, ...);

// Precise overlap (narrowphase, no AABB false positives)
Physics.OverlapSpherePrecise(center, radius, layerMask);
Physics.OverlapBoxPrecise(center, halfExtents, orientation, layerMask);

// Forces
rigidbody.AddForceAtPosition(force, worldPoint);

// New components (appear in the editor Add-Component > Physics menu automatically):
//   BallSocketJoint, HingeJoint, FixedJoint, SpringJoint, SliderJoint
//   WheelCollider, VehicleController
// Colliders now support per-collider IsTrigger on the same Rigidbody, and child-entity colliders.
```

## The ONE intentional behaviour change (A/B it)

**P1 restitution.** Before: `Bounciness` was a soft-spring approximation that barely bounced
(measured: `Bounciness = 0.9` rebounded only ~0.28 of the drop height). After: it's a real
coefficient of restitution (`0.9` → ~0.76 rebound, matching `b²` energy, like Unity). Any scene
relying on the old weak bounce will now bounce correctly — this is a fix, but it's the one place
behaviour deliberately differs. Everything else (resting, stacking, friction, contacts, single-type
triggers, flat-body compounds) is byte-identical.

## Known limitations (documented, not bugs)

- **Solid compound contact resolution:** for a *solid* multi-collider body, a contact event resolves
  to the body's primary collider, not the exact child struck — Bepu's reduced manifold doesn't carry
  a reliable child index. *Trigger* child contacts DO resolve exactly (per-child narrowphase). (P5)
- **World-anchored joint runtime removal:** removing a world-anchored joint from a body that fell
  asleep balanced under it can leave it briefly frozen (a Bepu sleep-island edge case). Joints are
  normally torn down wholesale by `Reset()` at play-end, where this can't happen. (P6)
- **Vehicle is arcade**, not a Pacejka sim: linear tyre grip, load-clamped. Stable and tunable; not a
  racing-grade slip model. (P7)

## Verification

- **Headless physics suite:** 78/78 (`e:/tmp/bal-phys-overhaul`, compiles `Abstraction/Physics` +
  `Physics/Bepu` directly against this branch). Re-run: `cd e:/tmp/bal-phys-overhaul && dotnet run -c Release`.
- **Engine library** compiles clean (0 errors) at every phase.
- **Engine end-to-end** via `bal simulate` (real HeadlessRuntime: scenes + physics, no GL):
  child compound rests correctly, HingeJoint swings, the car settles + drives + steers.
- **No editor/GL launch** anywhere (GPU-hang safety + your renderer is active) — all verification headless.

## Visual confirmation (your call, post-integration)

Everything was verified numerically (headless). Once integrated into `dx12-renderer`, you can open
`CarDemo.scene` in the editor, press Play, and drive with WASD (Space = handbrake) to see it on screen.
