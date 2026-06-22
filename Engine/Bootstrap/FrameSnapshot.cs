namespace BallisticEngine;

using System.Collections.Generic;

public static class FrameSnapshot {
    static readonly List<IStaticMeshRenderer> renderSetSnapshot = new(512);

    public static IReadOnlyList<IStaticMeshRenderer> RenderSet => renderSetSnapshot;

    [System.ThreadStatic] static bool onRenderThread;
    public static bool IsRenderThreadDrawing => onRenderThread;

    static readonly List<IStaticMeshRenderer> publishList = new(512);

    static readonly int ParallelPublishThreshold =
        int.TryParse(System.Environment.GetEnvironmentVariable("BALLISTIC_PARALLEL_PUBLISH_MIN"), out int t) && t > 0
            ? t : 256;

    public static void PublishFromGameThread() {
        renderSetSnapshot.Clear();
        publishList.Clear();
        foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection) {
            if (r is null) continue;
            renderSetSnapshot.Add(r);
            if (r.Transform is not null) publishList.Add(r);
        }

        int n = publishList.Count;
        if (n >= ParallelPublishThreshold) {
            for (int i = 0; i < n; i++) _ = publishList[i].Transform.WorldMatrix;
            JobSystem.For(n, PublishIndex, batchSize: 64);
        }
        else {
            for (int i = 0; i < n; i++) publishList[i].Transform.PublishWorldForRender();
        }

        SceneManager.RenderCamera?.transform?.PublishWorldForRender();
    }

    static readonly Action<int> PublishIndex = i => publishList[i].Transform.PublishWorldForRender();

    public static void BeginRenderThreadFrame() => onRenderThread = true;
    public static void EndRenderThreadFrame() => onRenderThread = false;
}
