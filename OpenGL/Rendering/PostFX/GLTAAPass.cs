using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Temporal anti-aliasing. Each render target (Scene/Game view) keeps its own ping-pong
// history pair; Render blends the current jittered frame with the reprojected history
// and the result becomes the new history.
public sealed class GLTAAPass {
    readonly StandardShader shader;

    sealed class TargetHistory {
        public readonly GLRenderTexture A = new();
        public readonly GLRenderTexture B = new();
        public bool Valid;
        public bool WriteToB;
    }

    readonly TargetHistory[] histories = { new(), new() };

    public GLTAAPass() {
        shader = GraphicAPI.CreateStandardShader(
            EmbeddedShaderSource.Read("FSQ_Vert.glsl"),
            EmbeddedShaderSource.Read("TAA_Frag.glsl"));
    }

    // Drop accumulated history (camera cut, target switch with stale data).
    public void Invalidate(int targetIndex) => histories[targetIndex].Valid = false;

    public int Render(int targetIndex, int currentColor, int depthTexture, int width, int height,
        ref Matrix4 currInvViewProj, ref Matrix4 prevViewProj, PostProcessSettings fx) {
        TargetHistory history = histories[targetIndex];

        // Size change reallocates and loses contents — start the accumulation over.
        var aOk = history.A.Ensure(width, height);
        var bOk = history.B.Ensure(width, height);
        if (!aOk || !bOk)
            history.Valid = false;

        GLRenderTexture readTex = history.WriteToB ? history.A : history.B;
        GLRenderTexture writeTex = history.WriteToB ? history.B : history.A;

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);

        writeTex.BindAsTarget();
        shader.Activate();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, currentColor);
        shader.SetInt("currentTexture", 0);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, readTex.Texture);
        shader.SetInt("historyTexture", 1);
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, depthTexture);
        shader.SetInt("depthTexture", 2);

        shader.SetMatrix4("CurrInvViewProj", ref currInvViewProj);
        shader.SetMatrix4("PrevViewProj", ref prevViewProj);
        shader.SetFloat("Feedback", Math.Clamp(fx.TaaFeedback, 0f, 0.97f));
        shader.SetBool("ValidHistory", history.Valid);
        GLBufferUtilities.DrawFullscreenQuad();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        history.Valid = true;
        history.WriteToB = !history.WriteToB;
        return writeTex.Texture;
    }

    // Standard 8-phase Halton(2,3) jitter sequence in pixel units (-0.5..0.5).
    public static Vector2 JitterOffset(int frameIndex) {
        var i = frameIndex % 8 + 1;
        return new Vector2(Halton(i, 2) - 0.5f, Halton(i, 3) - 0.5f);
    }

    static float Halton(int index, int b) {
        var result = 0f;
        var f = 1f;
        while (index > 0) {
            f /= b;
            result += f * (index % b);
            index /= b;
        }
        return result;
    }
}
