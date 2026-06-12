using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine;

// Automatic exposure (eye adaptation). Each frame the lit HDR color is downsampled to a
// 64x64 grid of log2-luminance texels on the GPU and read back through a fence-guarded
// PBO ring: a slot is only drained once its fence reports the GPU finished it, so the CPU
// NEVER blocks on the readback (a few frames of metering latency instead, hidden by the
// adaptation ease). The drained grid is metered on the CPU (weights, optional histogram
// percentile rejection) and converted to a target EV100. Adapt() then eases the per-target
// adapted EV toward that target and publishes it to PostProcessSettings BEFORE the frame's
// lighting is resolved - the engine pre-exposes light uniforms, so the EV must be final
// before the first ExposureMultiplier read.
//
// The meter recovers ABSOLUTE scene luminance by dividing the measured buffer luminance by
// the pre-exposure multiplier the frame was rendered with, so the measurement is independent
// of the current exposure: there is no feedback loop, and adaptation speed is purely a look.
public sealed class GLAutoExposurePass {
    const int GridSize = 64;
    const int SampleCount = GridSize * GridSize;
    // EV100 = log2(L * S/K) with the engine's S=100 / K=12.5 photometric calibration.
    const float LuminanceToEV = 3f; // log2(100/12.5)

    // Readback ring depth: enough slots that the slot being reused was kicked 3 frames ago,
    // so its fence is long signaled and draining never waits on the GPU.
    const int RingSize = 3;

    sealed class TargetState {
        public float AdaptedEV = 15f;
        public float TargetEV;
        public bool HasTarget;
        public readonly int[] Pbos = new int[RingSize];
        public readonly nint[] Fences = new nint[RingSize];
        public readonly float[] PreExposure = new float[RingSize];
        public int Write;
    }

    readonly StandardShader meterShader;
    readonly TargetState[] states = { new(), new() };

    // Scratch buffers reused every frame (sorted in histogram mode).
    readonly float[] logLum = new float[SampleCount];
    readonly float[] weights = new float[SampleCount];

    int fbo;
    int meterTexture;

    public GLAutoExposurePass() {
        meterShader = GraphicAPI.CreateStandardShader(
            EmbeddedShaderSource.Read("FSQ_Vert.glsl"),
            EmbeddedShaderSource.Read("AutoExposure_Frag.glsl"));
    }

    // Frame start: ease the adapted EV toward the last metered target and publish it.
    // In Fixed mode this just tracks the dial so a later switch to auto eases from the
    // current look instead of lurching from a stale value.
    public void Adapt(int targetIndex, PostProcessSettings fx, float dt) {
        TargetState state = states[targetIndex];

        if (fx.ExposureMode == ExposureMode.Fixed) {
            state.AdaptedEV = fx.ExposureEV;
            fx.AdaptedExposureEV = fx.ExposureEV;
            return;
        }

        if (state.HasTarget) {
            // Min/max are authored as two independent dials, so a mis-dragged profile can land
            // with min > max - order them before clamping (Math.Clamp throws on an inverted range).
            var lo = MathF.Min(fx.AutoExposureLimitMin, fx.AutoExposureLimitMax);
            var hi = MathF.Max(fx.AutoExposureLimitMin, fx.AutoExposureLimitMax);
            var target = Math.Clamp(state.TargetEV, lo, hi);
            var speed = target > state.AdaptedEV
                ? fx.AutoExposureSpeedDarkToLight
                : fx.AutoExposureSpeedLightToDark;
            state.AdaptedEV += (target - state.AdaptedEV) * (1f - MathF.Exp(-dt * speed));
        }

        fx.AdaptedExposureEV = state.AdaptedEV;
    }

    // Frame end (pre-bloom): drain the oldest readback IF its fence says the GPU is done
    // (never wait - GetBufferSubData on an in-flight PBO blocks the CPU until the GPU
    // catches up, which serializes the pipeline and stutters the whole editor), then meter
    // this frame's lit color and kick the next async readback.
    public void Measure(int targetIndex, int sceneTexture, float preExposure, PostProcessSettings fx) {
        EnsureResources();
        TargetState state = states[targetIndex];
        EnsurePbos(state);

        // The write slot is the oldest in the ring (kicked RingSize frames ago).
        var slot = state.Write;
        if (state.Fences[slot] != 0) {
            var status = GL.ClientWaitSync(state.Fences[slot], ClientWaitSyncFlags.None, 0);
            if (status is not (WaitSyncStatus.AlreadySignaled or WaitSyncStatus.ConditionSatisfied))
                return; // GPU still busy with that readback; skip this frame, no stall

            GL.DeleteSync(state.Fences[slot]);
            state.Fences[slot] = 0;

            GL.BindBuffer(BufferTarget.PixelPackBuffer, state.Pbos[slot]);
            GL.GetBufferSubData(BufferTarget.PixelPackBuffer, IntPtr.Zero,
                SampleCount * sizeof(float), logLum);
            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);

            var metered = MeterTargetEV(state.PreExposure[slot], fx);
            if (!float.IsNaN(metered)) {
                state.TargetEV = metered;
                state.HasTarget = true;
            }
        }

        // GPU downsample: scene -> 64x64 log-luminance grid.
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.Viewport(0, 0, GridSize, GridSize);

        meterShader.Activate();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, sceneTexture);
        meterShader.SetInt("sceneTexture", 0);
        GLBufferUtilities.DrawFullscreenQuad();

        // Async readback + fence; drained once the fence signals, RingSize frames from now.
        GL.BindBuffer(BufferTarget.PixelPackBuffer, state.Pbos[slot]);
        GL.ReadPixels(0, 0, GridSize, GridSize, PixelFormat.Red, PixelType.Float, IntPtr.Zero);
        GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
        state.Fences[slot] = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        state.PreExposure[slot] = preExposure;
        state.Write = (slot + 1) % RingSize;

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    // CPU metering over the drained 64x64 log-luminance grid -> target EV100.
    float MeterTargetEV(float framePreExposure, PostProcessSettings fx) {
        for (var y = 0; y < GridSize; y++) {
            var ny = (y + 0.5f) / GridSize * 2f - 1f;
            for (var x = 0; x < GridSize; x++) {
                var nx = (x + 0.5f) / GridSize * 2f - 1f;
                var r2 = nx * nx + ny * ny;
                weights[y * GridSize + x] = fx.MeteringMode switch {
                    MeteringMode.CenterWeighted => MathF.Exp(-3f * r2),
                    MeteringMode.Spot => r2 <= 0.25f * 0.25f ? 1f : 0f,
                    _ => 1f,
                };
            }
        }

        var first = 0;
        var last = SampleCount;
        if (fx.ExposureMode == ExposureMode.AutomaticHistogram) {
            // Percentile rejection: sort by log-luminance and ignore the darkest/brightest
            // bands by cumulative WEIGHT, so a sun disk or a black void can't drag the meter.
            Array.Sort(logLum, weights);
            var totalWeight = 0f;
            for (var i = 0; i < SampleCount; i++)
                totalWeight += weights[i];

            var lowCut = totalWeight * Math.Clamp(fx.HistogramFilterMin, 0f, 100f) / 100f;
            var highCut = totalWeight * Math.Clamp(fx.HistogramFilterMax, fx.HistogramFilterMin, 100f) / 100f;
            var cumulative = 0f;
            first = SampleCount - 1; // degenerate cuts keep at least one sample
            for (var i = 0; i < SampleCount; i++) {
                cumulative += weights[i];
                if (cumulative < lowCut)
                    continue;
                first = Math.Min(first, i);
                if (cumulative >= highCut) {
                    last = i + 1;
                    break;
                }
            }
        }

        var weightSum = 0f;
        var logSum = 0f;
        for (var i = first; i < last; i++) {
            weightSum += weights[i];
            logSum += logLum[i] * weights[i];
        }
        if (weightSum <= 0f)
            return float.NaN; // nothing metered; the caller keeps the previous target

        // Geometric mean of buffer luminance, un-pre-exposed back to absolute scene units.
        var meanLogBuffer = logSum / weightSum;
        var logSceneLum = meanLogBuffer - MathF.Log2(MathF.Max(framePreExposure, 1e-9f));
        // Pure photographic metering maps the mean to 18% grey - technically correct, but
        // skies and sunlit exteriors render dull (real matrix meters and other engines bias
        // bright scenes up for exactly this reason). One stop toward bright is the
        // difference between an 18%-grey sky and a luminous one.
        const float pleasingBias = 1f; // stops toward brighter
        return logSceneLum + LuminanceToEV - pleasingBias;
    }

    void EnsureResources() {
        if (fbo != 0)
            return;

        fbo = GL.GenFramebuffer();
        meterTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, meterTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R32f, GridSize, GridSize, 0,
            PixelFormat.Red, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, meterTexture, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    static void EnsurePbos(TargetState state) {
        if (state.Pbos[0] != 0)
            return;

        for (var i = 0; i < RingSize; i++) {
            state.Pbos[i] = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.PixelPackBuffer, state.Pbos[i]);
            GL.BufferData(BufferTarget.PixelPackBuffer, SampleCount * sizeof(float), IntPtr.Zero,
                BufferUsageHint.StreamRead);
        }
        GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
    }
}
