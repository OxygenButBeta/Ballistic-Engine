namespace BallisticEngine;

using System.Threading;

public sealed class RenderThread {
    public static bool HeadlessSuppressed { get; set; }
    public static bool Enabled =>
        !HeadlessSuppressed &&
        System.Environment.GetEnvironmentVariable("BALLISTIC_DX12_RENDER_THREAD") == "1";

    readonly Thread renderThread;
    readonly System.Action<double> renderCallback;
    volatile bool running;
    Exception renderThreadFault;

    readonly SemaphoreSlim snapshotReady = new(0, 1);
    readonly SemaphoreSlim renderDone = new(1, 1);

    double frameDelta;

    public RenderThread(System.Action<double> renderCallback) {
        this.renderCallback = renderCallback;
        renderThread = new Thread(RenderLoop) { Name = "BallisticRenderThread", IsBackground = false };
    }

    public void Start() {
        running = true;
        renderThread.Start();
    }

    public void PublishAndKickRender(double delta) {
        if (renderThreadFault is not null) {
            var f = renderThreadFault; renderThreadFault = null;
            throw new System.Exception("Render thread faulted", f);
        }
        frameDelta = delta;
        snapshotReady.Release();
    }

    public void WaitForRenderIdle() {
        renderDone.Wait();
    }

    void RenderLoop() {
        try {
            while (running) {
                snapshotReady.Wait();
                if (!running) break;
                renderCallback(frameDelta);
                renderDone.Release();
            }
        }
        catch (Exception e) {
            renderThreadFault = e;
            try { renderDone.Release(); } catch {
            }
        }
    }

    public void Stop() {
        running = false;
        try { snapshotReady.Release(); } catch { }
        if (renderThread.IsAlive) renderThread.Join(2000);
    }
}
