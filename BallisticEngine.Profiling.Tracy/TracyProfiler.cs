using bottlenoselabs.C2CS.Runtime;
using Tracy;
using static Tracy.PInvoke;

namespace BallisticEngine.Profiling;

// Tracy-backed IProfilerBackend. Opt-in via BALLISTIC_TRACY=1 so the Tracy client (which
// buffers all events in RAM until a viewer connects) costs nothing in normal sessions.
// Attach Tools/Tracy/tracy-profiler.exe (live GUI) or tracy-capture.exe (headless).
public sealed class TracyProfiler : IProfilerBackend {
    // Tracy requires plot names to stay alive for the program lifetime (manual section 3.1).
    static readonly Dictionary<string, CString> PlotNameCache = new();

    public static bool TryInstall(string appName) {
        if (Environment.GetEnvironmentVariable("BALLISTIC_TRACY") != "1")
            return false;

        var backend = new TracyProfiler();
        backend.AppInfo(appName);
        Profiler.Backend = backend;
        Console.WriteLine("[Profiler] Tracy enabled (BALLISTIC_TRACY=1) — waiting for tracy-profiler/tracy-capture on port 8086.");
        return true;
    }

    public ulong ZoneBegin(string name, uint color, uint line, string file, string member) {
        using var fileStr = ToCString(file, out var fileLen);
        using var memberStr = ToCString(member, out var memberLen);
        using var nameStr = ToCString(name, out var nameLen);
        var srcLoc = TracyAllocSrclocName(line, fileStr, fileLen, memberStr, memberLen, nameStr, nameLen);
        var ctx = TracyEmitZoneBeginAlloc(srcLoc, 1);
        if (color != 0)
            TracyEmitZoneColor(ctx, color);
        return (ulong)ctx.Data.Id << 32 | (uint)ctx.Data.Active;
    }

    public void ZoneEnd(ulong handle) {
        TracyEmitZoneEnd(new TracyCZoneContext {
            Id = (uint)(handle >> 32),
            Active = (int)(uint)handle,
        });
    }

    public void FrameMark() => TracyEmitFrameMark(default);

    public void Plot(string name, double value) {
        if (!PlotNameCache.TryGetValue(name, out var nameStr))
            PlotNameCache[name] = nameStr = CString.FromString(name);
        TracyEmitPlot(nameStr, value);
    }

    public void Message(string text) {
        using var textStr = ToCString(text, out var textLen);
        TracyEmitMessage(textStr, textLen, 0);
    }

    void AppInfo(string text) {
        using var textStr = ToCString(text, out var textLen);
        TracyEmitMessageAppinfo(textStr, textLen);
    }

    static CString ToCString(string text, out ulong length) {
        if (text is null) {
            length = 0;
            return new CString(0);
        }

        length = (ulong)text.Length;
        return CString.FromString(text);
    }
}
