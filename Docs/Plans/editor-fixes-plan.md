# Plan — Editor Fixes (EF1–EF16)

Status: **PLAN ONLY — do not implement until the user gives the command.**
Branch: `dx12-renderer` (current). Editor exe: `BallisticEngine.Editor/`. Renderer: DX12 (`BallisticEngine.DX12/`).
Source: validated live against the codebase 2026-06-17 (file:line grounded below — not guessed).

Goal: fix a batch of editor UX/correctness defects the user reported, plus a **root-and-branch
theme overhaul** (the headline ask). Each chunk lists the **validated root cause**, the **fix
sketch**, and a **DoD/oracle**. Small fixes are byte-localized; the two big ones (EF5 theme,
EF9 windowing) are split into sub-chunks.

---

## Manual multi-chat execution protocol (READ FIRST)

This plan is run **one chat per chunk**, by hand: each chat implements exactly ONE chunk, commits it,
then emits a ready-to-paste prompt for the NEXT chat. You (the user) paste that prompt into a fresh chat.
This keeps each chat's context small and the history bisectable.

**The chunk pointer lives in this section.** Always trust git + this line over any chat's memory:

> ### ▶ NEXT CHUNK: **EF5d** (Theme: type/spacing tokens everywhere)
> Last committed chunk: **EF5c** · Branch: `dx12-renderer`
>
> **EF5 identity decision RESOLVED → (i) faithful UE5** (cool graphite + blue-grey shell + a single
> restrained azure highlight, NO warm accent). The azure accent `0x3D8BD4` (EditorPrefs default) is KEPT;
> the acceptance bar for the whole EF5 series is "looks like UE5". EF5d implements against this identity.
>
> **EF5c note (just landed):** panel chrome polished by routing the stark stock section/divider widgets
> through the existing `EditorDecoration` primitives (no hand-rolled DrawList chrome). (1) **Section headers:**
> every `ImGui.SeparatorText(...)` in the inspector cluster — the shared `ImGuiComponentGui.Header` adapter
> (so ALL attribute-driven `[Header]` sections at once), `InspectorPanel` (Render Features list + the
> `[Header]` attribute path), `ComponentPreviews` (11 sections), `AssetInspectors` (2) — now calls
> `EditorDecoration.DrawSectionHeader(...)`, which draws a Caption-font, RowCaption-colored label + a
> palette hairline trailing rule instead of ImGui's framed default-look box. (2) **Structural dividers:** the
> toolbar→content `ImGui.Separator()` in `ConsolePanel`, `HierarchyPanel`, and `AssetBrowserPanel`
> (nav-bar→content) now use `EditorDecoration.DrawDivider()` (the quieter `BorderLight` hairline). (3)
> `DrawSectionHeader` was given symmetric built-in vertical pad (`SectionPadY=4f`) + a `PushFont(Caption)`
> so a section title reads as a quiet group rule, not a loud header; the immediately-preceding redundant
> `ImGui.Spacing()` calls were dropped at the converted sites (the pad is now owned in one place). **NOT
> byte-identical** (deliberate visual harmonization — section titles recede, rules are palette-consistent);
> behaviour unchanged. Scope held to EF5c's Inspector/Hierarchy/Assets/Console panels — `StatsPanel`/
> `TagsLayersPanel`/`SettingsPanel` `SeparatorText`/`CollapsingHeader` are OUT of scope and untouched; the
> modal-dialog internal separators in `AssetBrowserPanel` (227/333) + inline favourites separator (498) were
> left as stock (dialog internals, not panel chrome). Touched only the 6 panel files (`AssetBrowserPanel`,
> `ConsolePanel`, `HierarchyPanel`, `InspectorPanel`, `Inspector/Adapters/ImGuiComponentGui`,
> `Inspector/Preview/ComponentPreviews`, `Inspector/AssetInspectors/AssetInspectors` — 7 files) +
> `EditorDecoration.cs` (all fully mine). Editor csproj builds 0-error (clean `--no-incremental` scratch
> dir); reflection oracle EXIT=0 (all 18 suites green). NOT visually verified yet — batched into the EF5a–d
> human-screenshot checkpoint (GPU-hang rule: no relaunch-loop).
>
> **EF5b note (just landed):** the panel "bypass offenders" (hand-typed `SysVec4` color literals that gave
> the UI its raw feel) are now routed through `EditorTheme`. Added a **SEMANTIC tokens** block to
> `EditorTheme.cs` (named by ROLE, not hue): `Error`/`Warning`/`Success`, `PrefabBlue`/`RowChild`/`IconMuted`,
> `PrimaryAction`(+Hovered/Active), `FolderTint`/`FolderTintDim`, the `LogLevel[]` info/warn/error ramp,
> `Hairline`/`TreeGuide`, and `PopupBg`/`InputBg` (modal-prompt surfaces, ramp-derived). Routed: `ConsolePanel`
> (`LevelColors[]`→`EditorTheme.LogLevel`), `HierarchyPanel` (prefab-blue/child-dim/eye-off/tree-guide),
> `AssetBrowserPanel` (prompt titles→`Text`, invalid-name→`Error`, popup/input bg, green Create→`PrimaryAction`,
> favourite + folder-tree gold→`FolderTint`/`FolderTintDim`), `StatsPanel` (border→`Hairline`), `BuildPanel`
> (success/fail summary→`Success`/`Error`), `VolumeProfileEditor` (disabled-override warn→`Warning`). **Tokens
> chosen so this is NOT byte-identical** (it deliberately retunes a handful of slightly-off literals to one
> coherent family — e.g. console warn `0.95,0.80,0.30`→`0xF2CC4D`, child-dim `0.72,0.74,0.78`→`0xB8BDC7`); the
> diff is *visual harmonization*, behaviour unchanged. What stays as JUSTIFIED literals (per the DoD grep):
> alpha-only overlays (`(0,0,0,0)`, `(1,1,1,0.0x)` ghost-button hovers, white-alpha watermark icons),
> alpha/scale DERIVATIONS of an already-token color (`(tint.X,tint.Y,tint.Z,α)`, `(color.X*0.6,…)`,
> `(accent.X,…,α)`), the no-icon-font FALLBACK folder glyph (degraded mode), and the **`Style(ext)` file-type
> color TAXONOMY** (a self-contained data table keyed by extension — annotated in-file as deliberate, not
> chrome). `PrefabBlue` is also used by InspectorPanel:711/811 — but those Inspector usages are OUT of EF5b
> scope (inspector cluster / EF5c–d); the token now exists for them to adopt later. Touched only the six
> panel files + `EditorTheme.cs` (all fully mine); `EditorApplication.cs`/`EditorMenus.cs` NOT touched.
> Editor csproj builds 0-error (clean `--no-incremental` to a scratch dir, around a running-editor bin-copy
> lock); reflection oracle EXIT=0 (all suites green). NOT visually verified yet — batched into the EF5a–d
> human-screenshot checkpoint (GPU-hang rule: no relaunch-loop).
>
> **EF5a note (just landed):** palette + geometry reworked to a deeper-graphite UE5 identity — pure style,
> behaviour byte-unchanged. (1) Geometry (`ImGuiController.ApplyGeometry`): rounding pulled into UE5's small
> band (Window/Child/Popup 5px, Frame/Grab/Tab 4px, Scrollbar 5px — was the soft 6-9px "consumer app" pass);
> spacing/padding/borders unchanged. (2) Palette (`ImGuiController.ApplyColors`): the bg0..titleBg elevation
> ramp pushed darker/cooler — bg0 `#1A1C20`→`#16181C`, bg1 `#212429`→`#1D2026`, bg2 `#282C32`→`#262A31`,
> bg3→`#333842`, header→`#2B3038`, titleBg `#15171A`→`#121418`, menuBar→`#101216`, Tab/TabDimmed darkened to
> match; `textDim` nudged `#848C99`→`#8C94A1` so secondary text clears 4.5:1 even on input frames; border/
> borderLight retuned. (3) `EditorTheme` Bg0..TitleBg ramp mirrored byte-for-byte (the overlay-chrome mirror;
> its comment mandates the sync) + `OverlayBg` (~Bg0@0.82) + RowLabel/RowCaption re-tuned to the new ramp.
> **Contrast VERIFIED (WCAG):** body text `#ECEEF2` = 12-16:1 on every surface; `textDim` ≥4.7:1 on inputs;
> azure accent 4.94:1 on bg0 (>3:1 UI-element min); RowLabel ~9-11:1, RowCaption ~4.7-5.8:1 — none muddy.
> Touched ONLY `ImGuiController.cs` + `EditorTheme.cs` (both fully mine); `EditorApplication.cs` NOT touched
> (its 2 pre-existing not-mine `RenderPassTogglesWindow.Draw(S)` lines are still the only diff there).
> Editor csproj builds 0-error (scratch dir, around running-editor bin-copy lock). NOT visually verified yet
> — batched into the EF5a–d human-screenshot checkpoint (GPU-hang rule: no relaunch-loop).
>
> **EF9d note (just landed):** the Window-menu open-state sync is COMPLETE. The checkmark was already correct
> (`DrawRegistryMenu("Window")` → `EditorWindows.IsOpen(key)` → `IsWindowOpen` → `panels.IsShown(key)`, queried
> every frame; EF9c made that the SAME `Shown` flag it persists, so menu-state and disk-state can't disagree).
> The only real gap was that re-opening a CORE panel from the menu flipped `Shown` false→true and set
> `pendingFocusWindow = key` but NOTHING consumed it for core panels — only the two viewports
> (`SceneView`/`GameView`) called `SetNextWindowFocus` — so a re-opened Inspector re-appeared BEHIND its
> dock-tab neighbour and read as a no-op. EF9d's one-line fix: `DrawDockPanel` now calls
> `ImGui.SetNextWindowFocus()` when its panel == `pendingFocusWindow`, surfacing the re-opened panel (the
> same Unity focus-on-open the viewports already get). Ordering is safe: `panels.DrawCore(DrawDockPanel)`
> runs BEFORE `DrawViewportWindows()` (which clears `pendingFocusWindow` at the end of the frame), and the
> viewport keys never collide with a core-panel key. Human-screenshot verify (re-open Inspector from the
> Window menu → it surfaces; close → checkmark clears) batched into the EF9/windowing visual checkpoint.
>
> **EF9c note (kept for reference):** layout persist/restore is DONE and the `Shown` open/closed state now ROUND-TRIPS
> across restart. EF9c added: (1) `EditorPanelRegistry.HiddenKeys()` / `ApplyHidden(...)` (the closed core
> panels are the persisted unit — viewports are never "closed"); (2) `EditorLayout.SavePanelState/LoadPanelState`
> writing a `<projectStem>.v2.panels` sidecar next to the dock `.ini` (the `.ini` persists window geometry/dock
> node but NOT whether the editor submits the window, so a closed panel would otherwise re-open on next launch);
> (3) wiring — `panels.ApplyHidden(EditorLayout.LoadPanelState())` after `EditorLayout.Load()` on startup, and a
> per-frame change-gated `SavePanelState` at the end of `BuildUI` (closing a panel only flips `Shown`, which does
> NOT dirty ImGui's dock settings, so the existing `WantSaveIniSettings` save can't catch it). `DeleteSaved` now
> also clears the sidecar so "Reset Layout" re-shows every panel (it already called `panels.ResetVisibility()`).
> **PassthruCentralNode REMOVED** (`DockSpace` now uses `ImGuiDockNodeFlags.None`): the central node is ALWAYS
> filled by the Scene/Game view windows so passthrough never engaged visibly, and the review flagged it as a
> maximize/modal-capture breaker — dropping it is byte-identical to the eye and removes the hazard. **For EF9d:**
> the Window menu is registry-driven (`DrawRegistryMenu("Window")` at `EditorApplication.cs:882`); the
> per-panel checkmark/open state reads `EditorWindows.IsOpen` → `IsWindowOpen` → `panels.IsShown(key)`, which is
> now the SAME `Shown` flag EF9c persists. So a closed panel is genuinely "closed" in the registry AND on disk —
> EF9d's job is to confirm the menu checkmarks reflect it and re-opening from the menu works (the wiring at
> `EditorWindowRegistry.cs:72-105` is already correct per the plan; bind/verify, don't rebuild).
>
> **EF9b note (kept for reference):** maximize no longer FIGHTS docking. The fullscreen windows now have their OWN
> ImGui identities — `###maxpanel` (core panels, `DrawMaximizedPanel`) and `###maxinstance` (duplicated
> tabs, `DockPanelHost.DrawMaximizedInstance`) — each with `NoSavedSettings`, instead of reusing the docked
> window's bare label (`"Inspector"`/`"Scene"`/`###KindKey`). The old shared-identity path force-undocked
> the panel on every maximize AND (lacking `NoSavedSettings`) wrote the fullscreen pos/size into that
> window's saved settings — which would have polluted the layout EF9c persists. So **EF9c can now trust the
> docked windows' saved geometry is clean** (maximize never touches it). The EF9a `ref open` close contract
> is intact (close still flips `Shown`/`Open` + `maximize.Clear()` same frame; restore-double-click keys off
> the maximize KEY `name`, not the window label). Editor "fullscreen" is purely an ImGui layout change — it
> does NOT resize the swapchain (only `runtime.Window.OnResizeCallback` → `imgui.WindowResized` +
> `viewport.InvalidateTargetSizes` does), so EF9b introduced NO second/undrained `ResizeBuffers` — the EF3
> coupling DoD is met by construction (the resize harness `Docs/Validation/dx12-resize-harness/` is unchanged
> and still the swapchain-resize guard). **For EF9c:** persisting the layout (`io.WantSaveIniSettings` →
> `EditorLayout.Save()` at `EditorApplication.cs:828`) is already wired; EF9c's real work is restore-on-startup
> + the `PassthruCentralNode` review (`EditorApplication.cs:784`).
>
> **EF3 note for later chunks (important):** the plan's original EF3 root cause ("`Dx12SwapChain.cs:165`
> flushes the WRONG fence — legacy upload, not `frameFence`") was **STALE** — `Dx12Device.Flush()` was
> already hardened in commit `49623af8` (P0b step1) to drain ALL THREE queue fences (render `fence` +
> pipelined `frameFence` + `uploadFence`) before returning, so the drained-resize was already correct.
> Empirically PROVEN: the new checked-in harness `Docs/Validation/dx12-resize-harness/` drives `Resize`
> over a 0×0/shrink/grow/4K→1080p/same-size stress sequence with a real in-flight frame before each
> resize — **no device removal**, in BOTH the default (`FramesInFlight==1`) and `BALLISTIC_DX12_OVERLAP=1`
> (`FramesInFlight==2`) paths. EF3 hardened `Dx12SwapChain.Resize` (explicit drain comment + post-resize
> `currentIndex` re-seed) and added the harness as the permanent regression guard. **EF9b still owes EF3
> a fullscreen re-verify** — but note the editor's "fullscreen" is an ImGui maximized panel (no DXGI mode
> change), so EF9b's fullscreen does NOT resize the swapchain; the only real swapchain resize remains the
> OS window resize through `Dx12BallisticEngineWindow.OnResize`. Re-run the harness after any swapchain/
> `Flush`/fence change.

**Each chat MUST, in order:**
1. Read this plan top-to-bottom. Confirm the NEXT CHUNK pointer above and the chunk's section.
2. Check any **Open Decision** that blocks the chunk. If blocked and unanswered, STOP and ask the
   user — do not guess (e.g. EF5a is blocked on the theme-identity decision).
3. Implement ONLY that one chunk (or sub-chunk). Honor every Cross-cutting rule, especially the
   **GPU-hang safety rule** (EF3/EF5/EF9) and **one-commit-per-chunk**.
4. Verify against the chunk's DoD/oracle. Visual chunks: do NOT relaunch-loop — build clean + (if a
   human screenshot is needed) hand that off as a checkpoint for the user.
5. **Commit** the chunk with the chunk id in the message (e.g. `[editor-fixes] EF3: drained swapchain
   resize + post-resize reset`). Stage explicit paths (no `git add -A`).
6. **Update this plan file**: advance the `▶ NEXT CHUNK` pointer to the next chunk in the execution
   order, set `Last committed chunk`, and tick the Progress checklist below. Commit that doc edit too
   (can be in the same commit as the chunk).
7. **Emit the next-chat handoff prompt** in the MANDATORY template below — never skip a section; empty
   "State to know" / "Gotchas" is not allowed (write "none" only if truly none).

**MANDATORY handoff template (the last thing each chat outputs):**
```
=== NEXT CHAT — paste this into a fresh chat ===
Plan: Docs/Plans/editor-fixes-plan.md  (read it fully first)
Branch: dx12-renderer
Just committed: <chunk id> @ <short SHA> — <one line of what changed>
NEXT CHUNK: <chunk id> — <chunk title>
Blocking open decision: <none | which decision + that it must be answered first>
State to know: <facts the next chat needs that aren't obvious from the plan/code — e.g.
  "EF9b still owes EF3 a fullscreen re-verify"; or "none">
Gotchas hit this chat: <surprises / dead ends to avoid — or "none">
Verify before you start: `git log --oneline -3` shows the commit above; editor csproj builds 0-error.
Your job: implement ONLY <chunk id> per the plan, commit it, update the NEXT CHUNK pointer, then emit
this same handoff for the chunk after it.
=== END ===
```

### Progress checklist (each chat ticks its chunk)
- [x] EF3 — resize crash FIXED (v3). ROOT CAUSE found by headless bisection (NOT the swapchain): the **Hi-Z occlusion pass** (`Dx12GpuDrivenRenderer.BuildHiZ`) re-pointed its BINDLESS Hi-Z SRV only on the FIRST build (`if (hizBindlessIndex < 0)`); on resize `Dx12HiZ.Ensure()` recreates the pyramid resource but the bindless descriptor kept pointing at the DISPOSED old pyramid → the GPU cull shader sampled a freed resource → DXGI_DEVICE_HUNG (PageFaultVA=0). FIX: re-register the all-mips SRV whenever `Ensure()` returns `recreated==true`. Verified: full default resize-stress PASS on CarDemo + Bistro; golden SHA byte-identical. (v1 @ d1799cb7 swapchain hardening + v2 @ ee5edee9 DRED-always/ImGui-grow-drain were correct but not the cause; DRED-always is what enabled v3's diagnosis.) Permanent guards: headless `BALLISTIC_DX12_RESIZE_STRESS=1` mode in `Dx12HeadlessRuntime` + swapchain harness `Docs/Validation/dx12-resize-harness/`.
- [x] EF9a — honor close everywhere (incl. maximized) — `ref open` threaded through both maximized paths; close STICKS + exits fullscreen same frame
- [x] EF9b — maximize/fullscreen (re-verify EF3 fullscreen) — dedicated `###maxpanel`/`###maxinstance` identities + `NoSavedSettings`; maximize no longer undocks/pollutes the docked window; no swapchain resize introduced
- [x] EF9c — layout persist + PassthruCentralNode review — `Shown` open/closed state now round-trips via a `.panels` sidecar (`EditorLayout.Save/LoadPanelState` + `EditorPanelRegistry.HiddenKeys/ApplyHidden`); PassthruCentralNode dropped (central node always filled, removed the maximize/modal-capture hazard)
- [x] EF9d — Window-menu open-state sync — checkmark already queried `panels.IsShown` each frame (EF9c made that the same persisted flag); the bind-gap was that a menu-reopened CORE panel flipped `Shown` but never surfaced (only the two viewports consumed `pendingFocusWindow`). Fix: `DrawDockPanel` now `SetNextWindowFocus()` when its panel == `pendingFocusWindow`, so re-open surfaces it — same Unity focus-on-open the viewports get. No state-vs-disk disagreement possible (EF9c gift).
- [x] EF5a — palette + geometry — identity = (i) faithful UE5 (cool graphite + azure, no warm accent). Reworked `ImGuiController.ApplyGeometry` (rounding into UE5's 4-5px band) + `ApplyColors` (deeper-graphite bg0..titleBg ramp, brighter `textDim` for ≥4.5:1 on inputs) + mirrored the `EditorTheme` Bg0..TitleBg ramp / OverlayBg / RowLabel-RowCaption. Pure style, behaviour byte-unchanged; WCAG contrasts verified (body 12-16:1, accent 4.94:1). Only `ImGuiController.cs`+`EditorTheme.cs` touched. Visual verify batched into the EF5a–d checkpoint.
- [x] EF5b — centralize bypass-color offenders — added a SEMANTIC tokens block to `EditorTheme.cs` (Error/Warning/Success, PrefabBlue/RowChild/IconMuted, PrimaryAction±, FolderTint/Dim, LogLevel[], Hairline/TreeGuide, PopupBg/InputBg) and routed the hand-typed `SysVec4` literals in ConsolePanel/HierarchyPanel/AssetBrowserPanel/StatsPanel/BuildPanel/VolumeProfileEditor through them. Deliberately harmonizes a few slightly-off literals into one family (NOT byte-identical — visual only, behaviour unchanged). Remaining literals in those files are justified (alpha-only overlays, alpha/scale derivations of a token, the no-icon fallback glyph, the `Style(ext)` file-type taxonomy data table — annotated in-file). Only the 6 panels + `EditorTheme.cs` touched. Build 0-error, oracle EXIT=0. Visual verify batched into the EF5a–d checkpoint.
- [x] EF5c — panel chrome polish — routed inspector-cluster `SeparatorText` → `EditorDecoration.DrawSectionHeader` (incl. the shared `ImGuiComponentGui.Header` adapter, so all attribute `[Header]` sections at once) + toolbar `Separator()` → `DrawDivider()` in Console/Hierarchy/Assets. Gave `DrawSectionHeader` Caption-font + symmetric pad so titles recede; dropped now-redundant leading `Spacing()`. Visual-only (not byte-identical), behaviour unchanged; build 0-error, oracle EXIT=0. Out of scope: Stats/TagsLayers/Settings + modal-dialog separators (left stock). Visual verify batched into the EF5a–d checkpoint.
- [ ] EF5d — type/spacing tokens everywhere
- [ ] EF12 — rename Inspector → "Details"
- [ ] EF-LAYOUT — inspector layout model (design + shared helper)
- [ ] EF16 — nested indent (fixed value-x)
- [ ] EF11 — adaptive label column + slider value legibility
- [ ] EF10a — per-component member search (conditional)
- [ ] EF10b — component-list search (conditional)
- [ ] EF15 — collection reorder/clear + polymorphic list serialize + round-trip test
- [ ] EF7 — Tag/Layer "Add…" → open Tags & Layers panel
- [ ] EF8 — split Layer Collision Matrix into its own panel
- [ ] EF13+EF14 — hierarchy collapse/expand + collapsed-by-default
- [ ] EF1 — gizmo-mode button auto-width
- [ ] EF2 — gizmo ↔ eye-menu de-overlap
- [ ] EF4 — FPS scene-view gate
- [ ] EF6 — delete dead shading-mode dropdown

---

### Cross-cutting rules (honor every chunk)
- **GPU-hang safety (standing rule):** EF3 + EF9 + EF5 touch swapchain/window/style and CAN hang
  the GPU. NEVER relaunch a hanging build in a loop — a TDR has hard-crashed the dev PC before.
  On first device-removal: stop, capture DRED, make safe, commit, diagnose WITHOUT relaunching.
  Prefer headless verification (`bal render` / `BALLISTIC_SCREENSHOT_PAUSED=1`) over launching the editor.
- **One commit per chunk (bisect discipline):** every EFx (and every sub-chunk EF5a/EF9b/…) lands as
  its own commit with the chunk id in the message, so a regression — especially a GPU hang — can be
  bisected to a single chunk and reverted cleanly. Never batch two chunks into one commit.
- **Rebuild the ROOT engine csproj** after engine changes, not just the Editor exe (stale-dll lesson).
- **Editor is GPU-launch-gated:** most of these need a human to look at the editor. Worker chunks
  that change pure layout/logic verify via the reflection test oracle + 0-error build; visual chunks
  hand off "needs human screenshot" rather than relaunch-looping.
- **Batch the human-screenshot checkpoints:** EF1/EF2/EF4/EF6 (viewport overlay), EF5a–d (theme),
  EF9 fullscreen and the Inspector-layout set all need a human to look. Group each cluster's visual
  verification into ONE editor launch (the user reviews several chunks at once) instead of one
  launch per chunk — fewer round-trips, and fewer risky editor launches.
- Oracle for non-visual correctness: `dotnet run --project BallisticEngine.Tests.Reflection`
  (B1/B2/F3 suites GREEN) + Editor csproj builds 0 CS-errors (MSB bin-copy lock from a running
  editor = environmental, ignore).
- `git add -A` FORBIDDEN while the tree is dirty (lots of untracked unrelated files) — stage explicit paths.

### Plan-review resolutions (validated 2026-06-17, in response to the review)
- **EF9 / "KEEP DockPanelHost" rationale — CONFIRMED & PRESERVED.** Read `DockPanelHost.cs`: it exists
  to support MULTIPLE INSTANCES of a panel type (Unity/VS-style: "Inspector", "Inspector 2", each with
  its own factory-made state), with SINGLETON protection for Scene/Game views (they back the one
  renderer target) and a fullscreen router that recognises a maximized duplicated tab (`OwnsLabel`,
  `DrawMaximizedInstance`). So the rewrite must NOT regress multi-instance + singleton + per-instance
  state. **Revised EF9 stance: keep DockPanelHost's multi-instance role; fix docking ON TOP of it
  (DockSpace + per-window p_open + native maximize) rather than deleting the host.** See revised EF9.
- **EF15 deserialize — CONFIRMED READY (fix is serialize-side only).** `SceneSerializer.cs:550,634,641`
  show the deserialize path already detects a `$type`-tagged element by (abstract element type + tag
  present) even for recursed collection elements (`member==null`). The only gap is serialize-side:
  `:349 SerializeSequence` calls `SerializeValue` (no per-element `$type`). Fix scope is small; the
  round-trip oracle below makes "byte-stable" concrete.
- **EF6 Wireframe — CONFIRMED DEAD on DX12 (full removal safe).** Grep of `BallisticEngine.DX12/` for
  `DebugViewMode`/`Wireframe`: zero reads (only an unrelated GI-isolate hit). Wireframe/Normals/Depth
  are ALL non-functional on DX12, not just the buffer modes. Full dropdown removal loses nothing.
- **EF5 identity decision — RESOLVED 2026-06-17 → (i) faithful UE5** (cool, monochrome graphite +
  restrained blue-grey shell + a single azure highlight `0x3D8BD4`, NO warm accent). This drives BOTH the
  accent and the "does it look like UE5" oracle for the whole EF5 series. EF5a landed against it.

---

## Validated architecture facts (file:line grounded)

**Viewport overlay (EditorApplication.cs):**
- Gizmo-mode buttons (Move/Rotate/Scale) drawn `EditorApplication.cs:2150,2157-2161` with a **fixed
  width `bw = 58 * S`** passed to `GizmoModeButton(...)` → `ImGui.Button(label, new SysVec2(width,0))`
  at `:2119`. No `CalcTextSize` → icon+label overflow gets clipped. **(EF1)**
- Orientation gizmo at `Gizmo/OrientationGizmo.cs:25` (`radius=34*scale`, center pinned
  `viewRight - 48*scale, viewTop + 48*scale` → footprint `viewRight-82*scale .. viewRight`).
  Visibility eye-menu at `EditorApplication.cs:2193-2194` (`SetNextWindowPos` top-right pivot,
  margin `OverlayMargin*S = 10*S`). They share the top-right corner → overlap. **(EF2)**
- FPS/stats overlay: `stats.Draw(...)` called for Scene view `EditorApplication.cs:1749-1751`
  (`RenderStats.Scene`) AND Game view `:1974-1976` (`RenderStats.Game`), both gated only by a single
  global `showStats` (decl `:76`) — no `SceneManager.IsPlaying` / view-type gate. **(EF4)**
- Shading-mode dropdown drawn `EditorApplication.cs:1315-1357`; wires `Renderer.DebugViewMode`,
  `HDRenderer.EditorExtraDebugMode`, `HDRenderer.EditorGiIsolate` (`HDRenderer.cs:20,41,47,54`).
  **DEAD on DX12:** `EditorDebugViews.Install()` is a no-op (`EditorDebugViews.cs:6-25`,
  `// No-op on DX12`), `EditorDebugComposite` delegate never invoked, DX12 renderer never reads
  these props. **(EF6)**

**DX12 swapchain / windowing:**
- Resize path `Dx12SwapChain.cs:162-177`: `dev.Flush()` at `:165` waits on the **legacy `fence`**
  (shared with asset uploads), NOT `frameFence` used by pipelined frames → back-buffer RTV can still
  be bound in an in-flight frame when released at `:166` → **device removal on `ResizeBuffers` `:168`**.
  Present error path captures DRED `:129-138`. Window resize forwarded
  `Dx12BallisticEngineWindow.OnResize → swapChain.Resize()` immediately, no frame-sync. **(EF3)**
- Docking: `DockSpace(... ImGuiDockNodeFlags.PassthruCentralNode)` `EditorApplication.cs:783-784`.
  Core panels via `EditorPanelRegistry.DrawCore` (`EditorPanelRegistry.cs:90-96`, writes back
  `d.Shown` on close — close DOES flip the flag). Extra instances via `DockPanelHost.DrawAll`
  (`:97-105`, writes back `inst.Open`). Maximize is a custom state machine drawing a fullscreen
  docked window (`EditorApplication.cs:728-740`, `DrawMaximizedPanel :1548-1575` with
  `NoResize|NoMove|NoDocking` and **no `p_open` wiring** → close ignored while maximized).
  `EditorWindowRegistry` menu wiring is correct (`EditorWindowRegistry.cs:72-105`). ImGui.NET docking
  branch is present and in use (DockSpace already called). **(EF9)**

**Inspector (InspectorPanel.cs):**
- Member grid `BeginGrid :2445-2448` = 2-col `BeginTable` with **fixed label col `WidthStretch,0.38f`**
  / value `0.62f`; label drawn `TextUnformatted` `:2463` with no clip-guard → long labels clip. **(EF1/EF11)**
- Nested members: `DrawNestedSlot :1973` uses `TreeNodeEx(... DefaultOpen)` → ImGui fixed indent
  (~20-25px) **per level**, stacking → value column pushed off-screen on deep nesting. **(EF16)**
- Collections `DrawCollectionSlot`: **per-element Remove ALREADY EXISTS** `:1608` (`EditorIcons.Delete`),
  Add `:1575`. Missing = reorder/clear + the polymorphic-serialize bug:
  `SceneSerializer.cs:349 SerializeSequence` uses `SerializeValue(element)` NOT
  `SerializeMemberValue(...)` → `[SerializeReference] List<IFoo>` loses per-element `$type` (RW8). **(EF15)**
- Tag/Layer dropdowns `:666-696` iterate `TagManager.Tags` / `LayerManager.DefinedLayers()` directly —
  **no "Add Tag.../Add Layer..." entry**. A real management window EXISTS:
  `Panels/TagsLayersPanel.cs:26` (Window > Tags & Layers) but is NOT linked from the dropdowns; it
  ALSO draws the collision matrix inline (`DrawCollisionMatrix(scale)` `:35`). **(EF7/EF8)**
- Inspector title hardcoded `"Inspector"` in `EditorApplication.cs:179` `panels.Register(...)` (also
  the extra-panel register `:156`); flows through `DockPanelHost.cs:76`. **(EF12)**
- **No per-component member search** exists; only an "Add Component" search buffer
  (`InspectorPanel.cs:51`). No reusable EditorWidgets search-field helper — each search inlines its
  own `InputTextWithHint`. **(EF10)**

**Hierarchy (HierarchyPanel.cs):**
- Tree nodes `:287-288` use `ImGuiTreeNodeFlags.DefaultOpen` → all expanded on load. **(EF14)**
- Toolbar `DrawEntities :32-51` has +/delete/search only — **no Collapse/Expand All**. **(EF13)**

**Theme (ALREADY HAS INFRASTRUCTURE — overhaul not greenfield):**
- `ImGuiBackend/EditorTheme.cs` (type scale Display/Header/Body/Caption + drawer-row palette +
  surface palette Bg0..TitleBg + overlay chrome) and `ImGuiBackend/EditorDecoration.cs` (DrawList
  primitives: cards/dividers/badges/accent stripes) **EXIST**.
- Central style entry `ImGuiController.ApplyGeometry/ApplyColors :229-343` (rounding 6-9px, accent
  from `EditorPrefs.Accent`). **Default accent = azure `0x3D8BD4`, NOT the orange in old screenshots.**
- Fonts centralized `ImGuiController.LoadFont :95-152` (Inter + Lucide, DPI-aware).
- **Bypass offenders (hardcoded colors, the "raw" feel):** `AssetBrowserPanel.cs` (lines
  224,262-263,280-282,330,480,731-732,746,906,1212-1215), `ConsolePanel.cs:24-28,200-209`
  (`LevelColors[]`), `HierarchyPanel.cs:299` (prefab blue), scattered in VolumeProfileEditor/StatsPanel/
  BuildPanel. **(EF5)**

---

## EF1 — Viewport gizmo-mode buttons clip ("Mov"/"Rota"/"Sca")
Root: fixed `bw = 58*S` (`EditorApplication.cs:2150`) ignores icon+label width.
Fix: size each button to `max(58*S, CalcTextSize(label).X + framePadding*2 + iconPad)` (or auto-size
and let the toolbar grow), so Move/Rotate/Scale/Pivot/Snap all read in full. Keep min width so the
3 mode buttons stay visually equal (compute the max label width once, apply to all three).
DoD: human screenshot shows full labels, no overlap. Non-visual guard: build 0-error.

## EF2 — Orientation gizmo ↔ Visibility eye-menu overlap (top-right corner)
Root: both anchored top-right within ~10-50px (`OrientationGizmo.cs:25` + `EditorApplication.cs:2193`).
Fix: reserve the gizmo's footprint (`82*scale` tall/wide) and push the visibility menu BELOW it (offset
its `SetNextWindowPos` Y down by gizmo height + gap), OR move the eye-menu to the top-LEFT cluster with
the toolbar. Prefer: eye-menu below the gizmo, right-aligned, so the axis balls stay fully clickable.
DoD: human screenshot — gizmo fully visible+clickable, eye-menu not overlapping.

## EF3 — Window resize / fullscreen → DX12 device removal ⚠️ CRASH (high priority) — ⚠️ REOPENED (still crashes)
**STATUS 2026-06-17 (after v1 @ d1799cb7): RESIZE STILL CRASHES THE LIVE EDITOR.** A user drag-resize
device-removed with `DXGI_ERROR_DEVICE_HUNG` (0x887A0006) at **`Dx12SwapChain` Present** (not at
`ResizeBuffers`). So the swapchain `Resize` itself is NOT the culprit — a GPU command EARLIER in the frame
hung; Present is just where it surfaced. v1's swapchain hardening + the resize-harness are CORRECT but
INSUFFICIENT (the harness exercises only swapchain clear+present, not the editor's real render path).
**Asymmetry that localizes it:** the PLAYER (`Dx12WindowedRuntime.OnResize`) resizes the swapchain AND the
renderer offscreen target SYNCHRONOUSLY in lockstep and does NOT crash; the EDITOR (`PresentToScreen=false`)
has (a) the ImGui present pass sampling the offscreen `ldr` via the shared `UiHeap`, and (b) a DECOUPLED,
panel-sized offscreen `ldr` resize deferred to a later frame via `viewport.InvalidateTargetSizes()`
(`EditorApplication.cs:251-253`) — `ldr` is disposed+recreated in `AllocateResolutionTargets`
(`DX12HDRenderer.cs:448`, which itself `dev.Flush()`es). The exact faulting op is NOT yet proven (candidates:
mismatched-dimension bind during the drag storm; an ImGui upload-buffer grow disposing an in-flight buffer;
a UiHeap descriptor overwrite). A scratch real-renderer repro is blocked (needs full engine bootstrap —
`DefaultTextures` NRE). **v2 follow-up shipped = MAKE-SAFE + DIAGNOSTICS, not the proven fix:**
(1) DRED **page-fault tracking is now ALWAYS-ON** (`Dx12Device` ctor — negligible cost; auto-breadcrumbs
still opt-in via `BALLISTIC_DX12_DRED=1`) so the NEXT crash logs the faulting VA without a special relaunch;
(2) hardened `ImGuiDx12Renderer.EnsureBuffers` to `dev.Flush()` before disposing an in-flight upload buffer
on a grow (a real latent UAF, defensive). **NEXT (needs the user, ONE careful repro — GPU-hang rule, no
relaunch-loop):** rebuild the editor, reproduce the resize crash ONCE, read `engine.jsonl` for the
`[DX12] Present device-removed` line — it will now carry `DRED=PageFaultVA=0x… reads/writes=…` naming the
freed allocation. That VA → the faulting resource → the real fix (likely: resize the editor offscreen target
in lockstep, or guard the present's sampled `ldr`/descriptor across the resize). Swapchain-side facts below
remain valid.

**(v1, still valid) VALIDATED RESOLUTION of the swapchain-side claim (it was STALE):** the plan claimed `Dx12SwapChain.cs:165`
"flushes the wrong fence" — but `Dx12Device.Flush()` was already hardened in commit `49623af8` (P0b step1)
to drain ALL THREE queue fences (render `fence` via `WaitForGpu`, pipelined `frameFence` via
`WaitFrameFence(frameFenceValue)`, AND `uploadFence`) before returning, so the drained-resize was already
correct on this branch. Proven empirically by the new harness (below) — no device removal across the full
stress sequence in default AND overlap paths. EF3 therefore HARDENED `Dx12SwapChain.Resize` (made the
drain comment accurate to the 3-fence reality + re-seed `currentIndex` post-resize so a stale index can
never index a disposed buffer) and added a permanent regression guard rather than a behavioural fix.
Root (historical, now fixed): pipelined/in-flight frames not drained before back-buffer release+
`ResizeBuffers`. (Tightly coupled to EF9 fullscreen — see coupling note; but the editor "fullscreen" is an
ImGui maximized panel, NOT a DXGI mode change, so it does not resize the swapchain.)
**Harness:** `Docs/Validation/dx12-resize-harness/` (hidden HWND → real Dx12Device+Dx12SwapChain → Resize
over 0×0/shrink/grow/4K→1080p/same-size with a real in-flight frame before each). Re-run after any
swapchain/`Flush`/fence change. SkyTest golden SHA byte-identical (`bal render` never uses the swapchain).
Fix (resize sequence, in order):
1. Drain ALL in-flight frames on `frameFence` (full `WaitForGpu` across `FramesInFlight`), NOT the
   legacy upload `fence`. Resize must be a hard barrier — no frame may be in flight.
2. Release every back-buffer reference (RTVs + back-buffer resources) so nothing is held at `ResizeBuffers`.
3. `ResizeBuffers` (keep the existing 0×0 clamp + same-size early-out).
4. **POST-resize reset (review catch — must not skip):** reacquire back buffers + recreate RTVs, reset
   `currentBackBufferIndex` from the new swapchain, and reset/re-seed the pipelined frame state
   (`frameFence` per-slot values + any `FramesInFlight` ring indices) so the next frame starts from a
   clean fence state. A stale back-buffer index or frame-fence value after resize re-introduces the hang.
5. Route the editor's window OnResize AND fullscreen toggle through this single drained path
   (`Dx12BallisticEngineWindow.OnResize → swapChain.Resize`); no other call site may resize.

**Coupling with EF9b (explicit):** EF3's DoD includes "fullscreen toggle repeatedly", but the fullscreen
MECHANISM changes in EF9b. So: (a) land EF3 first and verify the pure window-resize case (drag-resize),
(b) after EF9b, RE-RUN EF3's fullscreen verification and confirm EF9b's new fullscreen path still goes
through the EF3 drained resize (it must not introduce a second, undrained resize). Add to EF9b's DoD:
"fullscreen enter/exit routes through the EF3 drained Resize — no undrained ResizeBuffers anywhere."

**Verification (review catch — the headless oracle does NOT exercise resize):** headless `bal render`
has no swapchain, so it only proves "offscreen render unchanged", NOT the fix. Real verification options,
preferred order: (1) author a tiny resize-harness exe that creates the swapchain and calls `Resize` in a
loop over a sequence of sizes (incl. 0×0/minimize, shrink, grow, same-size) WITHOUT the full editor — the
smallest surface that exercises the crash; (2) PIX/DRED capture on a single careful manual editor
drag-resize + fullscreen. Use (1) to gain confidence before any (2) editor launch. ⚠️ GPU-hang rule —
diagnose with DRED, do NOT relaunch-loop.
DoD: resize-harness runs the full size sequence with NO device removal; one careful manual editor run
drag-resizes + toggles fullscreen with no removal; headless `bal render` golden scenes stay byte-identical
(regression guard only, not the fix proof).

## EF4 — FPS/stats overlay shows in Scene view (edit mode)
Root: single global `showStats` gates both views (`EditorApplication.cs:1749/1974`); no view/play gate.
Fix: keep the Game-view `stats.Draw` call; gate the Scene-view call so the FPS readout does not appear
in the Scene view (Scene view may keep a minimal draw/tri counter if desired, but NOT the FPS number —
edit mode is on-demand/inconsistent). Decision pending user: (a) FPS Game-view-only regardless of play,
or (b) Game-view + `SceneManager.IsPlaying` only. **Default to (a)** unless told otherwise.
DoD: Scene view shows no FPS; Game view unchanged.

## EF5 — Theme overhaul → Unreal Engine 5 look (HEADLINE; sub-chunks)
NOT greenfield: `EditorTheme.cs`/`EditorDecoration.cs`/`ImGuiController` exist. The "raw/ugly" feel =
(a) palette+geometry not yet pushed to a deep UE5-dark identity, (b) panels bypassing the theme with
hardcoded colors. Target: deep dark graphite base, rounded panel headers, **one strong accent**,
AAA-tool feel — "doesn't look like default ImGui."
✅ **IDENTITY DECISION RESOLVED → (i) faithful UE5** (cool graphite + blue-grey shell + a single
restrained azure highlight, NO warm accent). The azure accent `0x3D8BD4` (EditorPrefs default) is KEPT;
the acceptance bar for the whole EF5 series is "looks like UE5". EF5a–d all build against this identity.
- **EF5a — Palette + geometry pass — ✅ DONE:** reworked `ImGuiController.ApplyGeometry` (rounding pulled
  into UE5's small 4-5px band) + `ApplyColors` (deeper/cooler bg0..titleBg ramp: bg0 `#16181C`, bg1
  `#1D2026`, bg2 `#262A31`, bg3 `#333842`, header `#2B3038`, titleBg `#121418`, menuBar `#101216`; `textDim`
  brightened `#848C99`→`#8C94A1` for ≥4.5:1 on input frames) + mirrored the `EditorTheme` Bg0..TitleBg ramp
  (overlay-chrome mirror) / OverlayBg / RowLabel-RowCaption to match. **Contrasts verified (WCAG):** body
  `#ECEEF2` = 12-16:1 on every surface; `textDim` ≥4.7:1 on inputs; azure accent 4.94:1 on bg0; RowLabel
  ~9-11:1, RowCaption ~4.7-5.8:1 — none muddy. Byte-of-behaviour unchanged; pure style. Only
  `ImGuiController.cs`+`EditorTheme.cs` touched. Visual verify batched into the EF5a–d screenshot checkpoint.
- **EF5b — Centralize bypass offenders — ✅ DONE:** added a SEMANTIC tokens block to `EditorTheme.cs`
  (Error/Warning/Success, PrefabBlue/RowChild/IconMuted, PrimaryAction±, FolderTint/FolderTintDim, the
  LogLevel[] ramp, Hairline/TreeGuide, PopupBg/InputBg) and routed the hand-typed `SysVec4` color literals
  in `AssetBrowserPanel`/`ConsolePanel`/`HierarchyPanel` (+ secondary `StatsPanel`/`BuildPanel`/
  `VolumeProfileEditor`) through them. Deliberately harmonizes a handful of slightly-off literals into one
  coherent family (NOT byte-identical — pure visual, behaviour unchanged). The literals that REMAIN are
  justified per the DoD grep: alpha-only overlays (`(0,0,0,0)`, `(1,1,1,0.0x)` ghost hovers, watermark
  icons), alpha/scale DERIVATIONS of a token (`(tint/color/accent.X,…,α)`, `(color.X*0.6,…)`), the
  no-icon-font fallback folder glyph (degraded mode), and the `Style(ext)` file-type color TAXONOMY (a
  self-contained data table, annotated in-file as deliberate). Build 0-error, reflection oracle EXIT=0.
  Visual verify batched into the EF5a–d checkpoint. **EF5c/EF5d note:** the Inspector/ComponentPreviews/
  ProfilerPanel/AssetInspectors still hold semantic literals (e.g. InspectorPanel:711/811 prefab-blue,
  :1192 warn, :1500 amber-red) — those are the inspector cluster's, addressed by EF5c/EF5d; the EF5b
  tokens (`PrefabBlue`/`Warning`/`Error`) already exist for them to adopt.
- **EF5c — Panel chrome polish — ✅ DONE:** routed the stark stock section/divider widgets through the
  existing `EditorDecoration` primitives (no hand-rolled DrawList chrome — the lib already had them). Every
  inspector-cluster `ImGui.SeparatorText(...)` → `EditorDecoration.DrawSectionHeader(...)` (the shared
  `ImGuiComponentGui.Header` adapter covers ALL attribute `[Header]` sections at once; plus `InspectorPanel`
  Render-Features + the `[Header]` path, `ComponentPreviews` ×11, `AssetInspectors` ×2) — a Caption-font,
  RowCaption-colored label + palette-hairline trailing rule instead of ImGui's framed default box. The
  toolbar→content `ImGui.Separator()` in `ConsolePanel`/`HierarchyPanel`/`AssetBrowserPanel` (nav-bar) →
  `EditorDecoration.DrawDivider()` (quieter `BorderLight` hairline). `DrawSectionHeader` gained a built-in
  symmetric pad (`SectionPadY=4f`) + `PushFont(Caption)` so a title recedes as a quiet group rule; the
  immediately-preceding redundant `ImGui.Spacing()` were dropped (pad now owned in one place). Visual-only
  (deliberate harmonization, NOT byte-identical); behaviour unchanged. Out of scope (untouched): Stats/
  TagsLayers/Settings `SeparatorText`/`CollapsingHeader`, and the modal-dialog internal separators in
  `AssetBrowserPanel` (left stock — dialog internals, not panel chrome). Build 0-error, oracle EXIT=0.
  Visual verify batched into the EF5a–d checkpoint.
- **EF5d — Type/spacing tokens:** verify type-scale (Display/Header/Body/Caption) + spacing are applied
  everywhere (kills residual "flat" look); fix any panel still using raw `ImGui.Text`.
DoD: human screenshots before/after each sub-chunk — clearly modern, not default-ImGui; no panel
bypasses the theme (grep for `new SysVec4(` color literals in Panels/ → only justified ones remain).

## EF6 — Delete the dead Shading-Mode dropdown
Root: `EditorDebugViews.Install()` no-op on DX12; dropdown wires props the DX12 renderer never reads
(`EditorApplication.cs:1315-1357`, `EditorDebugViews.cs`, `HDRenderer.cs` debug props).
Fix: remove the dropdown UI + its dead wiring (DebugViewMode/EditorExtraDebugMode/EditorGiIsolate hooks
in the editor). **Full removal CONFIRMED safe (validated):** grep of `BallisticEngine.DX12/` shows NO
reads of `DebugViewMode`/`Wireframe` — Wireframe/Normals/Depth are ALL dead on DX12, not just the buffer
modes, so nothing functional is lost. Delete the whole dropdown. Leave engine-side `DebugView` enum +
`EditorDebugComposite` scaffolding ONLY if still referenced elsewhere (the DX12 port TODO may reinstate
them); otherwise remove the dead scaffolding too. Don't break the GI-isolate path (separate, still used).
DoD: dropdown gone, editor builds 0-error, no dangling `EditorDebugComposite`/`EditorExtraDebugMode`
references, GI-isolate untouched.

## EF7 — Tag/Layer dropdowns: add "Add Tag.../Add Layer..." → open Tags & Layers panel
Root: dropdowns (`InspectorPanel.cs:666-696`) are closed lists; `TagsLayersPanel` exists but unlinked.
Fix: append a separator + "Add Tag..." / "Add Layer..." item at the bottom of each combo; selecting it
opens `TagsLayersPanel` (set its `Open=true`, focus it). New tags/layers persist via the existing
TagManager/LayerManager store and appear in the dropdown next frame.
DoD: from Inspector, the "Add..." entry opens the panel; a tag added there shows in the dropdown.

## EF8 — Split Layer Collision Matrix into its own panel
Root: `TagsLayersPanel.cs:35` draws `DrawCollisionMatrix` inline in the same window.
Fix: move the collision-matrix UI into a new dedicated panel/window (e.g. "Layer Collision Matrix",
registered like other windows via the window registry / Window menu). `TagsLayersPanel` keeps only
tag+layer definitions. Both read the same LayerManager store. (Sequenced after EF7 — same file.)
DoD: two distinct windows; matrix edits still persist; Tags & Layers panel no longer shows the matrix.

## EF9 — Windowing/docking fix on ImGui DockSpace ⚠️ (big; sub-chunks)
Decision (user): use ImGui's built-in docking. **REVISED after reading the code (review catch):** do NOT
delete `DockPanelHost` — it provides MULTI-INSTANCE panels (Unity/VS "Inspector / Inspector 2", each
factory-made state), SINGLETON protection (Scene/Game back the one renderer target), and the fullscreen
router (`OwnsLabel`/`DrawMaximizedInstance`). Deleting it regresses those. Instead: fix the broken docking
behaviour ON TOP of the existing host. The old memory note "KEEP DockPanelHost" was right for this reason.
Symptoms: fullscreen broken; panels can't be maximized; opening a panel then can't close it (only Reset
Layout). Validated causes: `PassthruCentralNode` + custom maximize state machine fighting docking +
`DrawMaximizedPanel`/`DrawMaximizedInstance` using `ImGui.Begin(label, flags)` with NO `p_open` ref
(`EditorApplication.cs:1548-1575`, `DockPanelHost.cs:138`) → the X is ignored while maximized.
- **EF9a — Honor close everywhere (the real "can't close" fix) — ✅ DONE:** threaded a `ref open` through
  BOTH maximized paths (`DrawMaximizedPanel` for registered core panels via `panels.IsShown` + new
  `EditorPanelRegistry.SetShown`; `DockPanelHost.DrawMaximizedInstance` now `Begin(label, ref open, flags)`
  and returns `bool closed`). On X-while-maximized: the panel's `Shown`/`Open` flag flips false (close
  STICKS, no redraw loop) AND `maximize.Clear()` runs the SAME frame so the docked layout returns
  immediately. The normal docked path already honored close (`DrawDockPanel`/`DrawCore`). DockPanelHost
  multi-instance + singleton semantics untouched. Verified: editor builds 0-error (bin-copy lock from a
  running editor ignored), reflection oracle EXIT=0 (18 suites green). Human screenshot of close-while-
  maximized batched into the EF9/windowing visual checkpoint.
- **EF9b — Maximize/fullscreen that doesn't fight docking — ✅ DONE:** the maximize STATE machine
  (`MaximizeController`) was already clean + single-sourced (built in A1b, hardened EF9a). The remaining
  EF9b defect was that the fullscreen WINDOWS reused the docked panels' ImGui identities — core panels via
  `Begin(name, …)` (bare `"Inspector"`/`"Scene"` labels) and duplicated tabs via `Begin(label, …)`
  (`###KindKey`). A single ImGui window can't be both docked (dock-tree member, geometry in the `.ini`) and
  a `NoDocking` fixed-position fullscreen window, so each maximize FORCE-UNDOCKED the panel and — lacking
  `NoSavedSettings` — wrote its fullscreen pos/size into the docked window's saved settings (the layout
  EF9c persists). Fix: both maximize paths now `Begin` a DEDICATED identity (`###maxpanel` /
  `###maxinstance`) carrying `NoSavedSettings`, exactly like the viewport path's pre-existing
  `##viewportmax`. The docked window's identity, dock-node membership, and saved geometry are now NEVER
  touched by maximize. The EF9a `ref open` close contract is preserved unchanged (close flips `Shown`/`Open`
  + `maximize.Clear()` same frame; restore-double-click keys off the maximize KEY `name`, not the window
  label). Multi-instance + singleton semantics untouched. Verified: editor csproj builds 0-error (to a
  scratch output dir, around a running-editor bin-copy lock), reflection oracle EXIT=0 (all suites green).
  **DoD addition (EF3 coupling) — MET BY CONSTRUCTION:** editor "fullscreen" is a pure ImGui maximized-panel
  layout change; it does NOT call `Resize`/`ResizeBuffers` (the only swapchain resize is
  `runtime.Window.OnResizeCallback` → `imgui.WindowResized` + `viewport.InvalidateTargetSizes`). EF9b's
  changes are ImGui-window-identity only (grep of the two changed files for `Resize`/`swapChain` finds only
  the unrelated ImGui `NoResize` window flag), so there is NO second/undrained `ResizeBuffers`. The EF3
  resize harness (`Docs/Validation/dx12-resize-harness/`) is unchanged and remains the swapchain-resize
  regression guard. Human screenshot of maximize+restore not regressing the docked layout is batched into
  the EF9/windowing visual checkpoint.
- **EF9c — Layout persist/restore + PassthruCentralNode review — ✅ DONE:** the dock layout `.ini`
  (`EditorLayout.Save/Load`) already persisted window geometry/dock node, but NOT whether a panel is open —
  a closed core panel re-opened on next launch because `EditorPanelRegistry.Descriptor.Shown` defaults true
  and `DrawCore` simply skips a closed panel's `Begin` (so ImGui never learns it was closed). EF9c closes
  that gap: (1) `EditorPanelRegistry.HiddenKeys()` enumerates the closed core panels + `ApplyHidden(set)`
  re-applies them (viewports excluded — one renderer target, never "closed"); (2) `EditorLayout.SavePanelState/
  LoadPanelState` persist that set to a `<projectStem>.v{LayoutVersion}.panels` sidecar next to the `.ini`
  (versioned together; `DeleteSaved` clears both so Reset Layout re-shows everything, matching the existing
  `panels.ResetVisibility()`); (3) wiring — `panels.ApplyHidden(EditorLayout.LoadPanelState())` right after
  `EditorLayout.Load()` on startup (before the first frame submits panels), and a per-frame, change-gated
  `SavePanelState` at the end of `BuildUI` (closing a panel only flips `Shown`, which does NOT dirty ImGui's
  dock settings, so the existing `WantSaveIniSettings` save could not catch it; gated on a cheap joined-string
  compare so there's no per-frame file I/O). The `EditorLayout.BuildDefault` `###KindKey`/bare-name dock
  contract is untouched. **PassthruCentralNode REMOVED** — `DockSpace(..., ImGuiDockNodeFlags.None)`: the
  central node is ALWAYS filled by the Scene/Game view windows so passthrough never engaged visibly (the host
  window already carries `NoBackground`), and the review flagged the flag as a maximize/modal-capture breaker;
  dropping it is byte-identical to the eye and removes the hazard. Verified: editor csproj builds 0-error (to
  a scratch output dir, around a running-editor bin-copy lock), reflection oracle EXIT=0 (all suites green).
  Human verification (closed panel stays closed across restart; Reset Layout re-shows all; no visual change
  from the PassthruCentralNode drop) batched into the EF9/windowing visual checkpoint.
- **EF9d — Window-menu sync:** Window menu checkmarks reflect each panel's open state (closed panel
  visibly closed + re-openable). `EditorWindowRegistry` wiring already correct (`:72-105`) — bind to it.
⚠️ Same GPU area as EF3 (fullscreen). EF3 lands first; EF9b re-verifies EF3's fullscreen path. GPU-hang rule.
DoD: open/close any panel — close STICKS even when maximized, no Reset Layout needed; maximize any panel
(incl. a duplicated "Inspector 2") and restore; multi-instance + singleton (Scene/Game not duplicable)
still work; fullscreen works and routes through EF3's drained resize; layout persists across restart;
Window menu reflects state.

## EF-LAYOUT — Inspector layout model (design FIRST, then EF11/EF16/EF10/EF15 implement it)
Review catch: EF11 (adaptive label column), EF16 (nesting indent), and EF10 (per-component search) all
touch the SAME draw flow (`DrawMemberList`/`BeginGrid :2445`/`DrawNestedSlot :1973`) and can contradict
each other (EF16 wants the value column at a fixed x independent of depth; EF11 wants an adaptive label
column). Resolve them as ONE layout model BEFORE implementing any of the three:
- A single column model: value-field left edge anchored at a fixed x (does NOT shift with nesting depth);
  depth indents the LABEL/foldout only; label column adaptive within `[min, fixed-x − gap]` with ellipsis
  + hover-tooltip for overflow.
- The per-component search bar (EF10a) sits above this grid and only filters which rows draw.
This is a small design note + a shared helper, not a separate deliverable; EF11/EF16/EF10 then each
implement their slice against it. Sequence: write the model → EF16 → EF11 → EF10. Existing short-label,
shallow components must stay byte-identical.

## EF10 — Per-component (and component-list) conditional search bar
Root: no inspector member/component filter exists (`InspectorPanel.cs:51` only Add-Component search).
- **EF10a — Component-internal member search (PRIORITY):** under a component's header, a search box that
  filters that component's exposed fields (and hides foldout groups with no match). **Conditional
  visibility:** only shown when the component's field/member count exceeds a threshold OR the content
  doesn't fit the panel (don't show it on a 3-field component). Filter applies to the `DrawMemberList`
  flow; group headers (Drive/Gearbox/Steering...) hide when empty under the filter.
- **EF10b — Component-list search (secondary):** a top-of-inspector box filtering which components show,
  for many-component objects; same conditional-visibility rule.
- Factor a small reusable `EditorWidgets` search-field helper so later panels (Hierarchy/Assets/Add-
  Component) can reuse it (those panels are an optional later round, not required here).
DoD: on a heavy component (e.g. Vehicle Controller) typing "steer" leaves only steer fields + their
group; a small component shows no search box; behavior on unfiltered components byte-identical.

## EF11 — Inspector drawer-row readability (label clip + slider value overlay)
Implements the EF-LAYOUT model's label rule. Root: fixed 38% label column (`BeginGrid :2445`) clips long
labels; slider value text overlaps fill.
Fix: adaptive label column (min/auto width with ellipsis + full-text tooltip on hover) so labels like
"High Speed Steer Scale" aren't silently truncated; ensure slider value text is legible against the fill
(offset/contrast). Depends on EF-LAYOUT (shared column model) and pairs with EF16. Keep short labels neutral.
DoD: long labels readable (full via tooltip/ellipsis), slider values legible; short-label rows unchanged.

## EF12 — Rename Inspector → "Details"
Root: hardcoded `"Inspector"` (`EditorApplication.cs:179`, also `:156`).
Fix: change the registered title to "Details" (and any user-facing "Inspector" strings/menu). Window
menu + tab show "Details". (Trivial; bundle with EF7/EF11 since same file area.)
DoD: panel tab/title reads "Details" everywhere; Window menu entry updated.

## EF13 + EF14 — Hierarchy collapse/expand + collapsed-by-default (NOT trivial; do together)
Root: toolbar (`HierarchyPanel.cs:32-51`) has no collapse/expand-all; nodes use `:288
ImGuiTreeNodeFlags.DefaultOpen` → all expanded on load.
Review catch: this is more than "drop DefaultOpen". ImGui tree open-state is implicit/per-node in its
storage; to (a) collapse-all/expand-all on demand, (b) default to collapsed on FIRST load, and (c) NOT
re-collapse the user's manual expansions every frame, you need a small per-node open-state tracker the
hierarchy owns (keyed by entity id), driven into ImGui via `SetNextItemOpen` only on the frames where a
force (collapse-all / expand-all / first-load default) applies — otherwise let ImGui keep the user's state.
Fix: build that tracker once; EF13 = two toolbar buttons that set force-collapse/force-expand for the next
frame; EF14 = seed the tracker to collapsed on first scene load. After the forced frame, user expansions persist.
DoD: Collapse All folds the whole tree; Expand All unfolds it; fresh scene load → fully collapsed; a node
the user expands stays expanded across subsequent frames (no per-frame re-collapse).

## EF15 — Inspector collections: reorder/clear + fix polymorphic `List<IFoo>` serialize (RW8)
Root: per-element Remove already exists (`InspectorPanel.cs:1608`); MISSING = reorder/clear UI, AND the
serialize bug `SceneSerializer.cs:349 SerializeSequence` uses `SerializeValue` not `SerializeMemberValue`
→ `[SerializeReference] List<IFoo>`/`IFoo[]` elements lose their `$type` discriminator (don't round-trip).
**Deserialize side CONFIRMED READY (validated):** `SceneSerializer.cs:550,634,641` already detect a
`$type`-tagged recursed element (abstract element type + tag present, even when `member==null`). So the
fix is serialize-side: route sequence elements through the polymorphic-aware path (emit `$type` per
element when the element's declared/element type is `[SerializeReference]`-eligible). Non-polymorphic
lists must stay byte-identical (the existing `SerializeValue` path for primitives/structs/assets).
Fix: (1) add per-element reorder (up/down or drag handle) + clear/insert to the list drawer;
(2) make `SerializeSequence` emit per-element `$type` for polymorphic element types only.
**Oracle (review catch — reflection suite does NOT cover this):** add a concrete save→reload round-trip
test (extend `BallisticEngine.Tests.Reflection` or a scratch harness) over a `List<IFoo>` with ≥2
distinct concrete element types: serialize → deserialize → re-serialize, assert (a) all concrete types
survive, (b) YAML is byte-stable across the two serializations (deterministic key order + `$type`
placement), (c) a non-polymorphic `List<float>`/`List<Material>` is byte-identical to pre-change output.
DoD: the round-trip test passes (a/b/c); reorder/clear work in the editor.

## EF16 — Nested drawing indents too far right (never fits)
Implements the EF-LAYOUT model's depth/value-x rule (do FIRST of the layout trio). Root: `DrawNestedSlot
:1973` uses `TreeNodeEx` → fixed ImGui indent per level; each level shrinks the value column until it clips.
Fix: keep the value-field left edge at a FIXED x independent of nesting depth — depth indents only the
label/foldout (small fixed indent), not the value column. So a `list → element → nested struct → field`
chain keeps full-width value boxes at every depth.
DoD: a 4-deep nested structure still shows readable value boxes within the panel width; one extra level
doesn't push values off-screen.

---

## Suggested execution order (dependencies)
1. **EF3** (crash; unblocks safe resize for everything visual) — careful, GPU-hang rule, resize-harness first.
2. **EF9a → EF9b → EF9c → EF9d** (windowing fix on DockSpace; pairs with EF3; EF9b re-verifies EF3 fullscreen).
3. **EF5a–EF5d** (theme overhaul; the headline — AFTER windowing so panels are stable; BLOCKED on the
   EF5 identity decision below — do not start EF5a until resolved).
4. Inspector cluster (same file, batch into one editor-screenshot checkpoint):
   **EF12 (rename) → EF-LAYOUT (design) → EF16 → EF11 → EF10a/EF10b → EF15 → EF7 → EF8**.
5. Hierarchy: **EF13 + EF14** (one chunk, shared per-node open-state tracker).
6. Viewport overlay (one screenshot checkpoint): **EF1 → EF2 → EF4 → EF6**.

Theme (EF5) and windowing (EF9) are the user's emphasized asks — give them the most care and
human-screenshot verification. One commit per chunk (bisect discipline).

## Open decisions to confirm before implementing
- ~~**EF5 (BLOCKS EF5a): theme identity**~~ — **RESOLVED 2026-06-17 → (i) faithful UE5** (cool graphite +
  blue-grey shell + a single restrained azure highlight `0x3D8BD4`, NO warm accent). Acceptance bar for
  EF5a–d = "looks like UE5".
- EF4: FPS Game-view-only (a) vs Game-view-and-playing-only (b). Default (a).
- EF10: member-count threshold for showing the per-component search box (e.g. >12 fields) — tune in EF10a.
- (Resolved by validation, no longer open: EF6 full removal is safe; EF9 keeps DockPanelHost; EF15
  deserialize is ready — see Plan-review resolutions above.)
