using System.Numerics;
using System.Collections.Generic;
using Vortice.Direct3D12;         // CpuDescriptorHandle
using BallisticEngine;            // DX12HDRenderer, PostProcessSettings, RenderStats, IStaticMeshRenderer

namespace BallisticEngine.DX12;

// The per-frame shared state every IRenderPass reads — the bundle DX12HDRenderer.BeginRender used to keep
// as scattered locals. Built ONCE per frame by the orchestrator, passed BY REFERENCE to each pass.
//
// MUST be a MUTABLE CLASS, not an `in` readonly struct (design decision 2, correctness trap 5): a few
// fields are MUTATED mid-frame by the orchestrator AND by passes (SceneColor follows the FSR/native +
// back-copy branch; IblActive/Shadows/RtShadows are resolved once mid-frame). An `in` struct
// can't carry those.
//
// R8: genuinely read-only fields are `init`-only properties (set once at construction, never after) so a
// phase-2 authored pass can't accidentally reassign ctx.View. Only the five fields the orchestrator
// mutates mid-frame have public setters: SceneColor, IblActiveThisFrame, RtShadowsThisFrame,
// ShadowsThisFrame.
public sealed class Dx12FrameContext {
    // --- camera / projection (read-only) ---
    public Matrix4x4 View                { get; init; }
    public Matrix4x4 Proj                { get; init; }   // JITTERED projection (geometry/SSR render with this)
    public Matrix4x4 ViewProj            { get; init; }   // JITTERED view*proj
    public Matrix4x4 ProjUnjittered      { get; init; }   // pre-jitter proj (shadow cascade fit is stable)
    public Matrix4x4 ViewProjUnjittered  { get; init; }   // UNJITTERED view*proj (motion vectors / post math)
    public Vector2   CurrentJitter       { get; init; }   // this frame's sub-pixel jitter (pixels)
    public Vector3   CamPos              { get; init; }

    // --- light / exposure (read-only) ---
    public Vector3 LightDir    { get; init; }
    public Vector3 LightColor  { get; init; }
    public Vector3 Ambient     { get; init; }
    public float   Exposure    { get; init; }

    // --- scene inputs (read-only) ---
    // The whole-mesh renderer list (the renderer's `wholeMeshRenderers` field, a List). Concrete List type (not
    // IReadOnlyList) because the GI + Reflections passes feed it to Dx12GpuDrivenRenderer.EnsureMaterialTable,
    // which takes List<IStaticMeshRenderer>. Read-only USE (init-only); the orchestrator owns the list.
    public List<IStaticMeshRenderer> WholeMeshRenderers { get; init; }
    public Vector4[] FrustumPlanes { get; init; }         // UNJITTERED-viewProj planes (shared array, read-only use)
    public Matrix4x4[] CascadeMatrices { get; init; }     // sun shadow cascade light-MVPs, filled by RenderShadows (shared array, read-only use)

    // --- render resolution (read-only) ---
    public int TargetW { get; init; }
    public int TargetH { get; init; }
    public int OutputW { get; init; }
    public int OutputH { get; init; }

    // --- misc read-only frame state passes need (chunk 7: composite/TAA/FSR) ---
    public bool DeterministicCapture { get; init; }   // BALLISTIC_DETERMINISTIC=1 (freezes grain + exposure reset)
    public CpuDescriptorHandle SsaoResult { get; init; }   // (legacy; unused since the GTAO rework) was Dx12SsaoPass output
    // Dx12GtaoPass.ResultSrvCpu (blurred GTAO at the chosen AO resolution). The DEFERRED LIGHTING pass samples
    // it and multiplies it into the IBL ambient term only (the physically-correct layer). A stable descriptor
    // handle (gtaoA.ColorSrvCpu) — only its contents change per frame, so binding it at ctx build is correct.
    public CpuDescriptorHandle AoResult { get; init; }
    // The resolved upscale/AA branch. TaaActive == PostFX.TaaEnabled && !FsrActive && !DeterministicCapture &&
    // !Minimal; FsrActive == the FSR mode is on. TAA + FSR are mutually exclusive. TaaPass runs in the native
    // path (Enabled=!FsrActive); even when TaaActive is false it resets the (pass-owned) history-valid flag.
    public bool TaaActive { get; init; }
    public bool FsrActive { get; init; }

    // FSR shared resources + state (FsrPass dispatches; the orchestrator still OWNS the upscaler + output target
    // because the internal-vs-output render-resolution lifecycle — EnsureUpscaleTargets / native reset / mode
    // change — is whole-frame resolution management, not a leaf-post concern). Fsr is null when FSR is off.
    public Dx12FsrUpscaler      Fsr            { get; init; }
    public Dx12OffscreenTarget  FsrOutput      { get; init; }
    public bool                 MotionPrevValid{ get; init; }   // false on first frame after a (re)alloc → FSR resets its history

    // --- shared backend resources (read-only references; the objects self-track their own state) ---
    public Dx12Device          Dev            { get; init; }
    public Dx12OffscreenTarget Target         { get; init; }   // HDR scene color (the canonical render target)
    public Dx12OffscreenTarget Ldr            { get; init; }   // LDR composite output
    public Dx12GBuffer         GBuffer        { get; init; }
    public Dx12IblBaker        Ibl            { get; init; }
    public Dx12SkyLuts         SkyLuts        { get; init; }
    public Dx12ClusteredLights ClusteredLights{ get; init; }
    public Dx12ShadowMap       ShadowMap      { get; init; }
    public Dx12GpuDrivenRenderer GpuDriven    { get; init; }
    // Full-res R8 RT sun-shadow mask (1 lit / 0 shadowed) — null until RT shadows first run; the orchestrator
    // owns it (allocated/dispatched inline before deferred). The deferred pass (chunk 9) binds it to t12 when
    // RtShadowsThisFrame, else a valid unused fallback (gbuffer depth). Reference is stable per frame.
    public Dx12OffscreenTarget RtShadowMask   { get; init; }

    // The per-frame FrameConstants CB's GPU virtual address. The orchestrator owns `frameCb` (a shared
    // per-frame resource, filled once before the graph runs — see "what STAYS inline"); the Transparents
    // pass (chunk 8) binds it to its b1 FrameConstants root CBV. Read-only (the address is stable per frame).
    public ulong FrameCbAddress { get; init; }

    // Shared DXR substrate (chunk 10): the scene AS, the ID3D12Device5 facet, the one-time DXR-availability
    // probe, the per-instance bindless geometry SRVs, and the DDGI world cache — all lazily created on first
    // RT use. Shared by THREE consumers: RT sun shadows (still inline core in the orchestrator), the GI pass
    // (RT-GI branch), and the Reflections pass (RT-reflections branch). The orchestrator owns the holder (one
    // per renderer) and the same reference is threaded here every frame. Internally mutable (the holder does
    // its own lazy-create); the ctx field is a stable reference (init-only). Null is never expected.
    public Dx12DxrShared Dxr { get; init; }

    // PHASE-2 V3 (chunk 14): true when BALLISTIC_DX12_GRAPH_BARRIERS=1 (requires GRAPH=1). A MIGRATED pass reads
    // this at the head of Record: when true, the graph ALREADY emitted the pass's boundary head transition (the
    // derived set), so the pass SKIPS its own manual head transition (emit the derived set ONLY — plan §V3); when
    // false, the graph emitted nothing, so the pass emits its manual head transition as before. The two are
    // mutually exclusive → GBV sees exactly one transition sequence (not manual+derived stacked). init-only,
    // stable per frame. Un-migrated passes ignore it (they always emit their manual head transitions).
    public bool BarriersDerived { get; init; }

    // --- engine-side config / output (read-only references) ---
    public Dx12RenderDoors      Doors    { get; init; }
    public PostProcessSettings  PostFX   { get; init; }
    public RenderStats          Stats    { get; init; }   // RenderStats.Scene (the active per-frame block)

    // ============ MUTATED mid-frame (the reason this is a by-ref class) ============

    // The current canonical HDR scene-color target. Starts as Target; FSR sets it to fsrOutput; composite
    // reads it; back-copy passes copy back into it. The single mechanism that models the FSR/native
    // composite-input branch + every back-copy pass uniformly.
    public Dx12OffscreenTarget SceneColor { get; set; }

    // Resolved once mid-frame by the orchestrator. Passes read these instead of recomputing them.
    public bool   IblActiveThisFrame { get; set; }
    public bool   ShadowsThisFrame   { get; set; }
    public bool   RtShadowsThisFrame { get; set; }
    // Lumen V2 GI is active this frame (BALLISTIC_DX12_LUMEN armed + HW RT + valid scene AS). Resolved at ctx
    // build so the DEFERRED pass (event 300) can suppress its IBL diffuse ambient BEFORE the Lumen GI pass
    // (event 500) adds its own diffuse indirect — the two must agree to avoid double-counting. The Lumen pass's
    // own Enabled() recomputes the same predicate; this field mirrors it for upstream consumers.
    public bool   LumenActiveThisFrame { get; set; }

    // The film-grain animation counter. The orchestrator seeds it just before the single graph.Execute.
    // The composite reads it for the non-deterministic grain phase only (frozen to 0 under DeterministicCapture).
    public int GrainFrame { get; set; }

    // Monotonic per-frame counter (DX12HDRenderer.frameCounter), advanced every BeginRender regardless of
    // which passes run — UNLIKE GrainFrame/taaFrame which only tick when SSGI/jitter are active. Drives
    // animated effects that must move even with TAA off (the volumetric dust drift). Frozen to 0 under
    // DeterministicCapture so paused/bal-render frames stay byte-identical.
    public int FrameCounter { get; set; }
}
