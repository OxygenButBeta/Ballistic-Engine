# Gameplay Framework — ITEM 0 GATE (mechanism + proof)

**Status:** GATE ✅ (f71d9b9d) + **P0 ✅** (f06d831a) + **P1 ✅** (b855239a) + **P2 ✅ IMPLEMENTED &
VERIFIED.** The mechanism this doc settled is ported into the engine and proven against shipped code, now
through P2 (source generator + `[Networked]` bit-packed delta state + asymmetric send-rate).

**Verify — in-engine headless harness `%TEMP%\bal-gameplay-test` (GameplayP0.csproj, ProjectReference to the
engine; drives the REAL Behaviour.FireEnable / GamePhaseRunner / Network.Spawn / authority resolution):
65/65 PASS, exit 0.**
- **P0** (the §13 three gates + more): (a) a GameMode scene spawns + possesses a controllable pawn with
  owner-routed SetupInput; (b) a no-GameMode scene runs today's exact OnBegin/OnEnabled path (no OnSpawned,
  stays Offline) — the narrow byte-identity invariant; (c) net strand strictly before Unity strand, OnEnabled
  exactly once; plus a proxy never reaching SetupInput, the 0c registry clear, a disable/re-enable regression
  check, and framework-component serializer round-trip.
- **P1**: the full §4d.1 role truth-table — ALL 8 cells (dedicated/host/owner/other × client-pawn/world/
  host-pawn) including the host-corner (IsProxy=false on a host); the generational `NetworkRef<T>` (null on
  despawn via generation mismatch, NOT dangling, + the pooling invariant: an old handle stays null even when
  its slot is reused by a new identity); `TransferOwnership`/`RemoveOwnership` (server-only) flipping
  IsOwner/input-authority and firing `OnOwnershipChanged`.

- **P2**: the Roslyn source generator (`BallisticEngine.SourceGen`, quarantined like Networking/LiteNetLib
  per §12.2 — the ONLY project referencing Microsoft.CodeAnalysis, attached as an *analyzer*). It scans
  `NetworkBehaviour` subtypes and emits, per type with `[Networked]`/`[Rpc]` members, a browsable partial
  (the §11 advantage over IL weaving): `SerializeState`/`DeserializeState` (a FieldCount-bit changemask +
  only-changed fields vs a captured baseline — unchanged ≈ 1 bit/field), `CaptureNetworkBaseline`,
  `NetworkTypeId`/`NetworkLayoutHash` (FNV, codegen-time == runtime), and a `[ModuleInitializer]`
  registration into `NetworkReplicationRegistry`. `[Networked]` attribute (server-write default; the loud
  `[Networked(Authority.Owner)]` token; **opt-in** quantization via `Min/Max/Bits`, bare float = full 32-bit
  lossless). The **asymmetric send-rate** seam (§14 item 3): `SendRateClock` throttles state DOWN to the
  divisor (default 60 Hz sim / 3 = 20 Hz) in the real `NetworkManager.Tick`; the per-tick input-UP stream is
  modeled (`InputUpStream`) so P5 can't inherit a conflated rate. Gate-0c extended: the replication registry
  is the 2nd host-side root, cleared in `ReloadGameScripts` alongside `InputRegistry`.

`bal schema` confirms the registry auto-discovers every framework type (§10 free discovery). Full slnx builds 0
errors. **The P2 wire format was proven byte-for-byte in an ISOLATED harness `%TEMP%\bal-netserde-test`
(repo discipline) BEFORE engine integration**, then re-proven against the REAL generated code in the in-engine
harness. **NEXT = P3** (LiteNetLib transport — two processes; state crosses the wire under the packet budget).

---

**The gate itself (below) was settled FIRST** as a ~1-page mechanism + an isolated
callback-ordering harness, **with engine code untouched** — exactly the mesh-SDF / 37-check-physics discipline
(isolated correctness proof BEFORE engine integration).

Worktree `e:/Unity Projects/Ballistic-Engine-gameplay`, branch `gameplay-framework` (off `dx12-renderer` tip
`1e125a13`, clean). Renderer untouched.

The harness lives at `%TEMP%\bal-gate-test` (`LifecycleGate.csproj` + `Program.cs`), following the repo's
scratch-console discipline (`%TEMP%\bal-phys-test`, `bal-sdf-test`, etc.). It is a **faithful transcription**
of the real lifecycle machinery (verbatim semantics from `Behaviour.cs`, `Entity.cs`, `Scene.cs`,
`SceneManager.cs` as of `1e125a13`), so a green run proves the *mechanism* transfers to the engine. Exit code
0 = all checks pass.

**Re-run:** `cd %TEMP%\bal-gate-test && dotnet run -c Release` → **27/27 checks PASS** (proves the NAIVE runner
double-fires `OnEnabled`, the FIXED runner fires each exactly once with `OnSpawned` first, plain Behaviours are
byte-identical, the proxy never gets `SetupInput`, and the reload registries clear + layout-hash rejects drift).

---

## The three blockers (verified against real code @ `1e125a13`)

### 0a — B1/B2: the `OnEnabled` double-fire (the load-bearing defect)

**Real code:** [`Behaviour.FireEnable`](../../Engine/BObject/Objects/Behaviour.cs#L113):

```csharp
internal void FireEnable() {
    if (!HasBegun) {            // <-- HasBegun gates ONLY OnBegin
        HasBegun = true;
        try { OnBegin(); } catch { ... }
    }
    try { OnEnabled(); } catch { ... }   // <-- line 119: OnEnabled fires UNCONDITIONALLY, every call
}
```

`Scene.FireBegin` → `Entity.FireBegin` → `Behaviour.FireEnable` is the single play-start activation path
([`SceneManager.StartPlay:220`](../../Engine/BObject/Scene/SceneManager.cs#L220)).

**The defect the naive plan ("reuse `HasBegun`") would ship:** if a phase runner activates a framework
component in Phase 1 by calling `FireEnable()` (to get its `OnBegin`/`OnEnabled`), and Phase 3's
`scene.FireBegin()` then walks *every* entity and calls `FireEnable()` again, then `HasBegun` suppresses the
second `OnBegin` — but **`OnEnabled` fires a SECOND time** (line 119 is unconditional). That is a real,
observable double-`OnEnabled`. Proven by the harness's `NAIVE` mode.

**The fix (mechanism):**
1. **Phases 0–2 NEVER call `FireEnable` on framework components.** They drive only the *net* strand directly
   (`OnSpawned` → `OnStartServer/Client/LocalPlayer`), and set a per-component `NetBegun` mark.
2. **Phase 3's `scene.FireBegin()` is the SINGLE place the Unity strand (`OnBegin`/`OnEnabled`) fires** — for
   every component, framework ones included. Their `OnSpawned` already ran in Phase 1; Phase 3 gives them their
   `OnBegin`/`OnEnabled` exactly once.
3. **A `HasEnabled` companion guard in `FireEnable`** makes the *whole* `FireEnable` idempotent per play-start:
   `OnEnabled` (like `OnBegin`) fires at most once per activation. This is a one-line addition to `FireEnable`
   that is **byte-identical for all existing components** (they go through `FireBegin` exactly once today, so
   `HasEnabled` flips on the first and only call — no behavioural change). Re-enable-after-disable still works:
   `OnDisabled` clears `HasEnabled`, so a later re-enable fires `OnEnabled` again (matching today's semantics
   where `IsEnabled=true` → `FireEnable` → `OnEnabled`).

The harness proves: **FIXED mode → exactly one `OnBegin`, exactly one `OnEnabled`, `OnSpawned` strictly before
both — even though the framework component is "touched" by both Phase 1 (net strand) and Phase 3 (Unity
strand).**

### 0b — B3: the skeletal `NetworkBehaviour`/`IsOwner`/`IsSpawned`

`PlayerController : NetworkBehaviour`, and `SetupInput` gates on `IsOwner`/`HasInputAuthority` — so the
identity/ownership skeleton MUST exist in P0 even with no socket (trivially-true in loopback). The harness
models a minimal `NetworkBehaviour` with `IsSpawned`, `IsOwner`, `OnSpawned`/`OnDespawned`/`OnStartLocalPlayer`,
and proves the phase runner drives its net strand *before* its Unity strand.

### 0c — reload-safety + wire-format hash

Self-registering `InputAction`s + the network registration table are host-side static roots that pin the
collectible script-ALC unless cleared at the reload boundary
([`ReloadGameScripts:192`](../../Engine/Bootstrap/EngineBootstrap.cs#L192)). The mechanism: **both registries
join the existing "clear scene + registry + volume stack before `GameScripts.Unload`" list** — same pattern as
`ComponentRegistry.Build` being re-run. The harness models a `static` registry + a `ClearForReload()` hook and
asserts that after a simulated reload the registry holds NO handles from the "old assembly" (no leaked roots).
A layout-hash over a `NetworkBehaviour`'s replicated fields is stamped into a mock handshake; a changed layout
produces an explicit mismatch error, not a silent pass.

---

## The phase runner (the engine change P0 will make, modelled here)

`SceneManager.StartPlay` replaces the bare `scene.FireBegin()` with:

```
StartPlay():
  if (no GameMode in scene)  -> scene.FireBegin()           // EXACTLY today's path; byte-identical
  else:
    Phase 0: gameMode.InitGame()                            // server-only
    Phase 1: for each PlayerController (entity-id ordered):
               pawn = gameMode.ResolvePawn(c)               // spawn DefaultPawn OR possess scene-placed
               DriveNetStrand(pawn);  DriveNetStrand(controller)   // OnSpawned + OnStartX; mark NetBegun
               controller.Possess(pawn)                     // SetupInput on owner only
    Phase 2: hud.Init()                                     // client-only
    Phase 3: scene.FireBegin()                              // SINGLE OnBegin/OnEnabled site (guard prevents
                                                            //   double OnEnabled on the Phase-1 components)
```

`Network.Spawn` (runtime) sets a suppression flag like
[`SceneManager.SuppressPlayLifecycle`](../../Engine/BObject/Scene/SceneManager.cs#L32) so `Entity.Attach`'s
eager `FireEnable` ([`Entity.cs:95`](../../Engine/BObject/Objects/Entity.cs#L95)) does NOT run before
`OnSpawned`; the spawn path then drives strands in order (`OnSpawned` → net → `OnBegin`/`OnEnabled`).

**No-GameMode ⇒ byte-identical:** the `else` branch is never taken, `scene.FireBegin()` runs exactly as today.
The `HasEnabled` guard is the only touch to the shared `FireEnable`, and it is a no-op for today's
single-activation path.
