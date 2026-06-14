namespace BallisticEngine;

// Backend-agnostic GPU buffer update frequency hint (the abstraction's replacement for OpenTK's
// BufferUsageHint, which must not leak into Abstraction/ per the layering rules). The GL backend maps
// these to BufferUsageHint; a DX12 backend maps them to heap type / upload strategy.
public enum BufferUsage {
    StaticDraw,   // set once, drawn many times (mesh geometry)
    DynamicDraw,  // updated occasionally
    StreamDraw,   // updated every frame (per-frame instance matrices)
}
