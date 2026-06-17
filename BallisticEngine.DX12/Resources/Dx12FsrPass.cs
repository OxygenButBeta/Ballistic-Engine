using System;

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
public sealed class Dx12FsrPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.PostProcess;
    public string Name => "FSR";

    // The VERBATIM outer-if predicate: `if (fsrActive) { RunFsr(); ... }`.
    public bool Enabled(Dx12FrameContext ctx) => ctx.FsrActive;

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
        Dx12FsrUpscaler fsr = ctx.Fsr;
        int targetW = ctx.TargetW, targetH = ctx.TargetH;

        target.ColorToShaderResource();      // internal HDR scene -> PixelShaderResource
        gbuffer.DepthToShaderResource();      // depth -> PixelShaderResource
        // motion RT is already PixelShaderResource (gbuffer.ToShaderResource transitioned all colors).
        fsrOutput.ColorToUnorderedAccess();
        bool reset = !ctx.MotionPrevValid;    // first frame after a (re)allocation = reset the history
        dev.ExecuteSync(cl => {
            fsr.Dispatch(cl, target.RenderTarget, gbuffer.DepthResource,
                gbuffer.MotionResource, fsrOutput.RenderTarget,
                targetW, targetH, new Dx12FsrUpscaler.Vector2Jitter(ctx.CurrentJitter.X, ctx.CurrentJitter.Y),
                16.6667f, reset, ctx.PostFX.UpscaleSharpness > 0f, ctx.PostFX.UpscaleSharpness,
                CameraNear, CameraFar, FovYRadians);
        });
        fsrOutput.ColorToShaderResource();    // ready for the composite to sample
        ctx.SceneColor = fsrOutput;           // the canonical composite-input branch
    }

    public void Dispose() { }   // owns no resources (ctx.Fsr / ctx.FsrOutput are orchestrator-owned)
}
