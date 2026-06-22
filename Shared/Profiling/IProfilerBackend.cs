using System.Runtime.CompilerServices;

namespace BallisticEngine;

public interface IProfilerBackend {
    ulong ZoneBegin(string name, uint color, uint line, string file, string member);
    void ZoneEnd(ulong handle);
    void FrameMark();
    void Plot(string name, double value);
    void Message(string text);
}
