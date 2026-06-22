namespace BallisticEngine.DX12;

public sealed class Dx12FsrPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.PostProcess;
    public string Name => "FSR";

    public bool Enabled(Dx12FrameContext ctx) => ctx.FsrActive;

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
