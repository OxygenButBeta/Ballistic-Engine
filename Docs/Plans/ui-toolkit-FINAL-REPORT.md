# UI Toolkit — Final Report (autonomous build session)

Branch: `worktree-ui-toolkit` (worktree, off `dx12-perf-radical`). Full solution builds clean (5 projects,
0 errors). Every phase committed separately + proven with headless tests (pixel/value assertions, real
GPU where rendering is involved). **Not merged** — left for your review (per your instruction).

## What this was

Started from "UI Toolkit-like system on top of the existing UI/" → an adversarial 52-finding audit →
a 9-phase master plan to reach **"Unity UI Toolkit quality or better, clean, no rough edges"** → built
it all, autonomously, to completion.

## Phases delivered (all committed, all tested)

| Phase | What | Tests |
|---|---|---|
| **P1** DX12 backend + hardening | IUIRenderer DX12 impl (rect/rounded/border/clip/text/gradient/image); multi-font, gradient-ramp cache, text-shadow, multi-line, scissor-mapping, SDF-AA, NaN guards | 7/7 |
| **P2** Resolved-style architecture | from-scratch resolve (defaults→inherit→cascade→inline); :hover/:active/:focus apply **and revert**; var(); !important; real `> + ~ :not()` combinators; multi-sheet; hsl/named/radius parsers | 16/16 |
| **P3** Input subsystems | pointer move + **capture** + per-button + double-click; **focus** (Tab ring, Esc); **keyboard** + TextInput; wheel; clip+transform-aware hit-test | 16/16 |
| **P4** Layout completeness | measure invalidation (font/wrap/load); word-wrap; gap; aspect-ratio; MeasureMode; white-space | 6/6 |
| **P5** Controls library | **12 controls**: ScrollView, TextField, Toggle, Slider, Dropdown, Foldout, TabView, **virtualized ListView**, ProgressBar, Tooltip, ContextMenu, Modal + INotifyValueChanged + overlay layer | 9/9 |
| **P6** Effects | box-shadow (SDF); bold/italic font variants; backdrop-blur plumbing (+hook) | 5/5 |
| **P7** Data-binding | two-way control↔source, binding-path (cached reflection, nested paths), ObservableList→ListView auto-refresh | 9/9 |
| **P8** Tooling | UIIntrospect (tree→JSON: type/name/class/resolved-rect/resolved-style + Pick) — the `bal ui`/debugger core; Expand/Shrink scale modes; hot-reload hook | 6/6 |
| **P9** i18n/a11y | font fallback chains; basic RTL; accessibility roles+labels (semantic-tree export) | 6/6 |
| **E2E** | USS :root vars + .class + controls + flexbox + GPU render, all together | 7/7 |

**Total: ~87 headless assertions, all green.** Plus a real bug caught by the E2E test and fixed
(imperative `Style.*` overrides were wiped by USS resolve → now preserved as the highest-precedence layer,
Unity element.style parity).

## Deferred (deliberate scope calls, documented — NOT half-done)

These were left out because doing them right needs something orthogonally large, and a half version would
be worse than an honest gap. All are noted in `ui-toolkit-master-plan.md`:

- **P4.7 CSS Grid** — the vendored Yoga port has grid *setters* but no grid *algorithm* in CalculateLayout
  (flexbox only). Real support = porting Yoga's grid solver. Flexbox+gap+wrap covers the common layouts.
- **P6.2 real backdrop blur** — needs a readable copy of the composited frame (can't read+write one RT);
  `Dx12UIRenderer.SetBackdropSource` is the hook for renderer-merge. Faint frost meanwhile (never silent).
- **P6.6 rounded-corner clip** — current clip is rectangular scissor; rounded needs a per-quad shader
  clip-rect+radius (vertex-format addition).
- **P8.4 WorldSpace render + `bal ui` CLI verb + ImGui debugger panel** — renderer-merge / separate-exe
  wiring; the engine-core pieces (UIIntrospect) are done and tested, the wiring is mechanical.
- **P9 full complex-script shaping** (Arabic joining, Indic reordering) — needs a HarfBuzz-class native
  shaper. Latin/CJK/emoji + RTL block order work via the fallback + direction support.
- **R5/R6** — the portable DX12 backend is built + proven headless, but the live player hook (R5) and the
  visual UI-builder canvas (R6) wait on the in-flight renderer work merging back (your original plan).

## Portability preserved

The DX12 backend stays isolated in `BallisticEngine.DX12/UI/` + `Shaders/UI/` (2 files), leaning only on
stable helpers (Device/OffscreenTarget/ShaderCompiler/EmbeddedShaderSource), submitting its own
ExecuteSync — no edits to existing DX12 renderer files. One integration call (composite→present) is all
that's needed when the renderer merge lands. Engine `Input` facade gained `PushTypedChar`/`TryReadTypedChar`
(text input) — the only engine-core touch outside `UI/`.

## Where things live
- `UI/` — engine UI library (105 .cs): Elements, Style (resolved pipeline), Layout (Yoga facade),
  Input, Controls (12), Binding, Tooling, Rendering walker + IUIRenderer.
- `BallisticEngine.DX12/UI/Dx12UIRenderer.cs` + `Shaders/UI/UI.hlsl` — the portable GPU backend.
- `Docs/Plans/ui-toolkit-*.md` — audit report, master plan (per-phase status), portable-backend plan.

## Suggested next steps (your call)
1. Review the branch; merge to your UI line of work.
2. After the renderer-perf branch merges: do R5 (live player hook) + R6 (visual builder canvas) + the
   deferred renderer-bound effects (real backdrop blur, WorldSpace).
3. Wire the `bal ui` CLI verb + ImGui UI-debugger panel onto UIIntrospect (mechanical).
