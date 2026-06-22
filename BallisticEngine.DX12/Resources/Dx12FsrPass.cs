namespace BallisticEngine.DX12;

public sealed class Dx12FsrPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.PostProcess;
    public string Name => "FSR";

    // FAZ -1d — when render-graph v2 owns FSR/upscale (BALLISTIC_DX12_RG=1) the v1 graph SKIPS this pass;
    // v2 drives Record() itself. Door off (default) => RgV2OwnsFsr is false => Enabled == FsrActive, unchanged.
    public bool Enabled(Dx12FrameContext ctx) => ctx.FsrActive && !ctx.RgV2OwnsFsr;

    // FAZ -1d — render-graph v2 entry point (mirrors Dx12TaaPass.RecordV2). v2 imports SceneColor (read) +
    // GBuffer (depth read) and WRITES FsrOutput (a separate target the body owns), then calls this to run the
    // SAME record body (byte-identical to the v1 path). The v1 graph normally derives the input
    // (SceneColor/Target + GBuffer depth) -> shader-read transitions when ctx.BarriersDerived is on (the body
    // then skips its own — see the `if (!ctx.BarriersDerived)` guards in Record). Under v2 the v1 deriver is
    // bypassed (the pass is skipped in v1) AND v2 emits no barrier for the imports (by design — equal states),
    // so the body MUST own those transitions. Force the FSR-branch input states here (Target color + GBuffer
    // shader-read) so Record() never reads them in the wrong state regardless of ctx.BarriersDerived. The body
    // then transitions FsrOutput to UAV, dispatches, transitions it back to shader-read, and assigns it to
    // ctx.SceneColor — so the downstream v2 Composite (which Reads SceneColor) picks up FsrOutput.
    // NOTE: only the FSR upscaler path is wired into v2 (RgV2OwnsFsr keys off ctx.FsrActive, which is the FSR
    // upscaler). The DLSS/XESS branches of Record manage their own non-pixel-shader input states unconditionally.
    public void RecordV2(Dx12FrameContext ctx) {
        ctx.Target.ColorToShaderResource();
        ctx.GBuffer.ToShaderResource();
        Record(ctx);
    }

    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.Read(b.Resource("SceneColor"));
        b.Write(b.Resource("FsrOutput"));
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.SceneColorShaderRead);
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
    }

    const float CameraNear = 0.1f, CameraFar = 1000f;
    const float FovYRadians = 45f * (MathF.PI / 180f);

    readonly Dx12Device dev;
    public Dx12FsrPass(Dx12Device device) { dev = device; }

    public unsafe void Record(Dx12FrameContext ctx) {
        Dx12OffscreenTarget target = ctx.Target;
        Dx12GBuffer gbuffer = ctx.GBuffer;
        Dx12OffscreenTarget fsrOutput = ctx.FsrOutput;
        int targetW = ctx.TargetW, targetH = ctx.TargetH;
        bool reset = !ctx.MotionPrevValid;

        if (ctx.ActiveUpscaler == UpscalerKind.Fsr) {
            if (!ctx.BarriersDerived) {
                target.ColorToShaderResource();
                gbuffer.DepthToShaderResource();
            }

            fsrOutput.ColorToUnorderedAccess();
            dev.ExecuteSync(cl => {
                ctx.Fsr.Dispatch(cl, target.RenderTarget, gbuffer.DepthResource,
                    gbuffer.MotionResource, fsrOutput.RenderTarget,
                    targetW, targetH, new Dx12FsrUpscaler.Vector2Jitter(ctx.CurrentJitter.X, ctx.CurrentJitter.Y),
                    16.6667f, reset, ctx.PostFX.UpscaleSharpness > 0f, ctx.PostFX.UpscaleSharpness,
                    CameraNear, CameraFar, FovYRadians);
            });
            fsrOutput.ColorToShaderResource();
            ctx.SceneColor = fsrOutput;
            return;
        }

        target.ColorToNonPixelShaderResource();
        gbuffer.DepthToNonPixelShaderResource();
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
        _ = ok;
    }

    public void Dispose() { }
}
