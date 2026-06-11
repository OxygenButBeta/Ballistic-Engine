namespace BallisticEngine;

// Frame-transient render-target pool. Post passes Acquire() their scratch targets instead of
// owning them per view: at frame end EVERYTHING acquired returns to the free list, so the
// editor's Scene and Game renders (and all passes within one render) share one set of
// textures instead of each pass keeping two of its own. History buffers that must survive
// across frames (TAA/SSGI/volumetric accumulation) stay pass-owned — never pool those.
sealed class GLRenderTexturePool {
    public static readonly GLRenderTexturePool Shared = new();

    sealed class Entry {
        public GLRenderTexture Rt;
        public long LastUsedFrame;
    }

    readonly List<Entry> free = new();
    readonly List<Entry> inUse = new();
    long frame;
    const int TrimAfterFrames = 240; // a few seconds idle -> give the VRAM back

    public GLRenderTexture Acquire(int width, int height) {
        // Exact-size match avoids a realloc; otherwise resize the most recently freed one.
        var pick = -1;
        for (var i = 0; i < free.Count; i++)
            if (free[i].Rt.Width == width && free[i].Rt.Height == height) {
                pick = i;
                break;
            }
        if (pick < 0 && free.Count > 0)
            pick = free.Count - 1;

        Entry entry;
        if (pick >= 0) {
            entry = free[pick];
            free.RemoveAt(pick);
        }
        else {
            entry = new Entry { Rt = new GLRenderTexture() };
        }

        entry.Rt.Ensure(width, height);
        inUse.Add(entry);
        return entry.Rt;
    }

    // Call once per rendered frame, after the composite has consumed every transient target.
    public void EndFrame() {
        foreach (Entry entry in inUse) {
            entry.LastUsedFrame = frame;
            free.Add(entry);
        }
        inUse.Clear();
        frame++;

        for (var i = free.Count - 1; i >= 0; i--)
            if (frame - free[i].LastUsedFrame > TrimAfterFrames) {
                free[i].Rt.Dispose();
                free.RemoveAt(i);
            }
    }
}
