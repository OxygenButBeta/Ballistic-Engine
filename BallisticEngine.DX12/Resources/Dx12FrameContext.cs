using System.Numerics;
using System.Collections.Generic;
using Vortice.Direct3D12;         // CpuDescriptorHandle
using BallisticEngine;            // DX12HDRenderer, PostProcessSettings, RenderStats, GiMode, IStaticMeshRenderer

namespace BallisticEngine.DX12;

// The per-frame shared state every IRenderPass reads — the bundle DX12HDRenderer.BeginRender used to keep
// as scattered locals. Built ONCE per frame by the orchestrator, passed BY REFERENCE to each pass.
//
// MUST be a MUTABLE CLASS, not an `in` readonly struct (design decision 2, correctness trap 5): a few
// fields are MUTATED mid-frame by the orchestrator AND by passes (SceneColor follows the FSR/native +
// back-copy branch; IblActive/Shadows/RtShadows/GiMode are resolved once mid-frame). An `in` struct
// can't carry those.
//
// R8: genuinely read-only fields are `init`-only properties (set once at construction, never after) so a
// phase-2 authored pass can't accidentally reassign ctx.View. Only the five fields the orchestrator
// mutates mid-frame have public setters: SceneColor, IblActiveThisFrame, RtShadowsThisFrame,
// ShadowsThisFrame, GiMode.
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
    public CpuDescriptorHandle SsaoResult { get; init; }   // Dx12SsaoPass.ResultSrvCpu (blurred half-res AO) — composite samples it when Doors.Ssao
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

    // --- engine-side config / output (read-only references) ---
    public Dx12RenderDoors      Doors    { get; init; }
    public PostProcessSettings  PostFX   { get; init; }
    public RenderStats          Stats    { get; init; }   // RenderStats.Scene (the active per-frame block)

    // ============ MUTATED mid-frame (the reason this is a by-ref class) ============

    // The current canonical HDR scene-color target. Starts as Target; FSR sets it to fsrOutput; composite
    // reads it; back-copy passes copy back into it. The single mechanism that models the FSR/native
    // composite-input branch + every back-copy pass uniformly.
    public Dx12OffscreenTarget SceneColor { get; set; }

    // Resolved once mid-frame by the orchestrator (IBL bake succeeded / shadows ran / RT shadows ran / the
    // GI mode after the no-RT downgrade). Passes read these instead of recomputing them.
    public bool   IblActiveThisFrame { get; set; }
    public bool   ShadowsThisFrame   { get; set; }
    public bool   RtShadowsThisFrame { get; set; }
    public GiMode GiMode             { get; set; }

    // The film-grain animation counter (DX12HDRenderer.ssgiFrame). The orchestrator SEEDS it with the
    // un-incremented giPass.SsgiFrame just before the single graph.Execute (covers the GI-Off case); when GI
    // runs, FillSsgiConstants overwrites it with the POST-increment value during the GI pass's Record (GI event
    // 500 < Composite 700, so the composite sees the fresh value within one Execute — chunk 11 step-G collapse).
    // The composite reads it for the non-deterministic grain phase only (frozen to 0 under DeterministicCapture).
    public int GrainFrame { get; set; }
}
