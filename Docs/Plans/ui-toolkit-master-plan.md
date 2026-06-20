# UI Toolkit — Master Plan (fix everything, UITK-quality-or-better)

> Source: `ui-toolkit-audit-report.md` (52 confirmed findings + parity gaps). Goal: close ALL of it.
> Ordering is by DEPENDENCY, not severity — many "bugs" are blocked on a missing architecture; fixing
> the architecture first makes the dependent fixes fall out cleanly instead of as hacks.
>
> Each phase ends BUILDING + with a headless proof (the R1–R4 pattern: build a tree, render/solve,
> assert pixels/values). Worktree-isolated; portable backend discipline preserved (see
> `ui-toolkit-dx12-renderer-portable.md`). DX12 backend stays in `BallisticEngine.DX12/UI/`.

## Dependency map (why this order)

```
P1 Backend bug-fixes ───────────────┐ (independent; makes R1–R4 solid before anything builds on them)
P2 Resolved-style architecture ─────┼─► unblocks :hover revert, inheritance, var(), transitions, !important
P3 Input subsystems ────────────────┼─► unblocks ALL interactive controls
P4 Layout completeness ─────────────┤   (measure invalidation, wrap, gap, grid, transform-hit-test)
P5 Controls library ────────────────┘   needs P2+P3+P4
P6 Effects (shadow/blur/rounded-clip) — needs P1 backend
P7 Data-binding — needs P5 (INotifyValueChanged on controls)
P8 Tooling (hot-reload, bal ui, debugger) — needs P2 (resolved style to introspect)
P9 i18n / a11y — last; needs P5 text controls
```

---

## P1 — Backend bug-fixes (R1–R4 hardening)   [independent, do first]

Fix every confirmed DX12-renderer finding so the live path (R5) is built on solid ground.

- **P1.1 Multi-font correctness** (high): segments must carry WHICH atlas, not just slot 0. Bind font
  atlases to distinct persistent slots; CloseSegment records the atlas slot; EnsureAtlas caches per
  family (no per-glyph-run re-upload). `Dx12UIRenderer.cs:474,241`.
- **P1.2 SRV heap overflow → grow or page** (high): past 256 textures, grow the heap (or flush+restart
  a sub-batch) instead of silently dropping. `:575`.
- **P1.3 Kill per-frame GPU+GC churn** (high): cache gradient ramps by content hash; reuse a persistent
  staging buffer for ramp/image uploads (the VB/IB path already does this). `:531`.
- **P1.4 Text shadow/glow** (high): implement the plumbed TextStyle shadow (offset+blur pass) — second
  glyph pass behind, or SDF spread glow. `:234-291`.
- **P1.5 Multi-line text** (high): DrawText + a measure path must honor `\n` (and later wrap from P4).
- **P1.6 Scissor scale = target/canvas mapping** (med): derive scissor scale from the same mapping the
  VS uses (target dims / canvas dims), not the independent `scale` field. `:712`.
- **P1.7 SDF AA uses SdfPx** (med): derive edge width from SDF spread mapped to screen, not raw fwidth;
  crisp at scale extremes. `UI.hlsl:89`.
- **P1.8 NaN/Inf guards** (med): clamp/skip non-finite rects before scissor int-convert + quad emit.
- **P1.9 Rounded-corner clip** (med→P6): SDF mask clip for `overflow:hidden` on rounded containers
  (scissor can't). Can land here or in P6; tracked in P6.6.
- **P1.10 Image ScaleToFit/Crop** (med): real object-fit UV math (needs source aspect — query resource).
- **P1.11 slot0 seed leak + clip-stack imbalance reset + zero-size early-out** (low): tidy-ups.

## P2 — Resolved-style architecture   [the keystone]   ✅ DONE (P2.5/P2.9 deferred)

> Status: P2.1/2.2/2.3/2.4/2.6/2.7/2.8/2.10 implemented + proven (16/16 headless tests). `StyleResolver`
> resolves from scratch (defaults → inherit → matched[normal] → inline[normal] → matched[important] →
> inline[important]); :hover/:active/:focus apply AND revert; var()/!important/combinators/hsl/named work.
> Deferred: P2.5 transitions (transition parse is a no-op for now — real tween bridge needs the P5 state
> flow), P2.9 cascade perf bucketing (correctness first; optimize once profiled).


Replace the write-only `Style` accumulator with a computed-style model: matched-rule-set → resolved
style with a revertible baseline. This single change unblocks the cluster of "cascade" bugs.

- **P2.1 Computed-style model**: element keeps (a) base/default style, (b) the ordered matched rules,
  (c) inline. Resolve = recompute from scratch each restyle, never mutate-in-place. `Style.Reset()` +
  recompute. Fixes "additive cascade never reverts" (3 critical findings).
- **P2.2 Restyle-on-state**: AddToClassList/RemoveFromClassList/state change → mark element restyle-dirty
  → re-resolve. Fixes `:hover/:active/:focus` dead-on-arrival (critical).
- **P2.3 Property inheritance**: color/font-*/letter-spacing/text-align/visibility/white-space inherit
  to children (CSS/UITK semantics). "Set typography once on root."
- **P2.4 var() + custom properties + :root token store**: Claude `data.js` themes are `:root` palettes.
- **P2.5 CSS transition → UIAnimator bridge**: substrate exists; wire USS `transition` to tween on
  resolved-value change.
- **P2.6 Real combinators `> + ~`** + `:not()`/attribute/`:nth-child` selectors. Current downgrade-to-
  descendant produces WRONG matches.
- **P2.7 `!important` tier** + specificity correctness.
- **P2.8 Multiple stylesheets** per document + `src`/`@import`.
- **P2.9 Cascade perf**: rule bucketing by class/type/name + parsed-declaration cache (no per-element
  List+sort+re-tokenize every pointer-move).
- **P2.10 Value-parser completeness**: border-radius 2/3-val, min/max `%`/`none`, `rgb()%`/space/`hsl()`,
  full named-color table, pseudo-namespace separation, percentage/auto inset.

## P3 — Input subsystems   [unblocks all controls]

- **P3.1 Pointer subsystem**: PointerMove stream + pointer capture + per-button (L/M/R) dispatch +
  double-click + composite-bubble press target + hover bubbling.
- **P3.2 Focus subsystem**: focusedElement, Focusable/tabIndex, Tab/Shift-Tab ring, Focus()/Blur(),
  `:focus` class wiring (so P2 can style it).
- **P3.3 Keyboard subsystem**: KeyDown/Up/TextInput char events, NavigationSubmit (Space/Enter), Esc.
- **P3.4 Event phases**: real capture/trickle phase; `Handled` suppresses default activation.
- **P3.5 HitTest honors overflow:hidden + transform** (translate/scale→rect, rotate→OBB). Fixes
  clicking invisible clipped rows + translated-button mis-pick.
- **P3.6 Wheel/scroll events** (feeds ScrollView in P5).

## P4 — Layout completeness   ✅ DONE (P4.7 grid deferred)

> Status: P4.1/4.2 measure invalidation (font size/family/letter-spacing/wrap change + font-version →
> RefreshMeasureIfStale, flushed in UpdateFrame), P4.3 word-wrap (FontAtlas.MeasureWrapped + Label wrap on
> AtMost), P4.4 MeasureMode plumbed through the facade, P4.5 gap/row-gap/column-gap, P4.6 aspect-ratio,
> P4.8 white-space + text-overflow — all implemented + proven (6/6 tests). P4.9: Yoga already skips clean
> subtrees internally (the real cost); C#-side propagate is a cheap rect copy, left as-is.
> DEFERRED — P4.7 CSS Grid: the vendored Yoga port has grid STYLE setters but NO grid algorithm in
> CalculateLayout (flexbox only), so `display:grid` would be a fake feature. Real support needs porting
> Yoga's grid solver (large); flexbox + gap + wrap covers the common dashboard layouts meanwhile.


- **P4.1 Measure invalidation**: re-measure on font-size/family/letter-spacing change, not just text.
- **P4.2 Font-loaded-after-layout** triggers re-measure (UIFonts.Version → mark measured nodes dirty).
- **P4.3 Multi-line wrapping**: measure honors availW (MeasureMode plumbed through the facade), returns
  multi-line height; pairs with P1.5 render.
- **P4.4 MeasureMode plumb** (Exactly/AtMost) at LayoutNode facade.
- **P4.5 `gap`/`row-gap`/`column-gap`** (Yoga Gutter enum exists; wire Style+USS).
- **P4.6 aspect-ratio** Style property + USS parse (facade already supports).
- **P4.7 CSS Grid** (Yoga grid enums exist; expose display:grid + template-cols/rows).
- **P4.8 white-space / text-overflow:ellipsis / direction(RTL stub)**.
- **P4.9 Dirty-subtree skip** on the C# propagate (HasNewLayout) — perf.

## P5 — Controls library   [largest missing modality; needs P2+P3+P4]

Staged by dependency + frequency. Each = element + USS default style + INotifyValueChanged where apt.

- **P5.1 ScrollView** (viewport+content+scrollbar, wheel+drag) — needs P3.1/P3.6.
- **P5.2 TextField / text input** (caret, selection, edit, IME-ready) — needs P3.2/P3.3.
- **P5.3 Toggle / Checkbox / Radio**.
- **P5.4 Slider / SliderInt / ScrollBar** — needs pointer capture P3.1.
- **P5.5 Dropdown / PopupField / EnumField** — needs overlay layering + focus.
- **P5.6 Foldout, Tabs**.
- **P5.7 ListView / virtualized list** — the UITK perf control for big feeds.
- **P5.8 ProgressBar, Tooltip, ContextMenu, ModalDialog**.

## P6 — Effects   ✅ CORE DONE (rounded-clip/real-blur/nine-slice deferred)

> Status: P6.1 box-shadow (SDF soft-falloff shadow behind the box, CSS box-shadow parse) + P6.4 bold/
> italic (font-weight/style → UIFonts variant by name convention, falls back to base) implemented +
> proven (5/5). P6.2 backdrop-blur: plumbed end-to-end (Style.BackdropBlur, DrawBackdropBlur, CSS
> backdrop-filter); REAL blur needs a readable copy of the composited frame (read+write same RT is
> illegal) — Dx12UIRenderer.SetBackdropSource is the hook for the renderer-merge; until then it draws a
> faint frost so it's never a silent no-op. DEFERRED: P6.6 rounded-corner clip (current clip is
> rectangular scissor; rounded needs a per-quad shader clip-rect+radius — a vertex-format addition),
> P6.3 nine-slice/border-image, P6.5 outline/cursor/filter/text-transform (niche; add on demand).


- **P6.1 box-shadow** element drop shadow (SDF-expanded blurred rect behind).
- **P6.2 Backdrop blur / glassmorphism** (sample UI/scene backbuffer, separable blur) — Claude signature.
- **P6.3 Nine-slice / border-image / tiled bg**.
- **P6.4 Subpixel/gamma-correct text AA; bold/italic atlas variants (font-weight/style)**.
- **P6.5 outline, cursor, pointer-events:none as USS, text-transform, filter**.
- **P6.6 Rounded-corner SDF clip mask** (the real fix for P1.9).

## P7 — Data-binding   [needs P5]

- **P7.1 INotifyValueChanged<T>** + RegisterValueChangedCallback on controls.
- **P7.2 One/two-way binding to a data source + `binding-path`**.
- **P7.3 Observable collections → ListView item binding**.

## P8 — Tooling / agent introspection   ✅ CORE DONE (CLI/panel wiring + WorldSpace deferred)

> Status: P8.2/P8.3 CORE — UIIntrospect.ToJson (tree → type/name/classes/resolved-rect/resolved-style),
> .Pick (point→element), .ToTreeText; proven 6/6. This is the shared engine of `bal ui dump` and the
> in-editor UI debugger. P8.5 Expand/Shrink scale modes done. P8.1 hot-reload: UIDocument.Rebuild()
> re-reads UXML/USS via TextResolver (public) — the editor calls it on focus-regain/file-watch (same
> AssetChangeWatch pattern as scripts). DEFERRED (separate exe wiring, not engine-core): the actual
> `bal ui` CLI verb (BallisticEngine.Cli) + the ImGui debugger panel (BallisticEngine.Editor) — both just
> call UIIntrospect; P8.4 WorldSpace renderer (render-to-texture quad + ray-pick) is renderer-merge-bound
> like R5/R6.


- **P8.1 Live UXML/USS hot-reload** (focus-regain + file-watch; reuse AssetChangeWatch).
- **P8.2 `bal ui` headless verbs**: dump tree + resolved style + layout boxes as JSON; screenshot diff.
- **P8.3 UI debugger panel** (pick element → resolved style + box model + matched rules).
- **P8.4 WorldSpace renderer** (quad + ray-into-quad picking).
- **P8.5 ScaleWithScreenSize Expand/Shrink modes**.

## P9 — i18n / a11y   [last]

- **P9.1 RTL + complex-script shaping (HarfBuzz-class) + font fallback chains + emoji**.
- **P9.2 Accessibility roles/labels/focus-order export**.

---

## Execution

Run phase-by-phase (P1 → P9). Within a phase, items are mostly parallel-safe; across phases, respect the
dependency map. Each item: implement → build clean → headless proof → note in this doc. R5 (live player
hook) lands AFTER P1 (solid backend); R6 (visual editor canvas) folds into P8.
