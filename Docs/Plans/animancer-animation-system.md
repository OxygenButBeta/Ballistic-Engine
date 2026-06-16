# Animancer-style animation system + Mixamo workflow

A **code-driven** animation system (no state-machine graph asset) for third-person characters, built on the
engine's existing skeletal-animation foundation. You decide which animation plays from C# — `Play(clip)`,
`CrossFade(clip, fade)`, a directional blend space for locomotion — instead of wiring a graph of states and
transition arrows. This is the Animancer model.

Branch `animation-system` (worktree off `dx12-renderer`). Engine code in `Engine/Animation/`; the third-person
controller is a game script in `SampleProject/`.

## The pieces

| Type | What it does |
|---|---|
| `AnimationMixer` | Blends N clips by per-input weights (the "smart blend" — Unity's blend tree). |
| `BlendSpace2D` | A 2D directional blend space (gradient-band interpolation) — drive it with a movement vector for 8-way locomotion. |
| `RootMotion` | Extracts the root bone's per-frame delta so the animation moves the character (Unity's Apply Root Motion). |
| `AnimancerComponent` | The component you put on a character. `Play` / `CrossFade` / `PlayMixer` / `PlayBlendSpace` / `ApplyRootMotion`, all from code. |
| Clip retargeting | `AnimationClip.RetargetTo(skeleton)` remaps a clip onto another rig **by bone name** — the Mixamo workflow. |

There is **no state machine** and no graph asset. A `Behaviour` script reads input, sets the blend-space
parameter / calls `Play`, and that's the whole controller.

## Mixamo workflow (the recommended asset path)

The engine plays generic bone-name clips and has no Humanoid/Avatar retargeting, so do NOT use Unity-Humanoid
packs (their clips are muscle-space and can't be imported). **Use Mixamo** — it bakes every animation onto one
consistent skeleton.

### 1. Download the character
- mixamo.com → pick a character → **Download** → Format **FBX**, Pose **T-pose**. This is your character mesh
  (with skeleton). Import it into `Assets/` like any model — it becomes a skinned mesh + skeleton.

### 2. Download animations
- With the character selected, pick an animation → **Download** → Format **FBX**, **Skin: Without Skin**,
  FPS 30, **In Place: ON** if you'll drive movement with code, **OFF** if you want root motion.
- Each animation is a separate FBX on the **same Mixamo skeleton** (same bone names). Import each — the engine
  writes a sibling `<Anim>_Animations/<clip>.banim` (now **v2**, carrying bone names for retargeting).

### 3. Why it just works across separate files
- Clips are sampled by bone INDEX, and separate FBX imports can order bones differently. The system fixes this
  with **name-based retargeting**: `AnimancerComponent.Play(clip)` (and `Retarget(clip)` for clips you add to a
  mixer/blend space) remaps the clip onto the character's skeleton by bone name the first time it's used
  (cached). Same-skeleton clips pass through unchanged — zero cost.
- Caveat: it matches by exact bone name. Mixamo rigs share names (`mixamorig:Hips`, …), so this holds. A clip
  whose bone the character lacks is dropped; a v1 (nameless) clip is assumed same-order.

### 4. Root motion vs in-place
- **In-place clips + code movement** (simplest, most responsive): `ApplyRootMotion = false`; your controller
  moves the transform by velocity and just plays the matching locomotion animation.
- **Root motion** (authored, foot-locked): `ApplyRootMotion = true`; the engine reads the root bone delta each
  frame and moves the entity. Use the root-motion (In Place OFF) clip variants.

## Code sketch — third-person locomotion (no state machine)

```csharp
public class ThirdPersonController : Behaviour {
    public AnimationClip Idle, WalkF, WalkB, WalkL, WalkR, RunF;
    AnimancerComponent anim;
    BlendSpace2D locomotion;

    protected override void OnBegin() {
        anim = GetComponent<AnimancerComponent>();
        locomotion = new BlendSpace2D();
        locomotion.Add(anim.Retarget(Idle),  new Vector2(0, 0));
        locomotion.Add(anim.Retarget(WalkF), new Vector2(0, 1));
        locomotion.Add(anim.Retarget(WalkB), new Vector2(0, -1));
        locomotion.Add(anim.Retarget(WalkL), new Vector2(-1, 0));
        locomotion.Add(anim.Retarget(WalkR), new Vector2(1, 0));
        locomotion.Add(anim.Retarget(RunF),  new Vector2(0, 2));
        anim.PlayBlendSpace(locomotion);
    }

    protected override void Tick(in float dt) {
        Vector2 move = ReadMoveInput();             // -1..1 strafe/forward from keys or stick
        locomotion.SetParameter(move * SpeedScale); // idle->walk->run blends automatically
        MoveCharacter(move, dt);                    // velocity-driven (in-place clips)
    }
}
```

To do a one-shot (jump, attack) over locomotion: `anim.Play(JumpClip, fade: 0.1f)` then
`anim.PlayBlendSpace(locomotion)` when it finishes (or crossfade back). That decision lives in your code.

## Verification (all headless, no renderer needed)
- `AnimationMixer` / `BlendSpace2D` / `RootMotion` / retargeting: 38/38 in the `%TEMP%/bal-mixer-test` harness.
- `AnimancerComponent` end-to-end in the real engine: `bal simulate <scene> --watch CesiumMan:AnimancerComponent.Time`
  (Time advances at 1/60 s, root motion moves the transform, 0 errors).
- **GPU skinning on DX12** (so the character DEFORMS on screen): code complete (`GBufferSkinned.hlsl` +
  skinned PSO in `DX12HDRenderer`), but the DX12 **headless** render path is broken renderer-side (black frames),
  so visual confirmation is pending the editor or a fixed headless path.
