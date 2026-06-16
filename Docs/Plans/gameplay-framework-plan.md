# Ballistic Engine — Gameplay Framework & Networking-First Architecture

**Status:** DESIGN / PROPOSAL. Captured 2026-06-16. No code written yet — this is the agreed-on shape before
implementation. Sibling plan to the [AI-Native Master Plan](ai-native-engine-master-plan.md); orthogonal to the
DX12/GI tracks (renderer work is untouched by this).

**Core thesis:** keep the existing `Behaviour`/`Entity` substrate (it carries 40+ components, the editor,
serialization, reflection — all of it), and **layer an opt-in Unreal/FishNet-style gameplay framework on top**.
Design the API to be **shape-complete, not feature-complete**: push the wrong machine / wrong write / wrong
input path toward being *unrepresentable*. Precisely: ONE class is truly eliminated (non-owner input — §3
Grade 1); the rest are made **closed-by-default / loud** (Grade 2) or are **documented conventions** (Grade 3).
"Edge cases cease to exist" is the *aspiration* and is literally true only for Grade 1 — Grades 2–3 still need
validation and tests. The whole server-authoritative gameplay vocabulary reduces to ~four concepts (§3).

---

## 0. Owner-approved decisions (2026-06-16)

| # | Decision | Section |
|---|---|---|
| D1 | **Networking = server-authoritative** (Unreal/FishNet model). Server owns truth; clients send *intent*, not *state*. Authority/ownership/roles baked into the base types from day one. | §3, §6 |
| D2 | **Init order = `GameMode → Player/Pawn → HUD → everyone-else`.** Fixed deterministic phases replace Unity's unordered `Awake`/`Start`. No `GameMode` in the scene ⇒ today's exact behaviour. | §5 |
| D3 | **Input = event-based, possessor-routed.** You subscribe to input events; on a non-owner the event *never fires* — so there is no "what do I do when input is false" branch to write. (Event *routing* is free; cross-vendor device *support* is not — §7.8.) Replaces polling for gameplay. | §7 |
| D4 | **Player appears two ways: GameMode-spawn OR scene-placed.** GameMode spawns a default Pawn prefab (Unreal); but if a Pawn is hand-placed in the scene it *possesses* that instead (Unity familiarity). Both paths supported. | §6.2 |
| D5 | **Single-player = loopback/offline transport, same code.** SP = host (server+client in one process). GameMode still spawns, the same netcode runs, `bal simulate` uses the same path. One code path ⇒ no "worked in SP, broke in MP" class. | §8.3 |
| D6 | **Scope of THIS doc = plan only.** No code on the current `dx12-renderer` branch (open DX12 GI work). Implementation is a later, separate branch. | — |

---

## 1. Why add, not replace

The owner dislikes the straight Unity-clone feel. The fix is **addition, not replacement** — deleting
`Behaviour` to make everything an `Actor` would cost weeks and break the whole editor for nothing.

- **Unreal itself layers.** `AActor` + `UActorComponent` are the substrate; `AGameMode`/`APawn`/
  `APlayerController` are a gameplay *framework* on top. We already have the substrate: `Entity` ≈ `AActor`,
  `Behaviour` ≈ `UActorComponent`. We're missing only the framework layer.
- **Everything binds to `Behaviour`.** `StaticMeshRenderer`, `Rigidbody`, `Light`, `Volume`, `Animator`, … all
  derive from it. Serialization (`ComponentReflection`), the inspector drawer pipeline, the Add-Component menu,
  `bal schema`, MCP/pipe — every one walks `Behaviour` subtypes by reflection at bootstrap
  ([`ComponentRegistry.Build`](../../Engine/.../ComponentRegistry.cs)). New types that *derive from*
  `Behaviour` are discovered automatically; replacing the base re-touches all of it.
- **Opt-in = zero regression.** The framework activates only when a scene names a `GameMode`. Its absence ⇒
  today's exact behaviour. This is a standing headless test (`bal render` diff = 0) across all phasing.

---

## 2. Type map

| New type | Base | Unreal analog | Lives where | Replication |
|---|---|---|---|---|
| `GameMode` | `SceneBehaviour` | `AGameModeBase` | scene-wide, **server-only** | not replicated (clients never see it) |
| `GameState` | `SceneBehaviour` + `IReplicated` | `AGameStateBase` | scene-wide | replicated to all |
| `Pawn` | `NetworkBehaviour` | `APawn` | on an entity | replicated to all |
| `PlayerController` | `NetworkBehaviour` | `APlayerController` | on an entity | owner + server only |
| `PlayerState` | `NetworkBehaviour` | `APlayerState` | on an entity | replicated to all |
| `HUD` | `SceneBehaviour` | `AHUD` | scene-wide, **client-only** | not replicated (local UI) |
| `NetworkBehaviour` | `Behaviour` | (FishNet/Fusion) | on an entity | carries `[Networked]` + `[Rpc]` |
| `NetworkObject` | `Behaviour` | `UNetworkObject` | on an entity | identity + authority holder |

```
Component
└─ Behaviour                       (existing)
   ├─ NetworkObject                (NEW — net identity, ownership, the unit that spawns/despawns)
   └─ NetworkBehaviour             (NEW — HasStateAuthority/HasInputAuthority/IsProxy/IsOwner, [Networked], [Rpc])
      ├─ Pawn  └─ CharacterPawn    (NEW — possession + predicted movement, later)
      ├─ PlayerController          (NEW — owns the input event source, possesses a Pawn)
      └─ PlayerState               (NEW — per-player replicated data)

SceneBehaviour                     (existing)
   ├─ GameMode                     (NEW — server-only rules + the init driver)
   ├─ GameState (+ IReplicated)    (NEW — replicated match state)
   └─ HUD                          (NEW — client-only presentation)
```

`GameMode`/`HUD` are plain `SceneBehaviour` (never replicated). `GameState` adds a small `IReplicated`
interface the network tick collects (so it replicates without being on an entity — cleaner than a ghost
companion). `Pawn`/`PlayerController`/`PlayerState` are `NetworkBehaviour` because they replicate.

> **Note (verified):** today's `SceneBehaviour` ([`SceneBehaviour.cs`](../../Engine/BObject/Objects/SceneBehaviour.cs))
> has **only** `OnAttach`/`OnDetach` + gizmos — it is a config carrier with no tick/begin. So `GameMode.InitGame()`
> (Phase 0) is called *directly* by the phase runner (fine), but `GameState`'s replication and any per-tick
> GameMode driver need **new dispatch wiring on the scene** (the network tick collects `IReplicated`
> scene-behaviours; a GameMode that wants a tick gets one). This is additive machinery — small, but more than
> §5's "one change at StartPlay" implies. Budget it in P0 (GameMode/HUD init) and P7 (GameState replication).

---

## 3. The design philosophy — shape-complete, edge-case-minimal

The whole server-authoritative gameplay model reduces to **four concepts**: *machine perspective, authority
(state vs input), ownership, object readiness*. That's the entire vocabulary. Four **load-bearing decisions**
eliminate or defang 8 of 10 known edge-case classes (full catalog in §9); everything else is cheap
reinforcement.

| # | Load-bearing decision | Edge-case classes it kills |
|---|---|---|
| **L1** | **State = synced property; RPC = punctual event only.** Properties converge to the last value (idempotent, order-immune, loss-tolerant). | late-joiner gap · RPC buffer overflow · RPC ordering vs state · RPC-on-not-yet-spawned · intermediate-value surprise |
| **L2** | **One fixed tick = the network clock**, decoupled from render. We reuse the existing 60 Hz `FixedTick` accumulator — no second clock. (One fixed tick gives a deterministic *clock*, not a deterministic *simulation*; what prediction needs is only **local-replay determinism** — same machine, same binary, replay buffered inputs against the last server state — NOT cross-machine lockstep. So Bepu does NOT need to be bit-identical across platforms; see §8.2.) | frame-rate desync · spiral-of-death · prediction *local-replay* determinism · clock drift |
| **L3** | **State Authority ≠ Input Authority** — two orthogonal, explicit roles, never one flag. | "who runs this code" ambiguity · `IsOwner`-on-host · non-owner input · a cheating vector |
| **L4** | **Server-authoritative by default; client property writes are local prediction, overwritten on sync.** | the entire state-cheating class · the *re-simulation* part of validation reuses the reconcile loop (cheap), but explicit input bounds-checks (speed/teleport) are still code you write per game |

**Three GRADES of safety, not one** — be precise about this, because "cease to exist" is true for only ONE of
the three, and a future implementation session must NOT treat grade-2/3 cases as if they're already impossible
and skip validation/tests:

**Grade 1 — genuinely unrepresentable** (you *cannot write* the wrong thing):
- **No non-owner input path.** Input is *events* the framework fires only on the input authority. A proxy's
  event never fires — there is no `else`, no `if (false)` branch, nothing to misuse (§7). This is the *only*
  fully-pure example in the document. *Direct fix to the owner's objection that polling `TryGetInput()==false`
  leaves a dangling code path.*

**Grade 2 — closed-by-default, made LOUD, but still writable** (the dangerous thing is visible at the
declaration / runtime-rejected, NOT impossible — these still need validation + tests):
- **Writes default closed.** `[Networked]` = server-write / everyone-read with no parameters; owner-write is a
  *visible* `[Networked(Authority.Owner)]` token. A dev can still mis-declare it; the token just makes it loud.
- **Client→server RPC is owner-checked by default** and injects the caller identity when not — validation is
  unavoidable to *reach*, but the validation logic itself is yours to write correctly.
- **No public `netId` — but the null-on-despawn behavior is a MECHANISM, not magic** (§8.4). A plain C# `Pawn`
  reference does NOT null on despawn (it dangles until GC). "Ordinary null check" only works if refs are a
  `NetworkRef<T>` handle with a generation/version counter that returns null after despawn — that handle must be
  *built* (open question, §14). Until it is, this is an aspiration, not a guarantee.

**Grade 3 — documented convention** (correctness depends on the dev knowing a rule):
- **`Awake`/constructor = a no-network zone.** Networked members valid only from `OnSpawned` onward. Enforced by
  *documentation + ideally a runtime assert*, not by the type system.
- **State sync is "last-value-wins, per tick."** A convention the dev must internalize (§9.6).
- **One bare server boolean, about the *process* not an object.** `Network.IsServer` (topology) ≠ `IsSpawned`
  (object readiness) — FishNet's `IsServer`→`IsServerStarted`/`IsServerInitialized` correction, split from day
  one. Self-documenting naming, but still a convention.

**So the honest claim is:** *one* footgun is eliminated (proxy input); the rest are made closed-by-default or
loud. That's still a strong posture — but "edge cases cease to exist" applies literally to Grade 1 only.

---

## 4. The full API surface (minimal but complete)

The entire surface a developer learns. Four base types, four attributes, three callbacks, a handful of
role/lifecycle members — covers ~90% of server-authoritative games. Each line: why it earns its place.

### (a) Base types
- **`NetworkBehaviour : Behaviour`** — the one networked base; *is-a* `Behaviour`, so single-player code is
  unchanged and the editor/serializer/CLI discover it free.
- **`NetworkObject`** — net identity + authority/ownership; the unit that spawns/despawns; `[Networked]` state
  lives under it.
- **`Pawn : NetworkBehaviour` + `PlayerController : NetworkBehaviour`** — the possession pair (`Possess` /
  `Unpossess`); `PlayerController` owns the input event source.
- **`GameMode : SceneBehaviour`** — server-only rules + the init/spawn driver; fits the existing scene-wide
  pattern; never replicated.

### (b) Attributes (live in `Engine/Attributes/`, plain `System.Attribute`, house style — `[Range]`, `[ShowIf]`)
- **`[Networked]`** on an auto-property — declarative replicated state; server-write / everyone-read by default.
- **`[Networked(Authority.Owner)]`** — opt-in owner-write; the dangerous case made visible at the declaration.
- **`[Rpc(To.Server)]` / `[Rpc(To.Owner)]` / `[Rpc(To.All)]`** — one universal RPC attribute + typed target
  enum; reliable by default; `Rpc.Unreliable` opt-in; `To.Server` owner-checked by default. **RPCs are
  fire-and-forget — there is NO RPC return/response, by design (L1).** This looks like a gap but is the right
  shape: for "ask the server, await an answer" (purchase confirm, name-reservation, lobby-join result), the
  answer is **state, not an RPC return** — the server writes a `[Networked] Result` (or owner-only via
  `[Networked(Authority.Owner)]` + a request-id) and the owner sees it via `[OnChanged]`. *Stated explicitly
  (§4b) because the surface APPEARS incomplete and a dev/agent's first instinct is to bolt a return value onto
  `[Rpc(To.Server)]` — which re-imports the ordering/loss problem L1 exists to kill. Request→response = RPC-up
  (the request, an event) + state-down + `[OnChanged]` (the answer), never an RPC return.*
- **`[OnChanged(nameof(M))]`** — change notification *separated from the setter*, so it survives a future
  prediction layer (a naive per-set callback would fire spuriously during rollback replay).

### (c) Callbacks (virtual on `NetworkBehaviour`) — coarse-and-few, role by property not by callback count
- **`OnSpawned()`** — networked state is valid here; init visuals/subscriptions. (Fusion's `Spawned`.)
- **`OnDespawned()`** — symmetric teardown.
- **`NetworkTick()`** — the single simulation step; the *only* place state mutates; the sim/render boundary that
  makes prediction possible later. (Fusion's `FixedUpdateNetwork`.)
- *Rare hooks — `IOwnershipChanged`, `IPlayerJoined` — as opt-in interfaces, kept off the core surface.*

### (d) Role / lifecycle members & static facade
- **`HasStateAuthority` / `HasInputAuthority` / `IsProxy`** — authority as a noun you *have*; the role gate a
  body checks (no multiplied callbacks). **`IsProxy` has a PRECISE definition (the §3-class corner the earlier
  draft left undefined — it bites on host):** `IsProxy ≡ !HasStateAuthority && !HasInputAuthority`. So on a host
  (server+client in one process) looking at ANOTHER client's pawn: `HasStateAuthority = true` (it's the server),
  `HasInputAuthority = false`, `IsOwner = false`, and therefore **`IsProxy = false`** (it is NOT a proxy on the
  host — the host has state authority over it). "Proxy" means "neither authority," not "I don't drive its
  input." This is a **Grade 3 convention** (§3) and the exact role truth-table is §4d.1 — it MUST be written
  before P1's "roles resolve for host-own-pawn vs a second simulated pawn" verify, or that test is unwriteable.
- **`IsOwner` / `OwnerId`** — ownership, named separately from server identity. On host, *derived* from
  `Owner == LocalConnection` so it's correct (FishNet's host trap, designed out).
- **`IsSpawned`** — readiness ("safe to touch networked members").
- **`Network.IsServer / IsClient / IsHost`** — process topology; the only bare server/client boolean, and it's
  about the process. Mirrors the existing `Physics`/`Input` static facades.
- **`Network.Spawn(prefab, owner?) / Despawn(obj)`** — server-authoritative lifecycle; owner defaults to server
  (closed trust boundary).
- **`obj.TransferOwnership(connection)` (server-only, replicated) + `obj.RemoveOwnership()`** — runtime
  ownership transfer. *(Added: the earlier draft had `OnOwnershipChanged` (§4e) and used ownership transfer
  internally for reconnect (§8.5.5) but exposed NO API to trigger it — leaving `OnOwnershipChanged` a callback
  game code couldn't cause.)* This is what powers pick-up items, vehicle-enter, detachable turrets. Server-only
  to call (a client can't grant itself ownership — closed trust boundary); the change replicates and fires
  `OnOwnershipChanged` on the affected peers. P1 scope.

#### (d.1) Role truth-table (the canonical "who am I for this object" — settle before P1 verify)
For any networked object, on each machine, derived from State/Input authority (never a single overloaded flag):

| Object ↓ / Machine → | Dedicated server | Listen-server HOST | Owning client | Other client |
|---|---|---|---|---|
| **A client's own pawn** | StateAuth ✓, InputAuth ✗ → not-proxy | (if host owns it) all ✓; (else) StateAuth ✓ → not-proxy | StateAuth ✗, InputAuth ✓, IsOwner ✓ → **AutonomousProxy** | all ✗ → **IsProxy (SimulatedProxy)** |
| **World/AI object (server-owned)** | StateAuth ✓ → authority | StateAuth ✓ → authority | all ✗ → **IsProxy** | all ✗ → **IsProxy** |
| **The host's own pawn** | n/a | StateAuth ✓, InputAuth ✓, IsOwner ✓ → authority+owner | n/a | all ✗ → **IsProxy** |

Key reading: **`IsProxy` is false on the host for everything** (the host is the server → always has state
authority) — the host is never a "proxy," even for pawns it doesn't input-drive. `AutonomousProxy` (predicts +
reads input) ⟺ `!HasStateAuthority && HasInputAuthority`; `SimulatedProxy` ⟺ `IsProxy` (neither). This table is
the precise form of L3 and the answer to "who runs this code" on a host.

### (e) Auto-targeted, role-gated callbacks (the dev NEVER writes an ownership gate)
The framework invokes each on exactly the right machine, so the subclass body has zero `if (IsOwner)`:

| Callback | Auto-targets | Replaces the gate |
|---|---|---|
| `OnStartServer/Stop` | server only | `if (IsServer)` |
| `OnStartClient/Stop` | each observing client | `if (IsClient)` |
| `OnStartLocalPlayer()` | owner client only | `if (IsOwner)` |
| `OnPossessed(c)/OnUnpossessed()` | owning machine | possession setup |
| `OnOwnershipChanged(prev,next)` | server + affected clients | ownership transitions |
| `SetupInput(InputComponent)` | **owner / input-authority only** | the whole `if (IsLocalPlayer)` block (§7) |

---

## 5. Ordered initialization (D2)

When a scene declares a `GameMode`, play-start runs in strict phases — each fully completes before the next, so
a Pawn's `OnSpawned` can rely on the `GameMode` already being initialized.

```
StartPlay()
  Phase 0  GameMode.InitGame()              server-only; default Pawn class, spawn points, rules
  Phase 1  for each PlayerController:
             pawn = GameMode.ResolvePawn(c) spawn the default Pawn prefab, OR possess a scene-placed Pawn (D4)
             controller.Possess(pawn)       sets pawn.Controller; SetupInput fires on the owner only
  Phase 2  HUD.Init()                       client-only; binds to local PlayerController/PlayerState
  Phase 3  Scene.FireBegin()                every OTHER component begins (see the per-type note below)
```

- **Phase 3 fires the RIGHT callback per type (not a blanket "OnSpawned → OnEnabled").** A plain `Behaviour` has
  **no** `OnSpawned` — for it Phase 3 is exactly today's `OnBegin → OnEnabled`. A `NetworkBehaviour` gets
  `OnSpawned` (net-init) *then* `OnBegin/OnEnabled` (local). Network never leaks onto non-network objects — the
  full interleave contract is §8.5. *(Corrected: an earlier draft wrote "OnSpawned → OnEnabled" for every
  component, which would imply plain Behaviours have OnSpawned — they don't.)*
- **No `Awake`.** The only pre-begin hook is `OnAttach` (render registration, both edit+play — unchanged).
  Game-logic init is `OnBegin` (plain) / `OnSpawned` (networked), and Phase 3 fires the non-framework ones
  *after* the player exists. This is exactly the owner's ask: player/game-mode init before the rest of the
  world's callbacks.
- **No `GameMode` ⇒ fall straight to Phase 3** = byte-identical to today.
- **Hook + the REAL mechanism (corrected — `HasBegun` alone does NOT prevent the double-fire).** The hook is one
  change at [`SceneManager.StartPlay`](../../Engine/BObject/Scene/SceneManager.cs:220): replace the bare
  `scene.FireBegin()` with a phase runner. **But a final review caught a defect in the naive "reuse `HasBegun`"
  claim:** [`Behaviour.FireEnable`](../../Engine/BObject/Objects/Behaviour.cs:113) gates **only `OnBegin`** with
  `HasBegun`; `OnEnabled` fires *unconditionally* on every `FireEnable` call (line 119). So if Phase 1 called
  `FireEnable()` on a scene-placed framework component and Phase 3's `scene.FireBegin()` then walks every entity
  and calls `FireEnable()` again, `OnBegin` is suppressed but **`OnEnabled` fires TWICE.** The fix (a small,
  real piece of machinery, not "free reuse"):
  - **Phases 0–2 do NOT call `FireEnable` on framework components.** They drive only the *net* strand directly
    (`OnSpawned`/`OnStartX` — see §8.5), and mark those components so Phase 3 skips them.
  - **Phase 3's `scene.FireBegin()` is the SINGLE place the Unity strand (`OnBegin/OnEnabled`) fires**, for
    every component including the framework ones (their `OnSpawned` already ran in Phase 1). A small skip/dedup
    guard (an `Entity.FireBegin` that doesn't re-`FireEnable` a component already activated this play-start) or a
    `HasEnabled` companion flag prevents any double `OnEnabled`. **This is the B1/B2 fix — settle it before P0
    (see §14 item 0).**
- **Runtime spawn inverts the order unless `Network.Spawn` suppresses lifecycle.** At runtime,
  `Network.Spawn` → `Entity.Instantiate` + `AddComponent`, and [`Entity.Attach`](../../Engine/BObject/Objects/Entity.cs:95)
  calls `FireEnable()` *immediately* — firing `OnBegin/OnEnabled` with **no `OnSpawned` first**, the reverse of
  the §8.5 table. So `Network.Spawn` MUST set a suppression flag (exactly like
  [`SceneManager.SuppressPlayLifecycle`](../../Engine/BObject/Scene/SceneManager.cs:32) does for deserialize) so
  `Attach` skips the eager `FireEnable`, then the spawn path drives the strands in order (`OnSpawned` → net →
  `OnBegin/OnEnabled`). This is named machinery the plan owes; it reuses the *existing* suppression pattern.
- All of this is inside `StartPlay`/`Network.Spawn` (play-only) — edit mode untouched.

**Reconciling §5 with §6 (the per-spawn ordering guarantee).** Phase 1's "for each `PlayerController`" reads as
if all players exist at `StartPlay`. That's true only for **SP / listen-server's own player**. On a dedicated
server, players connect *over time*. So separate two things:
- **Global one-shot phases (0 and 2)** — `GameMode.InitGame()` (Phase 0) and `HUD.Init()` (Phase 2) run **once**
  at `StartPlay`. A **late joiner does NOT re-run them** — the game is already initialized; the joiner just
  receives replicated `GameState`.
- **Per-player spawn ordering (Phase 1)** — the real invariant is *"for a given player, GameMode is initialized
  before that player's pawn spawns, which is before that pawn's `OnSpawned`."* This invariant is enforced **both**
  at `StartPlay` (for players present then) **and** at every `OnPlayerJoined` (§6) for later joiners. §6's
  per-connection join flow IS Phase 1 applied to one player; `StartPlay` just runs it for the initial batch.

So the phase *order* is a per-player guarantee, not a "everyone at once" assumption — and the global phases are
genuinely once-only.

---

## 6. GameMode, spawning, possession (D1, D4)

**Server-authoritative spawn is the model's heart, not a contradiction.** The owner asked: "GameMode is
host-only, but it spawns the player — then what?" Answer: spawning a player *is* a server-authority operation,
so GameMode living on the server is exactly where it belongs. A client cannot spawn its own pawn (that would be
a cheat). The flow:

```
client connects ──▶ server GameMode.OnPlayerJoined(connection)
                      ├─ D4 mode A: Network.Spawn(DefaultPawn, owner: connection)   ← Unreal default
                      └─ D4 mode B: possess an existing scene-placed Pawn            ← Unity familiarity
                    ──▶ pawn replicates to ALL machines
                          owner client  → AutonomousProxy (predicts, reads input)
                          other clients → SimulatedProxy (interpolated)
                          server        → Authority (truth)
```

- **D4 — two ways a player appears.** `GameMode.ResolvePawn(connection)`: if a Pawn is hand-placed in the scene
  and unassigned, **possess it**; otherwise **spawn** `DefaultPawn`. Both supported so a Unity-minded user who
  drops a character in the scene isn't forced into Unreal's spawn-only model.
- **D4's MULTIPLAYER rules (the SP case is trivial; MP needs explicit, deterministic semantics — or the
  "SP==MP same code" thesis breaks).** With 3 scene-placed Pawns and 5 joining players:
  - **Scene-placed Pawns are claimed in connection order**, on the *server* (the authority), from a stable list
    ordered by the Pawns' entity IDs (deterministic across machines — not by iteration/hash order).
  - **When scene Pawns run out, overflow players get `Network.Spawn(DefaultPawn)`** at a spawn point (Unreal
    fallback). So: first N players (N = scene Pawns) possess the placed ones; the rest spawn.
  - **Assignment is server-decided and replicated** — clients never pick; they receive the possession. This
    keeps it deterministic and cheat-proof (a client can't claim a pawn).
  - If `DefaultPawn` is unset *and* scene Pawns run out, that's a configuration error the GameMode logs (a
    player with no pawn) — not a silent failure.
  This makes D4 work identically in SP (1 player, claims the 1 scene Pawn or spawns) and MP (deterministic
  claim-then-spawn), preserving D5's "same code" guarantee.
- **Possession wires input on the owner only.** `controller.Possess(pawn)` is server-authoritative; on the
  machine that locally controls the pawn the framework calls `SetupInput` (§7). On every other machine the pawn
  is a proxy and `SetupInput` is never called — Unreal's `IsLocallyControlled` mechanism, with zero gate code.

---

## 7. Input — event-based, possessor-routed (D3)

### 7.1 What exists today (the substrate we build on, don't delete)
- **`Input`** ([`Abstraction/API Bindings/Input.cs`](../../Abstraction/API%20Bindings/Input.cs)) — raw device
  polling (`IsKeyDown(Keys.W)`), global static, `Enabled`-gated, behind `IInputProvider`. **Stays** as the raw
  source.
- **`InputActions`** ([`Abstraction/API Bindings/InputActions.cs`](../../Abstraction/API%20Bindings/InputActions.cs))
  — Unity's *classic* InputManager: `GetAxis("Horizontal")`, `GetButton("Jump")`, device-fused, edge-tracked
  via per-frame `Update()`. **Polling, static, no per-player/per-owner notion.** It stays as the binding source
  but is *not* what gameplay subscribes to.

The gap: this is the **old** Unity model (polling). Unreal's Enhanced Input and Unity's New Input System are
**event/callback-based**. We need that layer, and it must be network-aware.

### 7.2 The definition model — ONE `InputAction` + a binding list (Unity/Unreal shape)
**The earlier `InputAxis2`/`InputAxis1`/`InputButton` three-class form was wrong** (the owner: *"our structure
was never like this — in Unity/Unreal you just define a simple input type: a button, A on keyboard, B on
gamepad; same for WASD and the stick"*). Research confirms both shipping engines use **ONE action class
parameterized by a value type + a SEPARATE list of bindings**, *not* per-shape subclasses. We adopt that exactly.

```csharp
// Core: one InputAction (value type is a FIELD, not a subclass) + a list of bindings.
public sealed class InputAction {
    public string         Name;
    public InputValueType Value;       // Button | Axis1D | Axis2D   (the SHAPE — on the action)
    public List<Binding>  Bindings;    // a SEPARATE list — each names a device control by ENUM
    public Trigger        Trigger;     // Press/Hold/Tap/DoubleTap/Pulse — default Down/Press (§7.6)
}
public sealed class Binding {
    public DeviceControl Control;      // the bound control, captured from a typed enum (NOT a string)
    public Modifier[]    Modifiers;    // Negate / Swizzle / Scale — turns a scalar key into an axis component
}
```

**NO string paths, and NO OpenTK enums** (owner: *"binding by string is awful — use enums"* and *"don't do these
with OpenTK, write our own custom types; we'll wire the backend later"*). We define the engine's **own**
device-control enums in `Abstraction/Input/` (BCL-only, **no OpenTK dependency** — today's `Input.cs` leaking
`OpenTK…Keys` is exactly the kind of dependency the DX12 migration is removing). Bindings use *our* enums,
captured via `Bind` **overloads**, one per device enum:

```csharp
// The engine's OWN enums — Abstraction/Input/, BCL-only, no OpenTK. The backend maps these later.
public enum Key       { A, B, C, … W, S, D, Space, R, LeftShift, F4, … }
public enum MouseCtrl { Left, Right, Middle, Delta, ScrollY }
public enum PadButton { A, B, X, Y, LeftBumper, RightBumper, Start, Back, … }
public enum PadAxis   { LeftStick, RightStick, LeftTrigger, RightTrigger }

public static class PlayerActions {
    // Jump — a Button: Space on keyboard, A on gamepad (the owner's "A on keyboard, B on gamepad")
    public static readonly InputAction Jump = new InputAction("Jump", Button)
        .Bind(Key.Space)
        .Bind(PadButton.A);

    // Move — Axis2D: WASD synthesized via per-binding Negate/Swizzle + the stick natively 2D
    public static readonly InputAction Move = new InputAction("Move", Axis2D)
        .Bind(Key.W, Swizzle)            // +Y
        .Bind(Key.S, Swizzle, Negate)    // -Y
        .Bind(Key.A, Negate)             // -X
        .Bind(Key.D)                     // +X
        .Bind(PadAxis.LeftStick);        // already 2D, no modifier

    public static readonly InputAction Reload = new InputAction("Reload", Button)
        .Bind(Key.R).Bind(PadButton.X)
        .WithTrigger(Hold(0.5f));        // trigger in the definition (§7.6)
}

// Bind overloads — one per OUR device enum, so each call is fully type-checked:
public InputAction Bind(Key key,         params Modifier[] m);
public InputAction Bind(MouseCtrl ctrl,  params Modifier[] m);
public InputAction Bind(PadButton btn,   params Modifier[] m);
public InputAction Bind(PadAxis axis,    params Modifier[] m);   // continuous 2D/1D stick or trigger
```

`.Bind(control, …modifiers)` returns the action (fluent), so the binding list reads as a block — no `InputAxis2`
vs `InputButton` split, no strings, no OpenTK. **One action type, one OUR-enum binding list.**

**Backend mapping is deferred (owner: "wire the backend later").** Our `Key`/`PadButton`/… enums map to the
actual device source (OpenTK today, a DX12-window input later) through `IInputProvider` — the interface is
re-expressed in *our* enums and the provider does the translation table. So gameplay code and the whole input
system are **backend-agnostic**; swapping OpenTK out (the DX12 endgame) touches only the provider's mapping, not
a single action definition. The existing `OpenTK…Keys` in `Input.cs` is migrated behind this seam (a follow-up,
not a blocker for the design).

The reaction side (owner-routed events) is unchanged and stays bare — the value type drives which overload:

```csharp
public class FpsController : PlayerController {
    protected override void SetupInput(InputComponent input) {   // framework calls this ON THE OWNER ONLY
        input.OnAxis2(PlayerActions.Move, v => Pawn.AddMoveInput(v));   // Axis2D → Vector2
        input.OnAction(PlayerActions.Jump,   Pawn.Jump);               // Button, trigger already resolved
        input.OnAction(PlayerActions.Reload, Pawn.Reload);             // Hold(0.5f) lives in the def
        input.OnAction(PlayerActions.Fire, Phase.Started,  Pawn.FireDown);   // optional explicit phase
        input.OnAction(PlayerActions.Fire, Phase.Canceled, Pawn.FireUp);
    }
}
// NO 'if (IsLocalPlayer)'. NO 'new InputComponent()'. NO OnBegin override.
// On a proxy, none of these callbacks ever run — there is no false-branch to handle.
```

- **Why events kill the edge case.** Polling (`if (TryGetInput(out i)) Move(i);`) leaves the `else` open —
  that's a code path, hence a footgun. Events have no `else`: "input not present" is *not firing*, not a value
  you branch on. The illegal state (acting on input you don't own) is **unrepresentable**.
- **One action class, value type a field.** Confirmed against Unity (`InputAction` + `type`/`expectedControlType`)
  and Unreal (`UInputAction` + `ValueType` enum) — neither has `Axis2/Button` subclasses. Our `InputValueType`
  is the same idea. Bindings are a separate list; WASD→2D is per-binding `Negate`/`Swizzle` (Unreal's uniform
  model — no special composite container).
- **Device abstraction by OUR enums** — `PadButton.A` is a *logical* button; the provider maps it to the actual
  controller. **Cross-vendor mapping IS free** (corrected from an earlier overstatement): `PadButton.A` →
  Xbox A / PS ✕ / Switch B is handled by the `SDL_GameControllerDB` that **GLFW already bundles and OpenTK
  already exposes** (`GLFW.GetGamepadState`) — zlib-licensed, in-stack today, a few hours of glue (§7.8).
- **Event ROUTING is free; the remaining device work is small, not a database project.** Free: callbacks fire on
  the owner not a proxy, and one binding list reads keyboard + mouse + gamepad uniformly. Also effectively free:
  cross-vendor button layout (bundled DB above). The genuinely-to-build remainder is modest — deadzones/rescale
  (mostly already in `Input.cs`), multi-pad routing, hot-plug, and (only if needed) rumble/gyro via an SDL3
  backend (§7.8). The earlier "controller support comes free" was imprecise: *mapping* is free, *multi-pad +
  haptics* is small extra work.
- **Network-transparent.** On the owner the callbacks drive the pawn locally (prediction) *and* the same intent
  feeds the per-tick input the server simulates (§8.2). On proxies the pipeline isn't built. Identical game code.
- **Respects the existing `Input.Enabled` master gate.** The new `InputComponent` event layer samples through the
  same `Input`/`InputActions` source ([`Input.cs:27`](../../Abstraction/API%20Bindings/Input.cs)) that the
  editor flips off outside play-with-Game-focused — so editor debug-key leakage stays prevented (the gate's
  whole purpose). Events do not fire while `Input.Enabled` is false. (Stated because a bypass here would
  re-introduce the exact leak the gate exists to stop.)
- **Phases & Triggers** — `Phase` (`Started`/`Performed`/`Canceled`) is the *when*; `Trigger`
  (`Press`/`Hold`/`Tap`/`DoubleTap`/`Pulse`) is the *condition*, defined WITH the action, so `OnAction(Reload,
  cb)` has no trigger parameter (§7.6).

### 7.3 "Defining the action" — the real problem, and arbitrary keys (the owner's two sharpest questions)
The owner: *"the actual problem is defining `Move`"* and *"the guy just wants the F4 key — now what?"* These are
the two cases the action model must answer cleanly, or it's a leaky abstraction.

- **Defining an action = one `InputAction` + its binding list** (§7.2). `new InputAction("Move", Axis2D)` names
  it and sets the value shape; `.Bind(Key.W, Swizzle)…` adds the device controls **via our enums** (§7.2 — no
  strings, no OpenTK). There is no second place (no `InstallDefaults`, no `.inputmap` asset *required*) — the
  field declaration is the source of truth. This is the answer to "how do I bind WASD to Move": the action
  declares the 2D shape, the binding list names WASD + the stick, per-binding `Negate`/`Swizzle` compose the
  four keys into the vector.

#### 7.3.1 The full flow when a user authors their OWN action (owner-approved: code-first, ctor self-registers)
A user adding, say, `Crouch` writes exactly this — nothing else, nowhere else:

```csharp
public static class MyActions {                                   // anywhere in the game project
    public static readonly InputAction Crouch = new InputAction("Crouch", Button)
        .Bind(Key.C).Bind(PadButton.B);                            // own action — OUR enums (§7.2)
    public static readonly InputAction Move = new InputAction("Move", Axis2D)
        .Bind(Key.W, Swizzle).Bind(Key.S, Swizzle, Negate)
        .Bind(Key.A, Negate).Bind(Key.D)
        .Bind(PadAxis.LeftStick);
}
// in the controller:
input.OnAction(MyActions.Crouch, () => Pawn.Crouch());            // works, no registration step
```

- **The constructor self-registers.** `new InputAction(...)` records itself into the fusion registry *at
  construction* (the `.Bind(...)` calls fill its binding list) — so there's no central `InstallDefaults` list to
  edit and forget. **This is Grade 3, not Grade 1** (honest, per §3): because `static readonly` is *lazy*, the
  registration only runs when the field is touched (or by the load-time scan below). So "creating = registering"
  holds *given* the field is reached — a documented convention backed by the load-time scan, NOT a truly
  unrepresentable bug. (Corrected to match §3's own grade discipline — the earlier "not a representable bug"
  overclaimed.)
- **No central file to touch.** The user's action lives in *their* class; the engine never needs to know its
  name ahead of time. The handle carries everything.
- **Two ways to read it, both kept** (Unity-familiarity escape hatch): the recommended gameplay path is the
  type-safe handle (`input.OnAxis2(MyActions.Move, cb)` — owner-routed, refactor-safe); the existing
  `InputActions.GetAxis("Move")` polling API (Unity-classic string form) still works for quick/non-network
  scripts and is *not* removed.
- **GOTCHA to design around — `static readonly` is lazy.** C# runs a static class's field initializers only on
  *first touch* of that class, so an action no code has referenced yet is unregistered. For the per-binding flow
  this is harmless (referencing `MyActions.Crouch` in `SetupInput` touches it). But a "list ALL actions" screen
  (rebinding UI) needs every action registered up front — handled by a **one-time, load-time scan** that touches
  action-container types (assembly scan for `static readonly InputAction` fields, run once at bootstrap like
  `ComponentRegistry.Build`, never per-frame). Note this so a future session doesn't ship a rebind screen that
  silently misses un-touched actions.
- **Arbitrary keys (F4) need NO action ceremony.** Not every key press is a rebindable game action. A dev who
  "just wants F4" should not author an `InputAction`. Two tiers, by intent:
  - **Ad-hoc / one-off gameplay key** → `input.OnKey(Key.F4, Phase.Started, …)` on the *same* `InputComponent`,
    using **our `Key` enum** (NOT OpenTK's `Keys` — same rule as §7.2): no action handle, no rebind layer, but
    still **event-based and owner-routed** (fires only on the input authority, never on a proxy). So *all
    gameplay input* — rebindable actions and raw keys alike — flows through one owner-gated surface; the "should
    this run on the owner?" question never reappears for a raw key. F4 = one line, zero ceremony.
    *(The legacy global `Input.IsKeyDown(...)` still exists for non-gameplay editor/debug uses that are
    intentionally NOT owner-gated; it speaks OpenTK's `Keys` today and migrates behind our `Key` enum in the
    DX12 endgame — a documented legacy passthrough, the ONE place OpenTK's enum survives until then. Gameplay
    code never touches it.)*
  - **Real, rebindable game action** → declare an `InputAction` so it shows up for remapping, gamepad fusion,
    and serialization. The line between them is *"will a player ever want to rebind this?"* — Jump yes, a debug
    F4 no.

  So the model is **not** "everything must be an action." It's *actions for the rebindable game verbs, raw key
  hooks for everything else* — which keeps the action set small (the thing that actually needs a name) and makes
  the F4 case trivial. This directly avoids the over-abstraction trap where a one-off key forces a whole action
  definition.
- **Rebinding / persistence (later layer).** Because an action carries its name + default binding in code, a
  `.inputmap` asset is an *optional override* layer (player remaps `Jump` to `Enter`) that the registry merges
  on top of the code defaults — not a required authoring step. Code defines; the asset only overrides.

### 7.4 Why NOT per-input objects (a considered-and-rejected design)
The owner first sketched each input as its own object: `class MovementInput : IInputProvider, IDisposable {
InputEvent<Vector2> Move; ctor { Move = new(); Move.Redirect(Keyboard.Move); … } Dispose(); }`. The *good*
ideas in it — code-first binding via "redirect," and a type-safe value (`InputEvent<Vector2>` not a string) —
are **kept** (§7.2). The *form* is rejected for three concrete reasons:

1. **`: IInputProvider` is a layer + name violation.** In this engine `IInputProvider` is the raw *device
   backend* (`GLInput`/`ScriptedInput`, `Abstraction/`), not a gameplay binding. Deriving a gameplay action
   from it breaks the grep-auditable layering and collides with an established name (an agent reads
   `IInputProvider` as "device backend"). The action handle derives from nothing — it's just a handle.
2. **Per-input object + `IDisposable` is an allocation/indirection multiplier.** A character has Move/Look/Jump/
   Fire/Crouch/Reload/… → one `InputEvent<T>` object + redirect list + `Dispose` lifecycle *each*, × every local
   player, invoked every frame — for zero benefit over a single `InputComponent` + `static readonly` handles.
   It violates the engine's "no needless per-frame overhead" posture and the master plan's "subtract complexity
   axes."
3. **More surface = worse for AI, not better.** A small, uniform API (`input.OnAxis(Actions.X, …)`) is learned
   once and applied everywhere; a per-input-object pattern must be re-derived (and its `Dispose`/layer kept
   correct) for each input. Few strong concepts beat many small objects — the AI-native doctrine.

The type-safe handle form (§7.2) preserves *both* good ideas (code-first redirect, type safety) with one object
per player and no per-input lifecycle.

### 7.5 The one residual nuance (prediction)
Prediction needs the owner's input *as data per tick* (to buffer + replay). So the event layer also feeds a
small per-tick input struct internally — but the **developer never sees `TryGetInput`**; they only see events.
The struct is an implementation detail of the prediction system (§8.2), not API surface. This keeps the
ergonomic event API *and* deterministic replay, without exposing the false-branch.

### 7.6 Triggers — variety beyond "on press" (the owner's "different events too" + "Unreal feels good")
The owner: *"I don't only want on-press — there must be different events too,"* and *"I love Unreal's structure;
in Unreal you do this comfortably as a Blueprint node."* The resolution adopts **Unreal Enhanced Input's full
model**: input has **two orthogonal axes**, and the variety lives in the *definition*, not the callback.

- **Axis 1 — Phase:** where in a press's life the event fires (`Started` / `Performed` / `Ongoing` / `Canceled`).
- **Axis 2 — Trigger:** the *condition* under which the action counts — `Press`, `Release`, `Hold(0.5s)`,
  `Tap(0.2s)`, `DoubleTap`, `Pulse(rate)`, `Chord(otherAction)`. (Enhanced Input also has **Modifiers** —
  `Negate`, `Swizzle`, `DeadZone`, `Scale` — that transform the *value* before it reaches the action; same
  "in the definition" placement.)

**Decision (owner-approved): the Trigger lives WITH the action's definition; the callback stays bare.** This is
exactly why Unreal *feels* comfortable — the trigger complexity is resolved in the action/mapping-context, so
the reaction (a Blueprint node, or here a C# callback) is just "it triggered → do this," with a single
`Triggered` path. Putting the trigger at the callback (`OnAction(Reload, Trigger.Hold(0.5f), cb)`) was
considered and rejected: it re-states the trigger at every bind site and loses that "resolved once" comfort.

The definition has **two equally-valid homes** (the owner picked "definition in text-asset OR code"). NOTE: the
text-asset uses the enum *value names* (`Key.R`, `PadButton.X`) as tokens — these are the same enums as the code
form, just spelled as text for the human/agent-readable asset; they round-trip to the enums, they are NOT the
rejected free-form string paths:

```text
# IA_Reload.inputmap   — TEXT asset: AI agents read/write it, players rebind it (the AI-native bend on
#                        Unreal's binary Input Action asset — our "YAML source + derived index" doctrine).
#                        Tokens are enum VALUE NAMES (Key.*, PadButton.*), validated against the enums on load.
Reload:  bindings: [Key.R, PadButton.X]            trigger: Hold 0.5s
Dash:    bindings: [Key.LeftShift]                 trigger: DoubleTap
Fire:    bindings: [MouseCtrl.Left, PadAxis.RightTrigger]  trigger: Press
```
```csharp
// OR in code — for code-first users; the .inputmap is then an OPTIONAL override layer merged on top:
public static class PlayerActions {
    public static readonly InputAction Reload = new InputAction("Reload", Button)
        .Bind(Key.R).Bind(PadButton.X).WithTrigger(Hold(0.5f));
    public static readonly InputAction Dash = new InputAction("Dash", Button)
        .Bind(Key.LeftShift).WithTrigger(DoubleTap);
}
// The reaction is identical and bare either way (no trigger parameter — already resolved in the definition):
input.OnAction(PlayerActions.Reload, Pawn.Reload);
input.OnAction(PlayerActions.Dash,   Pawn.Dash);
```

This single decision reconciles the three things that looked contradictory: **Unreal's comfort** (trigger out of
the callback), **code-first** (definable in code), and **AI-native** (definition is text an agent can author).

### 7.7 The Blueprint-node question — what's in scope, honestly
The owner: *"in Unreal you do this comfortably as a Blueprint node."* That comfort is **two separable parts**;
be honest about which this engine delivers:

1. **The visual *definition* editor** (Unreal's Input Action / Mapping Context window: dropdowns for bindings +
   triggers + modifiers). **IN SCOPE and a natural fit.** The `.inputmap` is already a text asset; an editor
   panel over it — add action, pick bindings, pick a trigger from a dropdown → writes the text — is exactly the
   engine's existing **attribute-driven inspector pipeline** (`Inspector/`). This reproduces ~80% of Unreal's
   input comfort, because the *reaction* in Unreal is usually a single `Triggered` node anyway. **Planned** as a
   `.inputmap` editor (a later P0+ editor task, not core P0).
2. **Blueprint-style visual *scripting* of the reaction** (a node graph wiring `IA_Jump → Pressed → Launch`).
   **OUT OF SCOPE.** This is an entire visual-scripting subsystem; this engine is deliberately **C# script-first**
   (collectible ALC, hot-reload). The reaction is C#: `input.OnAction(Actions.Jump, Pawn.Jump)`. A node-graph
   scripting layer is a separate, much larger product decision — not part of the input system, and not assumed
   here. (If ever pursued, it would be its own plan, on top of this API, not a prerequisite.)

So: the comfortable *definition* surface (the part that actually makes Unreal input pleasant) is reproduced via
a text `.inputmap` + an inspector-pipeline editor; the *reaction* stays first-class C#, which is this engine's
identity. The owner gets Unreal's input feel without taking on a visual-scripting engine.

### 7.8 The device layer — honest scope, but the cross-vendor mapping DB is FREE and already in-stack
Choosing our own `Key`/`PadButton`/`PadAxis` enums (§7.2) is right for backend-independence. The earlier draft
said this means "we own the HID database that Unity has" and called it big work. **That was overstated — the
cross-vendor mapping database is free, zlib-licensed, and already shipping under OpenTK today.** Corrected:

- **Cross-vendor gamepad layout = SOLVED, free.** The canonical DB is **`SDL_GameControllerDB`**
  (`gamecontrollerdb.txt`, several thousand controller models, **zlib license — explicitly commercial-OK, a
  closed-source game may ship it**, one line in third-party-notices). And we don't even ship it ourselves to
  start: **GLFW 3.3+ bundles a copy of it**, and **OpenTK 4.x already exposes it** —
  `GLFW.GetGamepadState(jid, out GamepadState)` returns the **Xbox-layout-remapped** state (A/B/X/Y, both
  sticks, triggers, D-pad), and `GLFW.UpdateGamepadMappings(dbText)` refreshes it with a current
  `gamecontrollerdb.txt`. So `PadButton.A` → physical bottom-face-button across Xbox/PS/Switch is **a few hours
  of glue, not a database project.** *(One factual check owed before relying on the "few hours" estimate: verify
  the exact `GLFW.GetGamepadState`/`UpdateGamepadMappings` method signatures against the pinned OpenTK 4.x minor
  — these can shift across OpenTK minors. The architecture doesn't depend on it; the time estimate does.)* Also
  note: GLFW's gamepad API only remaps controllers that **have a DB entry** — an unknown new controller falls
  back to raw-joystick (the "frozen DB" caveat). So "cross-vendor free" is a guarantee for **known** devices and
  a graceful degrade for unknown ones.
- **The real (small) work** is wiring our `IGamepadProvider` (in our enums) over `GLFW.GetGamepadState`, plus
  deadzones / trigger 0..1 rescale / sensitivity — **most of which already exists** in today's `Input` facade
  ([`Input.cs`](../../Abstraction/API%20Bindings/Input.cs)) and just moves behind our enums.
- **What GLFW does NOT give** (the genuine future work, if needed): rumble, gyro, touchpad, battery, LED, and a
  DB frozen at GLFW's release date. The upgrade path for those is an **SDL3 backend** (SDL3-CS / ppy.SDL3-CS,
  zlib/MIT) behind the *same* `IGamepadProvider` interface — adopt only when haptics/gyro actually matter. A
  **Steam Input** backend (free for Steam builds, requires the client) is an optional third impl for the Steam
  release (glyphs + user rebinding), again behind the same interface.

**Backend-independence is preserved AND cheap.** `IGamepadProvider` speaks our enums; the first impl calls
GLFW's bundled-DB gamepad API (zero new deps). When the DX12 endgame removes OpenTK/GLFW, the same interface
gets an SDL3 impl — and SDL *is* the upstream of that very DB, so the mapping survives the migration for free.

**Scope call:** P0 = keyboard + mouse + a standard gamepad via GLFW's bundled DB (cross-vendor mapping included,
not deferred). Multi-pad routing (split-screen → which pad = which player), hot-plug events, and rumble/gyro are
a separate **P-Input-Devices** item — real but *small*, and explicitly *not* a from-scratch HID database. This
correction matters so a future session neither under-budgets multi-pad nor *over*-budgets the (already-solved)
cross-vendor mapping.

---

## 8. NetworkManager, the tick, and single-player (L2, D5)

### 8.1 NetworkManager — the orchestrator (NOT an entity)
A plain engine object, instantiated once at bootstrap (alongside `EngineBootstrap`, like
`Physics.World`/`Input.Provider`), reachable via a thin static `Network` facade. **It is not a
`NetworkBehaviour` and never lives on an entity** (FishNet's hard rule — avoids recursive/identity tangles). It
owns:

| Sub-part | Responsibility |
|---|---|
| `TransportManager` | the socket (`ITransport`), send/recv queues, optional pipeline (encrypt/compress) |
| `ServerManager` | server authority: accept/auth connections, client list, server-side spawn, `StartConnection()` |
| `ClientManager` | local connection + known-object registry |
| object registry | netId → entity (internal; never a public netId, §3) |
| observer/interest | who sees which object |
| (`PredictionManager`, stats) | later phases |

Access: `Network.IsServer`, `Network.Spawn(...)`, `Network.StartHost()` — the same static-facade shape as
`Physics.Raycast` / `Input.IsKeyDown`. Connection start delegates (`ServerManager.StartConnection()` /
`ClientManager.StartConnection()`); "host" = both.

### 8.2 The tick — reuse the existing 60 Hz step (L2)
**We do not build a second clock.** The engine already has a fixed 60 Hz accumulator
([`SceneManager.Update` → `Physics.Advance`](../../Engine/BObject/Scene/SceneManager.cs:188)). The network tick
brackets that *existing* fixed step:

```
IterateIncoming  →  OnPreTick  →  NetworkTick (= the existing FixedTick: sample input, predict, sim)
                 →  physics step  →  OnPostTick (write dirty [Networked], send reconcile)  →  IterateOutgoing
```

Reconciliation runs as its own bracket (`OnPreReconcile` → replay buffered inputs per tick → `OnPostReconcile`)
on predicting clients. `LocalTick` (monotonic) is the replay index; `Tick` (server-approx) may fluctuate.

**Tick-rate ≠ send-rate (a distinct decision the earlier draft conflated).** "One fixed tick = the network
clock" sets the *simulation* cadence (60 Hz, bound to physics) — it does NOT mean we send a full state packet
every tick. That would blow the bandwidth budget. Like Fusion and FishNet, **send rate is separate**: simulate
at 60 Hz, but replicate state at a lower **send rate** (e.g. every 2nd/3rd tick → 20–30 Hz) and *interpolate*
on the receiver. Delta + quantization + 1-bit-unchanged (§11) shrink each packet; the send-rate divisor caps
how often we pay even that. Two sub-decisions to settle (§14): (a) the default send rate / divisor; (b) whether
physics-rate and tick-rate can ever differ (some games want 120 Hz physics, 60 Hz netcode) — for now they're
coupled, but the coupling is a choice, not a law.

**Determinism scope (important — this is NOT lockstep).** Prediction's replay (§7/P5b) requires only
**local-replay determinism**: the *same machine, same binary* replays its buffered inputs from the last
authoritative server state and must reach the same result it predicted. It does **not** require cross-machine
bit-identical simulation — we replicate *state*, not inputs (so a client and server need not compute identical
floats; the server's state simply overwrites on the next sync, and the client replays locally). **Consequence:
Bepu does NOT need to be deterministic across platforms/debug-vs-release** — a far weaker requirement than
lockstep. P5b's verify (same-machine convergence) is exactly the right test. (Stated so a future session neither
chases needless cross-machine determinism nor assumes "Bepu replays identically" untested — it must hold only
*on one machine*, which is achievable.)

### 8.3 Single-player = loopback host, same code path (D5) — but inject latency/loss from day one
SP = `Network.StartHost()` over a **loopback/offline transport** (server+client in one process). `GameMode`
still spawns, `[Networked]` still "replicates" (in-memory), `NetworkTick` still runs — **the exact same code
path** as multiplayer; only the transport collapses. Going multiplayer is a transport swap; *the game code does
not change*. `bal simulate` headless uses the same loopback path. This is the single biggest "no
SP-vs-MP-divergence" guarantee — **for the code-divergence class.**

**But be honest about what loopback does NOT test.** A naive loopback is a happy-path: zero latency, zero loss,
in-order delivery. It exercises that the *same code runs*, not that the *netcode survives the failure modes that
actually break netcode* (latency, packet loss, reordering, jitter). So latency/loss/jitter/reorder is available
**from P0** — but as a **separate decorator `SimulatedTransport : ITransport` that WRAPS any inner transport**
(loopback *or* LiteNetLib *or* Steam), NOT baked into loopback and NOT relying on LiteNetLib's own
`SimulateLatency` (which is DEBUG-build-only and library-specific). This is Mirror's
LatencySimulation-transport pattern — the decorator composes over the abstraction. With it on, the "same code"
claim is *stress-verified*, not merely *exercised*: a `bal simulate` run can replay the identical scene at
0 ms/0% and at 150 ms/5%-loss (seeded, deterministic) and assert the predicted+reconciled result converges.
This turns loopback from a convenience into a real test harness — matching the AI-operability doctrine
(deterministic, headless, diffable).

### 8.4 The `NetworkRef<T>` mechanism — how "null on despawn" actually works
§3 promised "no public netId, only typed refs that null on despawn." **A plain C# reference does not do this** —
it dangles (kept alive by GC) until collected, so a held `Pawn pawn` after despawn is a stale managed object,
not null. The "ordinary null check" only works if references to networked objects are a **handle**, not a raw
pointer:

```csharp
public readonly struct NetworkRef<T> where T : NetworkBehaviour {
    readonly int id;          // registry slot
    readonly int generation;  // bumped on despawn — stale handles detect it
    public T Value => Network.Resolve<T>(id, generation);   // returns null if generation mismatched (despawned)
    public bool IsAlive => Value is not null;
}
```

The object registry holds `(slot → object, generation)`. Despawn bumps the slot's generation; any `NetworkRef`
captured before that now resolves to null (generation mismatch) instead of a dangling object. This is the
standard generational-handle pattern. **Consequence:** networked object references in `[Networked]` fields and
RPC parameters are `NetworkRef<T>`, not `T` directly — the framework boxes/unboxes at the API edge so user code
mostly writes `T` but the *stored/wired* form is the handle. Exact ergonomics (implicit conversion? `.Value`
access like Fusion's `NetworkBehaviourRef`?) is an open question (§14) — but the *mechanism* is mandatory, not
optional, and §3's guarantee depends on it existing.

### 8.5 Two lifecycles, one object — the intersection contract (the part the single-object model didn't cover)
A `NetworkBehaviour` *is-a* `Behaviour`, so **two lifecycle strands fire on the same object**: the existing
Unity-style strand (`OnAttach → OnBegin → OnEnabled → Tick → OnDisabled → OnDetach`) and the network strand
(`OnSpawned → OnStartX → NetworkTick → OnDespawned`). The earlier draft defined each *single* object correctly
but never wrote how the two **interleave**, nor how enable/disable interacts with spawn/despawn. That contract:

**The canonical linearization** (one object, server-auth spawn; predicted spawn is the §8.5.1 exception):

| # | Callback | Strand | Fires when | A networked author may… | …may NOT |
|---|---|---|---|---|---|
| 1 | `OnAttach` | Unity | component added (edit + play) | register with renderer/editor sets (local, non-net) | touch any networked state (not spawned) |
| 2 | **`OnSpawned`** | **net** | net identity live + **baseline applied atomically** | read `[Networked]` state, subscribe, init net-logic, spawn predicted children | assume *referenced* objects exist (§8.5.2) |
| 3 | `OnStartServer/Client/LocalPlayer` | net | per role, right after Spawned | role-specific net setup (e.g. `SetupInput` on owner) | — |
| 4 | `OnBegin` (`HasBegun`) / `OnEnabled` | Unity | once active in play (existing flag) | **local, cosmetic** init only (VFX, audio, UI) | net-logic — that belongs in Spawned |
| 5 | `NetworkTick` | net | every fixed tick **while spawned AND active** | mutate state (server) / predict (owner) | run if disabled (see disable rule) |
| 6 | `Tick` | Unity | every frame while active | local cosmetic/visual update | authoritative net state |
| 7 | `OnDisabled` | Unity | locally disabled | pause local cosmetics | despawn / change net state |
| 8 | **`OnDespawned`** | **net** | net identity removed (see exit matrix §8.5.3) | **unsubscribe everything from Spawned**, release | — |
| 9 | `OnDetach`/`OnDestroy` | Unity | component/entity torn down | final local cleanup | net ops (already despawned) |

**The load-bearing rule (resolves the §5 terminology slip):** *net-logic lives ONLY in `OnSpawned`/`OnDespawned`;
`OnBegin`/`OnEnabled`/`OnDisabled` are for LOCAL cosmetic state only.* So §5 Phase 3's "OnSpawned → OnEnabled"
was a loose write: a **plain `Behaviour` has no `OnSpawned`** — for it Phase 3 is just today's `OnBegin →
OnEnabled`. For a `NetworkBehaviour`, `OnSpawned` (step 2) precedes `OnBegin/OnEnabled` (step 4). Network never
leaks onto non-network objects (§11's scoping holds). *This single rule + table closes the §5 defect.*

**How this maps onto the EXISTING machinery (the B1/B2 integration point — verified against real code).** Today
[`Behaviour.FireEnable`](../../Engine/BObject/Objects/Behaviour.cs:113) fires `OnBegin`→`OnEnabled` **atomically
in one call** — there is no seam to slot `OnSpawned` "between" them, and `OnEnabled` is *unconditional* (no flag
stops a second fire). Therefore the spawn path must **drive the two strands separately**, NOT lean on a single
`FireEnable`:
1. spawn path calls `OnSpawned` + `OnStartX` itself (steps 2–3), marking the component "net-begun";
2. THEN the Unity strand runs `OnBegin/OnEnabled` (step 4) exactly once — at `StartPlay` via the Phase-3
   `FireBegin` (with the skip/dedup guard of §5 so it can't double-fire `OnEnabled`); at runtime via the
   suppression-flag path of §5 (`Network.Spawn` suppresses `Entity.Attach`'s eager `FireEnable`, then runs the
   ordered strands).
This is the concrete reason §5 needs the small guard + suppression flag rather than "free `HasBegun` reuse." The
table's ordering is a *contract the spawn path enforces*, not something the current `FireEnable` produces on its
own.

**Enable/disable while spawned (the NGO footgun, now defined):** disabling a *spawned* networked object is
**local-only and cosmetic** — it does NOT despawn, does NOT stop replication, does NOT change authority. It
pauses the *Unity* strand (`Tick`/rendering) but the *net* strand keeps going: the server still owns the truth,
state still replicates, `NetworkTick` still runs (state must stay consistent regardless of a client's local
disable). There is no "half-alive spawned-but-disabled" net state — net liveness is `IsSpawned`, *independent*
of `IsActive`. To actually remove it from the network you call `Despawn` (server), never `SetActive(false)`.
This is the explicit answer NGO never gave cleanly.

#### 8.5.1 Predicted spawn — the exception that the "OnSpawned = baseline delivered" model does NOT cover (P5)
The table above assumes **server-authoritative spawn**: `Network.Spawn` on the server → baseline replicates →
`OnSpawned` fires atomically with the baseline. Correct and safe for pawns (§6). **But it breaks for predicted
gameplay spawns:** a fired bullet must appear *instantly* on the owner, before a server round-trip — so the
client spawns a *predicted* copy with **no server baseline yet**, the server spawns the authoritative one, and
the two **reconcile** (the prediction is confirmed-and-linked, or rolled-back-and-destroyed). This is one of the
hardest lifecycle problems in netcode and it **invalidates the clean "OnSpawned = baseline delivered"
invariant** — a predicted object has no baseline at spawn. Open questions it forces: does `OnSpawned` fire on
the predicted copy, the confirmed copy, or twice? How does state carry across the predict→confirm link?
(Fusion handles this with a dedicated predicted-spawn mechanism.) **This is a hidden chunk of P5's weight** — it
is added as a P5 sub-phase (**P5f — predicted spawn/despawn + reconcile-link**) and an open question (§14). The
server-auth model ships first (P0–P4 + pawns); predicted spawn is explicitly deferred, not assumed.

#### 8.5.2 Spawn ORDERING (the symmetric problem to despawn — §3 solved despawn, not this)
§3 killed dangling references on *despawn* (`NetworkRef` nulls). The *spawn* side has the mirror hazard: spawn
messages arrive **in arbitrary wire order**, so object A spawning with a reference to B may run its `OnSpawned`
*before* B's spawn message arrives — A sees B as not-yet-present. Two-part contract:
- **At `StartPlay`,** §5's phases give ordering (GameMode → pawns → rest), so initial cross-refs resolve.
- **At runtime (mid-game spawn),** there is **no phase ordering** — so the rule is: **`OnSpawned` may NOT assume
  a referenced networked object already exists.** A `NetworkRef<T>` to a not-yet-spawned object resolves to null
  (same mechanism as despawn, §8.4) and the author re-checks on a later tick or on the referenced object's own
  `OnSpawned`. This makes "runtime spawn" and "StartPlay spawn" have *different* guarantees, stated explicitly
  so a future session doesn't assume StartPlay's ordering holds at runtime.

#### 8.5.3 Teardown is NOT one symmetric exit — the exit matrix
"Symmetric teardown" (Spawned↔Despawned) is optimistic: teardown has **multiple exit paths, and not all fire
`OnDespawned`.** The contract:

| Exit path | Fires `OnDespawned`? | Then |
|---|---|---|
| explicit `Network.Despawn(obj)` | **yes** | normal path; unsubscribe here |
| scene unload (networked object in unloaded scene) | **yes** (framework despawns first) | then `OnDetach`/`OnDestroy` |
| connection lost (object owned by/visible to that conn) | **yes** on affected peers | server may keep authority copy |
| app quit / hard process exit | **best-effort** — may NOT fire | rely on OS cleanup, not `OnDespawned` |
| entity `Destroy` WITHOUT despawn (misuse) | framework should `Despawn` first, then destroy | a guard, or it's a leak |

**Rule for authors:** put **all** Spawned-paired teardown in `OnDespawned`, and treat `OnDetach`/`OnDestroy` as
local-only final cleanup. The framework guarantees `OnDespawned` for every *graceful* exit (rows 1–3); only hard
process-kill (row 4) is best-effort. This is where subscription leaks hide if left undefined — now defined.

#### 8.5.4 Pooling — `OnSpawned` must FULLY init, assume nothing from the ctor (P2/P3)
Real netcode pools networked objects (spawn is hot, GC pressure). In a pool the **C# object is reused** — the
ctor does NOT re-run; only `OnSpawned`/`OnDespawned` re-fire. So any state a networked author sets in the ctor
(or assumes persists) **leaks across pool reuse.** Our "`Awake`/ctor = no-network zone, `OnSpawned` = init" rule
is already the right ground (`OnSpawned` re-runs on reuse) — but the **contract must be explicit**: *`OnSpawned`
must fully (re)initialize the object assuming nothing survives from a previous use; `OnDespawned` must reset it
to a clean poolable state.* Bound here so a future session's pooling (P2/P3) doesn't reintroduce ctor-assumption
leaks.

**Pooling × `NetworkRef` invariant (the intersection neither §8.4 nor §8.5.4 spelled out — and it's a §3-class
trap if missed).** When a *pooled* C# object comes back with a new netId/slot, must stale `NetworkRef`s to its
*previous* identity null out? Yes — and the design already supports it *because `NetworkRef<T>` keys on
`(id, generation)`, NOT on the object pointer* (§8.4's struct holds `id`+`generation`, never the managed ref).
So the same reused object can serve two distinct network-identities over its life, and a handle captured against
the old slot+generation resolves null after the despawn bumped that generation — correct. **Make this explicit
so P2/P3 pooling doesn't "optimize" `NetworkRef` to cache the object pointer** — that shortcut would resurrect a
use-after-pool-reuse bug, exactly the §3 class the handle exists to kill. *Invariant: the handle binds to
slot+generation, never to the object.*

#### 8.5.5 Reconnect window (interacts with §9.8 ConnectionToken)
On disconnect, §9.8's `ConnectionToken` keeps the pawn alive for a TTL so the player can reclaim it. The
lifecycle question: during that orphan window, does the pawn **despawn+respawn** (firing
`OnDespawned`/`OnSpawned`, losing subscriptions) or **stay spawned with ownership transferred** to
server/nobody (firing `OnOwnershipChanged`, keeping subscriptions)? **Decision (recommended): stay spawned,
ownership → server, `OnOwnershipChanged` fires; reconnect transfers ownership back.** This keeps `OnSpawned`
subscriptions alive across a reconnect (no teardown), which is what a player expects ("I dropped and came back
to my character, not a fresh one"). Confirmed as a §14 item.

### 8.6 Hot-reload × networking — the seam neither §7.7 nor §11 looked at (a real blind spot)
The engine hot-reloads game scripts through a **collectible ALC** (§7.7, §11), and the source generator scans
`NetworkBehaviour` subtypes to emit typeId/methodId hashes + bit-packed field layout (§11). **Game-defined
`Pawn`/`PlayerController`/`InputAction`s live in that reloadable assembly.** The two systems are correct
*separately* but their intersection produces two real hazards, **verified against the actual reload code**
([`GameScripts.Unload`](../../AssetPipeline/Scripting/GameScripts.cs:107),
[`EngineBootstrap.ReloadGameScripts`](../../Engine/Bootstrap/EngineBootstrap.cs:192)):

**8.6.1 Wire-format stability across reload — and the LOGIC-drift the hash CANNOT catch.** If a hot-reload
adds/reorders a `[Networked]` field, the generator's field layout and `(typeId, methodId)` hashes **shift**. In
SP loopback this is tolerable (reload ≈ restart). But D5's whole thesis is "SP == MP same code path" — and **you
cannot hot-reload a peer's wire-format mid-session**; the other side desyncs.

**The honest position (corrected — a layout-hash is NOT a session-safety guarantee).** A layout-hash catches
*field* changes, but a reload can change `NetworkTick`'s **simulation logic** (a movement speed, a formula)
without touching any `[Networked]` field — the hash still matches, the handshake passes, and the two peers now
**simulate differently.** Server-authoritative state-replication masks this partially (the server overwrites on
the next sync), but on a *predicted owner* it shows as **permanent misprediction → constant rubber-band** (every
tick predicted wrong, every sync corrected). So:
- **The rule:** live reload is **NOT session-safe for ANY `NetworkBehaviour` in MP** — only a **coordinated
  reload (all peers on the same build)** is safe. (Cosmetic/local-only-logic — code that never runs inside
  `NetworkTick`/replication — remains reload-safe, but the framework can't prove a given edit is purely that.)
- **The layout-hash is a GUARD, not a guarantee:** it stamps into the wire handshake so an *accidental*
  field-layout mismatch becomes an **explicit error instead of a silent desync** — it does NOT make reload
  session-safe (it can't see logic drift). Positioned as accident-detection, not safe-reload machinery (the
  cheaper, more honest of the two options — vs hashing the whole `NetworkTick` IL, which is brittle and
  over-aggressive).
- SP / `bal simulate` is unaffected — reload there is a restart.

**8.6.2 Collectible-ALC unload leak — the #1 ALC footgun, and we're adding two new triggers.** For the
collectible ALC to unload, **every reference from a host-side (non-collectible) static to a script-ALC type
must be dropped.** The existing reload flow already knows this — `GameScripts.Unload`'s own comment says the
caller "must have cleared the scene, the component registry, and the volume stack first, or the old assembly
lingers," and `ReloadGameScripts` does exactly that (scene clear → `VolumeManager.ResetStack` → `Unload` →
`ComponentRegistry.Build` *rebuilt from scratch*). **But this plan adds TWO new host-side static roots that pin
script-ALC types:** (a) §7.3.1's `InputAction` ctor self-registration into a fusion registry, and (b) §11's
network registration table (`typeId → serializer/dispatch`). If either is a persistent host-side
`static Dictionary` holding script-ALC `Type`s/delegates, **the ALC never unloads** — a leak you'd debug months
later. **Contract to add:** both registries MUST be cleared+rebuilt at the reload boundary, exactly like
`ComponentRegistry.Build` is re-run in `ReloadGameScripts` — i.e. they join the "clear before Unload" list. This
is the load-bearing fix; without it P0's self-registering input actions pin the ALC on the first hot-reload.

*Symmetry note: the registry isn't the only host root.* Any **host-side UI that holds an `InputAction` handle**
(a rebind screen caching `MyActions.Move` from the old build) ALSO pins the old ALC. Not urgent (the rebind UI
is P0+), but the §14 0c test must check **every host surface that retains a script-ALC handle**, not just the
registry — clearing the registry alone is necessary, not sufficient.

**Why this is P0-critical, not later:** self-registering `InputAction`s ship in **P0**. So the registry-clear
contract (8.6.2) must be settled before P0 coding, alongside the §14 Item-0 gate (it becomes Item 0c).

---

## 9. Edge-case catalog (root cause → eliminating decision)

Each class: symptom → architectural root cause → the API decision that eliminates it (mapped to L1–L4 / the §3
footgun list). Where it can't be eliminated, the canonical clean handling.

1. **Lifecycle/timing races** (SyncVar default in wrong callback, RPC for unspawned object). *Root:* state and
   callbacks on different paths. *Kill:* spawn delivers the baseline atomically *before* the first user callback
   (`OnSpawned`); `Awake` is a no-network zone. *Residue:* "want the initial value as a change event" → read
   current value in `OnSpawned`, subscribe for *subsequent* changes.
2. **Ownership/authority confusion** (`IsOwner` on host, "who runs this"). *Kill:* L3 — split State/Input
   Authority; derive `IsOwner` from `Owner==LocalConnection` so host is correct.
3. **Spawn/despawn & late joiners** (missing state, RPC not replayed, dangling netId). *Kill:* L1 (state in
   properties, delivered at spawn) + no public netId (typed refs null on despawn). *Residue:* truly punctual
   late-join events → opt-in `[Rpc(To.All, BufferLast)]`, else model as state.
4. **Prediction/reconciliation** (rubber-band, non-deterministic replay). *Kill:* L2 (one fixed tick) + a
   *mandatory* typed `Replicate`/`Reconcile` contract where the server never reconciles; predict only
   input-owned objects, interpolate the rest. *Residue:* big-gap resimulation cost → cap lookback / spread
   across frames.
5. **RPC hazards** (reliable-buffer overflow, ordering vs state, trust-the-client). *Kill:* L1 (prefer
   properties) + owner-checked-by-default RPCs + typed object-ref params resolved by the framework. *Residue:*
   genuinely punctual events stay RPCs, bandwidth-budgeted.
6. **State sync semantics** (missed intermediate values, in-place edit not dirtying). *Kill:* document
   "properties are last-value-wins per tick"; mutation goes through a dirtying method; strong per-object tick
   coherence (all of an object's properties land the same tick → no `health`/`isDead` split). *Residue:*
   transition sequences → model as an event stream, not a scalar.
7. **Time/tick** (frame-rate desync, spiral of death, drift). *Kill:* L2 — one fixed tick = network clock,
   clamp the accumulator (~0.25 s cap), sync to server tick not wall-clock.
8. **Disconnect/reconnect** (orphaned pawn, zombie connections). *Kill:* identity = persistent
   `ConnectionToken`/`PlayerRef`, not transport id → reconnect reclaims the pawn by token; TTL heartbeat for
   zombies. *Residue:* P2P host migration is hard → prefer dedicated/listen-server topology.
9. **Cheating/validation** (speed hack, teleport). *Kill:* L4 — client sends intent, server simulates; a client
   *cannot* replicate a cheated value (no API path). The re-simulation reuses the reconcile loop (cheap), but
   explicit input bounds-checks (max speed/teleport distance) are still per-game code you write — NOT free.
   *Residue:* aimbot/wallhack send valid input → out of engine scope (anti-cheat).
10. **Scene transitions** (load-order id races). *Kill:* server-driven synchronized scene loading + one-scene-
    per-object-lifetime (don't migrate post-spawn). *Residue:* per-connection pre-join scenes → promote shared
    content to global scenes.

---

## 10. What the existing infrastructure gives for free

Because every per-entity type derives from `Behaviour` (via `NetworkBehaviour`) and scene-wide ones from
`SceneBehaviour`:

- **`ComponentRegistry.Build`** discovers them by base-type reflection at bootstrap → Add Component menu (or
  `HideFromAddMenu`).
- **`ComponentReflection`** serializes their members → scenes round-trip them. **`[Networked]` ⟂
  `[NotSerialized]`:** `[Networked]` = wire replication, `[NotSerialized]` = YAML persistence. A `[Networked]`
  field defaults to *also serialized* (authored initial values save) unless `[NotSerialized]`.
- **Inspector drawer pipeline** renders them via the shared `DrawerRegistry`; a `[Networked]` *decorator*
  (`IPropertyDecorator`) shows a net badge / read-only-on-proxy — the house pattern, no new type-switch.
- **`bal schema`/`bal scene add-component`/MCP/pipe** get the gameplay framework with zero wiring — the agent
  surface inherits it.

**One honest exception to "free":** `GameState` (§2) is the *only* type that replicates without being on an
entity — via the `IReplicated` interface the network tick collects. That collection + dispatch is **bespoke
machinery, not free** (today's `SceneBehaviour` has no tick — §2 note). Budget it explicitly in P7. Everything
*entity-based* is genuinely free per the list above; `GameState`'s entity-less replication is the carve-out.

## 11. Codegen — zero reflection in the hot path

Standing engine rule: **zero reflection per-frame** ([memory: pref-no-reflection-render-hotpath]). Netcode is
the worst place to break it. Every C# framework (Mirror Weaver, NGO ILPostProcessor, FishNet, Fusion CodeGen)
solves this with compile-time codegen. We use a **Roslyn source generator** — the modern .NET 9 form, and a
*better* fit than FishNet's IL weaving because it produces **browsable, debuggable** generated C# and slots into
the existing `dotnet build` → collectible-ALC script pipeline (FishNet's biggest pain is un-debuggable woven
IL). The generator scans `NetworkBehaviour` subtypes at compile time and emits per type:

- `SerializeState`/`DeserializeState` over `[Networked]` fields — bit-packed, quantized (~mm), delta against the
  last ACK'd baseline (unchanged objects ≈ 1 bit).
- RPC dispatch stubs — integer-hash `(typeId, methodId)` → method, **no runtime reflection**.
- A registration table built **once at load** (the only sanctioned reflection).

`NetworkBehaviour` being a distinct base scopes the generator — plain `Behaviour`s are never touched and pay
nothing.

---

## 12. Transport & layering (grep-auditable, per CLAUDE.md)

### 12.1 What the network runs on — the transport decision (researched, decisive)
**`ITransport` is our own interface; the question is what goes behind it first.** Decision, after a current
(2026) survey of every realistic C# option:

- **First backend (P0–P3): LiteNetLib.** The only candidate that is *simultaneously* **pure-managed** (trivial
  .NET 9 Windows deploy, **no native build** — matters because the DX12 migration is *removing* native deps, not
  adding them), **MIT**, **actively maintained in 2026** (v2.1.4, May 2026 — confirmed alive, not a dead repo),
  and **feature-complete**: 5 delivery modes + multiple channels (we map Reliable→`ReliableOrdered` for
  RPCs/spawns, Unreliable→`Unreliable` for snapshots), plus built-in NAT punch for player-hosted games.
  Decisively, **FishNet's default transport (Tugboat) IS LiteNetLib** — so the prediction/reconciliation model
  we're emulating is validated on this exact backend. (Its one gotcha — per-`SendTo` `IPEndPoint` GC — is a
  .NET-socket-API issue, mitigated by span overloads, and a non-issue at server-authoritative scale.)
- **Rejected alternatives, briefly:** **ENet-CSharp** — native build tax + NuGet frozen at 2022 + *no* NAT
  punch, for *more* channels than we need; **Riptide** — nice `Notify` mode but maintenance cooling, no ordered-
  reliable guarantee, no NAT punch; **kcp2k** — a reliability layer with fewer channel semantics + no NAT punch;
  **raw UDP roll-your-own** — congestion control is a multi-month trap LiteNetLib already de-risked; **QUIC
  (`System.Net.Quic`)** — **no managed datagram API until ≥.NET 11**, stream-only = head-of-line blocking =
  wrong for "latest-snapshot-wins." None beat LiteNetLib for our constraints.
- **Secondary path (Steam build): Steam GameNetworkingSockets via Facepunch.Steamworks** — relayed, encrypted,
  DDoS-protected P2P with no server hosting, as a *second* `ITransport` impl. Steam-gated (can't back headless
  dedicated servers or `bal simulate`), so it's a swap-in for the Steam SKU, never the primary. **Caveat — a hole
  in "every phase headless-verifiable":** because Steam GNS can't run under `bal simulate`, Steam-specific
  transport bugs escape the headless discipline. Mark the Steam transport as the **one path requiring manual
  test** (relay/NAT/encryption can only be validated live), not headless — an explicit, named exception to the
  otherwise-uniform "headless-verify every phase" rule.
- **Loopback (D5): a hand-written `ITransport`**, independent of all the above — passes buffers in-process for
  single-player + `bal simulate`. Ships first (P0).
- **Latency/loss/jitter: a separate `SimulatedTransport : ITransport` DECORATOR** that wraps any inner transport
  (§8.3) — not baked into loopback, not LiteNetLib's DEBUG-only sim.
- **Reliability = the library's; wire format = ours.** We do NOT build connection/ARQ/congestion (the dangerous
  part — take LiteNetLib's). We DO own the bit-packed delta-snapshot encoding (§11) — it rides as an opaque
  `ReadOnlySpan<byte>` payload inside a LiteNetLib packet on the chosen channel. `ITransport` stays honest:
  `Send(connectionId, ReadOnlySpan<byte>, Channel)` + connect/disconnect/receive events — no snapshot semantics
  leak into it, so swapping the backend never touches snapshot code.

### 12.2 Layering
```
Abstraction/Networking/    ITransport, NetworkRole enum, BitWriter/BitReader, IReplicated   (BCL + OpenTK.Math)
Engine/Gameplay/           GameMode, Pawn, PlayerController, PlayerState, HUD, GameState     (Abstraction + Engine)
Engine/Gameplay/Input/     InputComponent (event source), action-map binding, OUR device enums
Engine/Networking/         NetworkBehaviour, NetworkObject, replication tick, prediction, NetworkManager
Networking/LiteNetLib/     the ONLY place allowed to reference LiteNetLib                    (like Physics/Bepu)
Networking/Loopback/       in-process ITransport + SimulatedTransport decorator             (BCL only)
BallisticEngine.SourceGen/ Roslyn generator project, referenced as an analyzer
```
LiteNetLib is quarantined behind `ITransport` exactly like `Physics/Bepu` / `AssetPipeline`'s Assimp. The
loopback + simulation decorator are BCL-only. A later `Networking/SteamGNS/` would be the same quarantine for
the Steam build.

---

## 13. Phasing — and an HONEST weighting of where the risk lives

**The phases are NOT equal weight.** A flat bullet list would lie about the risk distribution. **P0–P4 are
tractable** (well-understood, each a few weeks). **P5 alone is 50%+ of the total difficulty and risk — it is
where netcode projects die** — and it is *not* "multi-week," it is realistically **multi-month**. The whole
track is best read as "P0–P4: a quarter; P5: a project of its own; P6–P8: tail." Don't enter P5 with P4 energy.
**Start only after the DX12 GI work is committed.**

### Tractable foundation (P0–P4)
- **P0 — Ordered init + event input + the MINIMAL network spine, single-player loopback.** *(Scope corrected: a
  final review found P0 is NOT independent of P1 — `PlayerController : NetworkBehaviour`, and `SetupInput` is
  gated on `HasInputAuthority`/`IsOwner`, which live on `NetworkBehaviour`. So a skeletal `NetworkBehaviour` +
  `IsOwner`/`IsSpawned` + `OnSpawned`/`OnDespawned` MUST exist in P0, even with no socket. P0 and the
  role/identity skeleton ship together.)* Deliverables: `GameMode`/`Pawn`/`PlayerController`/`HUD` types; a
  **skeletal `NetworkBehaviour`/`NetworkObject`** (identity, `IsOwner`/`IsSpawned`, `OnSpawned`/`OnDespawned` —
  in single-player loopback `IsOwner` is trivially true, but the *gate* exists); the §5 phase runner **with the
  B1/B2 fix** (separate-strand drive + dedup guard + `Network.Spawn` suppression flag); the §8.5 lifecycle
  contract; the §7 input system (`InputAction`+our enums, `InputComponent`, `SetupInput` gated on `IsOwner`);
  loopback transport + the `SimulatedTransport` latency/loss decorator (§8.3). **Verify (3 gates):** (a) a
  `GameMode` scene spawns/possesses a controllable pawn headlessly via `bal simulate`; (b) **the NARROW
  byte-identity invariant** — a scene *containing no framework components* serializes and renders byte-identical
  to today. *(NOT "registry byte-diff = 0" — that's unsatisfiable: framework types derive from `Behaviour`, so
  `ComponentRegistry.Build` (§10) MUST discover them the moment the assembly loads, growing the Add-Component
  menu / `bal schema` / serialization schema. The registry legitimately gains the new types; the invariant is
  that this changes **no existing scene's bytes**, not that the registry is unchanged. Corrected from the
  earlier over-strict S1 wording, which was unsatisfiable.)* (c) an isolated 2–3-callback ordering test proves
  `OnSpawned` precedes `OnBegin/OnEnabled` and `OnEnabled` never double-fires (the B1/B2 harness). *Still the
  owner's core ask, still no socket — just honestly including the identity skeleton it structurally needs.*
- **P1 — Full roles + replication-readiness + `NetworkRef<T>` + ownership transfer** over loopback — promote the
  P0 skeleton to the full `HasStateAuthority`/`HasInputAuthority`/`IsProxy` model (per the §4d.1 truth-table —
  *settle that table first, the host-`IsProxy` corner is load-bearing for the verify*) + the generational handle
  of §8.4 + the multi-pawn proxy distinction + **`TransferOwnership`/`RemoveOwnership` (§4d) so `OnOwnershipChanged`
  is reachable from game code**. **Verify:** roles resolve correctly per the §4d.1 table for host-own-pawn vs a
  second simulated pawn (esp. host-`IsProxy = false` for the other pawn); `TransferOwnership` fires
  `OnOwnershipChanged` and flips `IsOwner`/input-authority; a `NetworkRef` to a despawned pawn reads null.
- **P2 — Source generator + `[Networked]` state** (bit-packed, delta, **separate send-rate** §8.2). **Verify:**
  `[Networked] int Health` mirrors; profiler shows no per-tick reflection allocs.
- **P3 — LiteNetLib transport.** Two processes; state crosses the wire under the ~1200-byte packet budget at the
  chosen send-rate.
- **P4 — `[Rpc(To.X)]`** reliable/unreliable, owner-gated server RPCs.

### The hard core — P5, broken into its real sub-phases (each its own headless harness)
P5 is *not one bullet.* It is five, and the project's success hinges here:
- **P5a — Predict-only-self.** Owner applies input locally each tick, buffered by input-sequence. No server
  correction yet. **Verify:** owner moves with zero input lag in loopback.
- **P5b — Server reconcile.** Server simulates authoritative, returns state + last-processed-seq; client snaps +
  **replays unacknowledged inputs**. **Verify:** under injected latency, owner converges to server with no
  permanent drift (the isolated replay harness, like the mesh-SDF test, *before* engine integration).
- **P5c — Proxy interpolation.** Non-owned pawns render ~100 ms in the past between two snapshots. **Verify:** a
  watcher sees a smooth remote pawn under loss/jitter.
- **P5d — Misprediction / rollback handling.** Visible-correction smoothing, divergence detection, the
  `ReplicateState` (current vs replayed) distinction. **Verify:** a forced misprediction corrects without a
  jarring snap.
- **P5e — Resimulation cost control.** Cap tick lookback / spread a big-gap replay across frames so a packet gap
  doesn't hitch. **Verify:** a 500 ms gap doesn't stall the frame.
- **P5f — Predicted spawn/despawn + reconcile-link (§8.5.1).** The hidden weight: a client spawns a *predicted*
  bullet instantly (no baseline yet), the server spawns the authoritative one, the two reconcile (confirm-link
  or rollback-destroy). Breaks the clean "OnSpawned = baseline delivered" invariant. **Verify:** a fired bullet
  appears instantly on the owner, links to the server's copy on confirm, and vanishes cleanly on a mispredicted
  shot — no duplicate, no orphan.

### Tail
- **P6 — Spawn/late-join baseline** (a late joiner gets current state atomically at spawn).
- **P7 — `GameState`/`PlayerState`** replicated; HUD binds; `ConnectionToken` reconnect (reclaim pawn by token).
- **P8 — split into TWO independent subsystems (applying the same "a flat bullet lies" discipline as P5).** Lag
  comp is not one thing, and one half can ship without the other:
  - **P8a — Server-side lag compensation** (collider rollback / favor-the-shooter): the server rewinds other
    pawns to what the shooter saw, runs the hit, restores. Its own substantial subsystem; needed only when
    hitscan combat matters.
  - **P8b — Interest management / relevancy / dormancy**: who-sees-what culling, static-object dormancy,
    per-connection relevancy — a *separate* subsystem (a scale/bandwidth concern, not a hit-detection one).
    Legitimately doable independently of P8a (and vice-versa).

Every phase carries a headless test. The serializer (P2) and the prediction replay (P5b) are the prime
candidates for an *isolated* correctness harness (the `%TEMP%\bal-*-test` scratch-console discipline) *before*
engine integration — same discipline as the mesh-SDF baker.

### 13.1 Schedule — AI-EXECUTED, not human-developer (the correct calibration)
**Execution model (owner-decided): the AI (Claude) writes ALL of it; the owner directs.** So human-developer
estimates are the WRONG reference — they overshoot massively. Evidence on THIS codebase: the **OpenGL→DX12+DXR
migration took ~5–6 hours** of AI-executed work (a months-long job by human-developer reckoning). The estimate
must be recalibrated to AI throughput, with one honest exception (P5).

**Build approach:** transport is LiteNetLib (done); replication/prediction we WRITE by applying published
techniques — Gambetta (prediction+reconcile), Valve `cl_interp` (interpolation), Unreal's 4-role model,
Fiedler's fixed-timestep — onto our `Entity` substrate. NOT invented; applied. Unity-bound libs can't be reused.

**Two AI-throughput regimes (the key distinction):**
- **Regime 1 — mechanical/generative (AI ~50–100× human):** bit-pack serializer, RPC dispatch, ownership flags,
  Roslyn generator, transport binding, role truth-table, lifecycle wiring. This is **almost all of
  P0–P4 + P6–P7** — exactly the shape that made the DX12 migration fast (large but systematic, "apply a proven
  API methodically").
- **Regime 2 — experimental/convergence (AI ~3–10×, sometimes less):** **P5 prediction.** The hard part isn't
  *writing* code, it's the **observe→hypothesize→fix loop** ("why is it rubber-banding, where does the
  misprediction diverge"). This needs a running sim measured repeatedly. **Our AI-operability infra is a big
  lever here** (`bal simulate` deterministic headless, `SimulatedTransport` latency injection, isolated replay
  harness → the AI loops "run, measure, diff, hypothesize" by reading numeric time-series, not watching pixels —
  far faster than a human) — but the loop is still serial, and some netcode bugs (float determinism, edge-case
  desync) are genuinely subtle. P5's convergence time is the ONE real unknown.

| Block | Human-dev | **AI-executed (this is the real plan)** |
|---|---|---|
| Item-0 gate + P0 | 4–7 weeks | **~1–2 days** |
| P1–P4 (roles, state, transport, RPC) | ~3 months | **~3–6 days** |
| **P5 (a–f: prediction)** | 3–6 months | **~1–3 weeks** ← the only real variance |
| P6–P7 (late-join, GameState/reconnect) | ~6 weeks | **~2–4 days** |

- **Playable MP without prediction (P0–P4 + P6–P7):** **~a few days to 1 week.**
- **Full FPS-quality (through P7, prediction included):** **~2–4 weeks** (driven by how P5 converges).
- **Absolute full (+ P8a lag-comp + P8b interest-mgmt):** **~3–5 weeks.**

**The one honest caveat (P5 vs DX12):** the DX12 migration was pure Regime 1 — big but mechanical, so it flew.
P0–P4 here are ALSO Regime 1 and will be similarly fast. But **P5 is Regime 2** — prediction can look "working"
while carrying a subtle desync, and catching that is a run-measure loop. The headless+deterministic infra makes
it *much* faster (not zero). So P5 could be 1 week or 3 — that's the entire schedule variance. **Networking
starts only after the DX12 GI track is committed (D6).**

---

## 14. Open questions to settle before P0 (P0-CRITICAL first)

**ITEM 0 — THE GATE (a final review's blockers; settle these as a ~1-page mechanism + a callback-ordering test
BEFORE touching engine code):**
0a. **The `StartPlay` phase-runner + `Network.Spawn` lifecycle contract (B1/B2, §5/§8.5).** `HasBegun` gates only
    `OnBegin`, NOT `OnEnabled` (which fires unconditionally in `FireEnable`) — so the naive "reuse `HasBegun`"
    double-fires `OnEnabled`. Settle: (i) Phases 0–2 drive ONLY the net strand (`OnSpawned`/`OnStartX`) and mark
    components so (ii) Phase 3's `FireBegin` is the single `OnBegin/OnEnabled` site, with a dedup guard so it
    can't re-fire `OnEnabled`; (iii) runtime `Network.Spawn` sets a suppression flag (like `SuppressPlayLifecycle`)
    so `Entity.Attach`'s eager `FireEnable` doesn't run before `OnSpawned`. Prove with a 2–3-callback isolated
    ordering test. *Hard precondition for P0.*
0b. **P0 includes the minimal `NetworkBehaviour`/`IsOwner`/`IsSpawned` skeleton (B3).** `PlayerController : NetworkBehaviour`
    and `SetupInput` gates on `IsOwner` — so the identity/ownership gate must exist in P0 even with no socket
    (trivially-true in loopback). P0 = ordered-init + input + this skeleton, shipped together. *Restated in §13.*
0c. **Reload-safety of the input + network registries, and wire-format stability across hot-reload (§8.6).**
    Self-registering `InputAction`s ship in P0, so this is P0-critical. Settle + PROVE with a test (engine code
    untouched, like 0a/0b): (i) the `InputAction` fusion registry (§7.3.1) and the network registration table
    (§11) are **cleared+rebuilt at the reload boundary** — they join the existing "clear scene + registry +
    volume stack before `GameScripts.Unload`" list ([`ReloadGameScripts`](../../Engine/Bootstrap/EngineBootstrap.cs:192)),
    so the collectible ALC actually unloads (no host-side static pinning script-ALC types); (ii) a hot-reload
    that changes a `NetworkBehaviour`'s replicated layout produces an **explicit error** via a layout-hash in
    the wire handshake, not a silent MP desync (SP/`bal simulate` reload = restart, unaffected). *Hard
    precondition for P0 — without it the first hot-reload pins the ALC and/or silently desyncs MP.*

**These are also P0-critical or block a near phase:**
1. **Binding syntax — final single form.** Confirmed: the **enum form** (`.Bind(Key.W, Swizzle)`, our own
   `Key`/`PadButton`/`PadAxis`/`MouseCtrl`) is the source of truth; the `.inputmap` text-asset uses the enum
   *value-name tokens* (`Key.R`), validated on load; **no free-form string paths anywhere.** Raw-key tier
   `OnKey(Key.F4,…)` also uses our `Key`; only the legacy global `Input.IsKeyDown` keeps OpenTK's `Keys` until
   the DX12 endgame migrates it. (Was a 3-syntax defect; now unified — keep it unified.) *P0.*
2. **`NetworkRef<T>` ergonomics AND resolve cost** — the generational-handle *mechanism* (§8.4) is mandatory;
   open is (a) the surface: implicit `T`↔`NetworkRef<T>` conversion vs explicit `.Value` (Fusion's
   `NetworkBehaviourRef` style)? and (b) **the cost**: `Value => Network.Resolve(id, gen)` does a registry
   lookup on *every* deref — dereffing a ref inside `NetworkTick` would violate §11's "no needless hot-path
   overhead." Decide: per-tick cached resolve (validate generation once, reuse the object pointer for the tick)
   vs a slot-direct array index. *Blocks P1.*
3. **Send-rate vs tick-rate** (§8.2) — default send-rate / tick divisor (e.g. 60 Hz sim, 20–30 Hz send), and
   whether physics-rate may ever differ from tick-rate. **NOT just a bandwidth knob — it's load-bearing for
   P5b/P5e:** at 60 Hz sim / 20 Hz send, the client buffers input every tick but gets corrections every ~3
   ticks, so each reconcile replays ~3× the send-interval; the input-buffer size, `LocalTick`↔server-`Tick`
   mapping, and last-processed-seq granularity all depend on this divisor. Pick it wrong and the *reconcile
   window* breaks, not just bandwidth. **UP-link and DOWN-link are SEPARATE, ASYMMETRIC knobs — do not conflate
   them into one "send-rate."** State **down** (server→client) can be the low send-rate (20–30 Hz) + interpolate.
   But input **up** (client→server) MUST be **per-tick (60 Hz), batched** — the server simulates authoritatively
   every tick, so it needs *every* tick's input; if input only went at the 20 Hz down-rate, the server would be
   **input-starved** (nothing to simulate the in-between ticks with). So a client sends ~3 ticks' input in one
   packet at the send cadence, but never *drops* ticks. Conflating these is a functional bug in P5a/P5b, not a
   tuning miss. *Blocks P2/P3 AND P5a/P5b/P5e.*
4. **Loopback latency/loss injection** (§8.3) — confirm it's a P0 transport feature, as a `SimulatedTransport`
   decorator (recommended) so the "same-code" claim is stress-verified, not just exercised. *P0.*
5. **Lifecycle linearization (§8.5) — confirm the contract.** The two-strand table (OnAttach→OnSpawned→
   OnStartX→OnBegin/OnEnabled→NetworkTick→OnDisabled→OnDespawned→OnDetach) + the load-bearing rule "net-logic
   only in Spawned/Despawned, Begin/Enable = local cosmetic." Plus the three sub-contracts: disable-while-spawned
   is local-only cosmetic (no despawn, replication continues); runtime-spawn does NOT get StartPlay's ordering
   (§8.5.2); teardown exit-matrix (§8.5.3) — `OnDespawned` for every graceful exit, best-effort on hard kill.
   *P0/P1 — this is the just-written intersection contract; confirm it before building either strand.*

**Lower-stakes (mostly P5+):**
6. **Predicted spawn (§8.5.1)** — does `OnSpawned` fire on the predicted copy, the confirmed copy, or twice; how
   does state carry across the predict→confirm link? The hidden P5 weight (now P5f). *P5.*
7. **Reconnect window (§8.5.5)** — on disconnect, pawn stays spawned with ownership→server (recommended, keeps
   `OnSpawned` subscriptions alive) vs despawn+respawn? *P7.*
8. **Pooling contract (§8.5.4)** — confirm "`OnSpawned` fully re-inits, assumes nothing from ctor; `OnDespawned`
   resets to clean poolable state." *P2/P3.*
9. **Engine vs game movement** — ship a basic predicted `CharacterPawn` (recommend yes, P5) or leave movement to
   game code?
10. **Split-screen / multiple local players** — N local `PlayerController`s on one client, each `InputComponent`
    bound to a different pad? Role model supports it; confirm scope. (Couples to the §7.8 device layer.)
11. **Host topology** — listen-server default (indie-friendly), dedicated optional? (Recommend listen-server.)
12. **Device layer scope (§7.8)** — confirm P0 = keyboard+mouse+one standard pad via GLFW's bundled
    `SDL_GameControllerDB` (cross-vendor mapping is FREE/in-stack, *not* deferred). Only multi-pad routing,
    hot-plug, and rumble/gyro (SDL3 backend) are the separate **P-Input-Devices** item.
13. **`[Networked]` collections** — fixed-capacity (Fusion) vs growable (FishNet `SyncList`)? Affects the delta
    encoder.
14. **Relevancy/interest transition lifecycle (§8.5 gap, P8b).** When an object leaves and re-enters a client's
    area-of-interest (AOI culling), what fires on the proxy? Two models: (a) it **despawns for that client**
    (fires `OnDespawned` — but misleading: the object still lives, the client just can't see it, and it wrongly
    tears down `OnSpawned` subscriptions), or (b) a **separate `OnInterestLost`/`OnInterestGained`** pair that
    keeps the object's lifecycle intact while pausing replication. Relevancy ≠ disconnect — do NOT route it
    through the §8.5.3 "connection lost" row. **Recommend (b)** so AOI culling doesn't spuriously fire
    `OnDespawned` and break the §8.5 teardown contract. *P8b — but decide before writing interest management, or
    it silently violates the teardown contract §8.5 built so carefully.*
15. **Role truth-table (§4d.1) + `IsProxy` host-corner** — `IsProxy ≡ !HasStateAuthority && !HasInputAuthority`
    (so host-`IsProxy = false` for every object). **Settle the §4d.1 table BEFORE P1's role-resolution verify** —
    that test is unwriteable without the host-corner definition. *Blocks P1.*

---

## 15. One-paragraph summary for a future session

We are **adding an opt-in Unreal/FishNet-style gameplay framework on top of the existing Unity-style
`Behaviour`/`Entity` substrate**, designed to be **shape-complete** (the wrong machine/write/input path is
unrepresentable) so edge cases cease to exist rather than get handled. New types (`GameMode`, `Pawn`,
`PlayerController`, `PlayerState`, `HUD`, `GameState`, `NetworkBehaviour`, `NetworkObject`) derive from
`Behaviour`/`SceneBehaviour`, so the editor/serializer/CLI/MCP discover them free. Play-start runs a fixed phase
order **`GameMode → Player → HUD → everyone-else`** (no `GameMode` ⇒ byte-identical to today). **Input is
event-based and possessor-routed** (`SetupInput` on the owner only; a proxy's events never fire — no
false-branch footgun) and brings controller support along free. **GameMode spawns the default Pawn *or*
possesses a scene-placed one** (D4); spawning is a server-authority op, which is why GameMode is server-only.
Networking is **server-authoritative, state-replicated**, with four load-bearing decisions (properties-over-RPC,
one-fixed-tick-as-clock, State≠Input authority, server-authoritative-with-prediction) that kill 8 of 10
edge-case classes. **Single-player = loopback host running the identical code** (no SP/MP divergence;
`bal simulate` uses it too). The no-reflection rule is honored by a **Roslyn source generator** over
`NetworkBehaviour`. Transport = **LiteNetLib** behind `ITransport`, quarantined like `Physics/Bepu`. Built in
measured phases P0 (ordered-init + event-input, loopback) → P8 (lag-comp), each headless-verifiable, **starting
only after DX12 GI is committed.**
```
