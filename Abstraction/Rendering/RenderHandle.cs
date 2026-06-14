namespace BallisticEngine;

// An opaque, backend-agnostic GPU texture handle the renderer hands to the host for display
// (ImGui::Image of the Scene/Game offscreen targets). The GL backend stores its texture name; a DX12
// backend stores its shader-visible descriptor GPU handle. The value is whatever ImGui's ImTextureID
// expects for that backend — the host passes it straight through without interpreting it.
//
// Keeps raw GL `int` texture ids out of the HDRenderer/editor contract (layering: the abstraction must
// not assume GL handles). Type-safe wrapper over a native-sized integer.
public readonly struct RenderHandle {
    public readonly nint Value;
    public RenderHandle(nint value) => Value = value;
    public static implicit operator nint(RenderHandle h) => h.Value;
    public static readonly RenderHandle None = new(0);
}
