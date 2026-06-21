using System;
using BallisticEngine;   // UpscaleMode / UpscalerKind

namespace BallisticEngine.DX12;

// FSR temporal upscale (AMD FidelityFX Super Resolution): reconstruct the output-resolution HDR from the
// internal-res HDR color + depth + motion + jitter. REPLACES TAA (mutually exclusive). The FFX DX12 backend
// restores imported resources to their declared states at dispatch end, so the engine's per-resource state
// trackers stay consistent.
//
// VERBATIM MOVE (chunk 7 of the pass-graph migration): the body of RunFsr is copied unchanged, only re-rooted
// onto `ctx`. No logic change → eyeball-unchanged + zero NEW GBV (a MOVE-only commit). Copies the Dx12SsaoPass
// template — but FSR owns NO resources of its own: the upscaler (ctx.Fsr) and the output target (ctx.FsrOutput)
// stay ORCHESTRATOR-owned, because the internal-vs-output render-resolution lifecycle (EnsureUpscaleTargets /
// native reset / mode change) is whole-frame resolution management, not a leaf-post concern. So no ctor
// resources, no Resize.
//
// Event = PostProcess (650), registered AFTER TaaPass (TAA and FSR are mutually exclusive: FsrPass.Enabled =
// ctx.FsrActive, TaaPass.Enabled = !ctx.FsrActive, so exactly one runs). It writes ctx.FsrOutput and sets
// ctx.SceneColor = FsrOutput — the canonical composite-input branch the Composite pass (event 700) then reads.
// GENERIC UPSCALE PASS (was FSR-only): dispatches whichever upscaler family is active this frame — FSR
// (vendor-agnostic, the universal fallback), NVIDIA DLSS (NGX), or Intel XeSS. Enabled == FsrActive (the master
// "an upscaler runs" flag, mutually exclusive with TAA); ctx.ActiveUpscaler picks the concrete one. All three
// write the SAME output-res HDR target (ctx.FsrOutput) and set ctx.SceneColor = it, so the rest of the frame is
// upscaler-agnostic. The class keeps its name (Dx12FsrPass) + "FSR" event identity so the pass-graph registration
// order / SHA matrix stays stable; the FSR path is byte-unchanged.
public sealed class Dx12FsrPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.PostProcess;
    public string Name => "FSR";

    // The VERBATIM outer-if predicate: `if (fsrActive) { RunFsr(); ... }`. FsrActive == ANY upscaler is active.
    public bool Enabled(Dx12FrameContext ctx) => ctx.FsrActive;

    // PHASE-2 V1: reads the G-buffer (depth + motion for the upscaler) and the HDR scene color, then WRITES the
    // FSR output target and sets ctx.SceneColor = FsrOutput (the canonical composite-input branch). Reads
    // SceneColor (so it depends on the prior SceneColor writer — TAA in the native path, but FSR/TAA are
    // mutually exclusive at runtime via Enabled; the edge just keeps the reg-order/event-order stable).
    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.Read(b.Resource("SceneColor"));
        b.Write(b.Resource("FsrOutput"));
        // PHASE-2 V3 (chunk 16): FSR's two shared-resource head transitions are `target.ColorToShaderResource()`
        // (target == ctx.SceneColor — the internal HDR scene; TAA didn't run in the FSR path so SceneColor is
        // still `target`) and `gbuffer.DepthToShaderResource()`. Derive both. The pass-private
        // fsrOutput.ColorToUnorderedAccess() is the OUTPUT (a Write), not a boundary head → stays inline.
        // NOTE: GBV+FSR is forbidden (18GB hang, ch7) — this derived path is verified by the SHA matrix + the
        // verbatim-move argument, not GBV (FSR-path GBV is DEFERRED/UNVERIFIED).
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.SceneColorShaderRead);
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
    }

    // Render-wide camera constants (the renderer's CameraNear/CameraFar/FovYRadians, inlined — they're frame-
    // independent literals the FSR dispatch needs for its reprojection math).
    const float CameraNear = 0.1f, CameraFar = 1000f;
    const float FovYRadians = 45f * (MathF.PI / 180f);

    readonly Dx12Device dev;
    public Dx12FsrPass(Dx12Device device) { dev = device; }

    // VERBATIM RunFsr, re-rooted onto ctx, then ctx.SceneColor = ctx.FsrOutput (the orchestrator already sets
    // this at ctx build; setting it here too makes the canonical composite-input branch explicit at the pass).
    public unsafe void Record(Dx12FrameContext ctx) {
        Dx12OffscreenTarget target = ctx.Target;
        Dx12GBuffer gbuffer = ctx.GBuffer;
        Dx12OffscreenTarget fsrOutput = ctx.FsrOutput;
        int targetW = ctx.TargetW, targetH = ctx.TargetH;
        bool reset = !ctx.MotionPrevValid;    // first frame after a (re)allocation = reset the history

        if (ctx.ActiveUpscaler == UpscalerKind.Fsr) {
            // === VERBATIM FSR PATH (byte-unchanged) ===
            // PHASE-2 V3: skip the manual SceneColor + depth heads when derived barriers are active (the graph
            // emitted ctx.SceneColor.ColorToShaderResource() + gbuffer.DepthToShaderResource() before Record).
            if (!ctx.BarriersDerived) {
                target.ColorToShaderResource();  // internal HDR scene -> PixelShaderResource
                gbuffer.DepthToShaderResource(); // depth -> PixelShaderResource
            }
            // motion RT is already PixelShaderResource (gbuffer.ToShaderResource transitioned all colors).
            fsrOutput.ColorToUnorderedAccess();
            dev.ExecuteSync(cl => {
                ctx.Fsr.Dispatch(cl, target.RenderTarget, gbuffer.DepthResource,
                    gbuffer.MotionResource, fsrOutput.RenderTarget,
                    targetW, targetH, new Dx12FsrUpscaler.Vector2Jitter(ctx.CurrentJitter.X, ctx.CurrentJitter.Y),
                    16.6667f, reset, ctx.PostFX.UpscaleSharpness > 0f, ctx.PostFX.UpscaleSharpness,
                    CameraNear, CameraFar, FovYRadians);
            });
            fsrOutput.ColorToShaderResource();    // ready for the composite to sample
            ctx.SceneColor = fsrOutput;           // the canonical composite-input branch
            return;
        }

        // === VENDOR PATH (DLSS / XeSS) ===
        // Both NGX and XeSS read their inputs from a COMPUTE stage and the D3D12 contracts require
        // NON_PIXEL_SHADER_RESOURCE (XeSS explicitly; NGX evaluates in compute). The G-buffer's ToShaderResource
        // already left depth+motion in the combined PIXEL|NON_PIXEL superset (valid for both). Re-transition the
        // scene color (FSR's derived head left it PixelShaderResource) to NON_PIXEL so the compute read is legal.
        target.ColorToNonPixelShaderResource();
        gbuffer.DepthToNonPixelShaderResource();   // idempotent if already in the superset
        fsrOutput.ColorToUnorderedAccess();
        bool ok = false;
        dev.ExecuteSync(cl => {
            if (ctx.ActiveUpscaler == UpscalerKind.Dlss && ctx.Dlss != null)
                ok = ctx.Dlss.Dispatch(cl, target.RenderTarget, gbuffer.DepthResource,
                    gbuffer.MotionResource, fsrOutput.RenderTarget,
                    targetW, targetH, ctx.CurrentJitter.X, ctx.CurrentJitter.Y, reset);
            else if (ctx.ActiveUpscaler == UpscalerKind.Xess && ctx.Xess != null)
                ok = ctx.Xess.Dispatch(cl, target.RenderTarget, gbuffer.DepthResource,
                    gbuffer.MotionResource, fsrOutput.RenderTarget,
                    targetW, targetH, ctx.CurrentJitter.X, ctx.CurrentJitter.Y, reset);
        });
        fsrOutput.ColorToShaderResource();
        ctx.SceneColor = fsrOutput;
        // The vendor upscaler may fail at runtime (e.g. CreateFeature failed mid-session). Output is then
        // whatever was last in fsrOutput — not a crash; the next EnsureUpscaleTargets will re-resolve and can
        // latch the family unavailable so it falls back to FSR. (ok is consumed only for that purpose.)
        _ = ok;
    }

    public void Dispose() { }   // owns no resources (ctx.Fsr / ctx.FsrOutput are orchestrator-owned)
}
