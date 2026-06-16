using BallisticEngine.Networking;

namespace BallisticEngine;

// The gameplay-framework network attributes (plan §4b) — plain System.Attribute, house style (like
// [Range]/[ShowIf]), zero codegen dependency. ONLY the source generator (BallisticEngine.SourceGen) and
// the editor inspector interpret them; they carry no behaviour themselves. They live in the engine
// assembly so game scripts in the collectible ALC reference them by the normal engine ref.
//
// [Networked] ⟂ [NotSerialized] (plan §10): [Networked] = WIRE replication (this generator), while
// [NotSerialized] = YAML persistence (ComponentReflection). A [Networked] auto-property still serializes
// to the scene (its authored initial value) UNLESS it is ALSO [NotSerialized] — the two axes are
// orthogonal. The dev opts a property into replication with [Networked]; that does not remove it from YAML.

// Declarative replicated state on a NetworkBehaviour auto-property (plan §4b). Default = server-write /
// everyone-read (the closed-by-default Grade-2 posture, §3): a plain [Networked] is written only by the
// state authority and read by all. Owner-write is the LOUD opt-in token [Networked(Authority.Owner)].
//
// Quantization is OPT-IN (owner decision): a bare [Networked] float ships full 32-bit lossless — no
// silent precision loss. Declaring Min/Max/Bits switches that field to the ~mm quantized packing (§11),
// which the generator emits as WireCodec.WriteQ. Bits 1..32; Min<Max required when quantizing.
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class NetworkedAttribute : Attribute {
    // Who may WRITE this field. Server (default, closed trust boundary) or Owner (the visible, loud
    // owner-write token — §3 Grade-2: a dev can still mis-declare it, the token just makes it obvious).
    public NetworkWriteAuthority Authority { get; }

    // Opt-in quantization range. Bits == 0 (the default) → full-precision, lossless. When Bits > 0 the
    // field is packed into Bits over [Min,Max] (the ~mm packing). Only meaningful for float/Vector fields.
    public float Min { get; set; }
    public float Max { get; set; }
    public int Bits { get; set; }

    public NetworkedAttribute(NetworkWriteAuthority authority = NetworkWriteAuthority.Server) =>
        Authority = authority;

    // True when this field opts into quantization (the generator emits WriteQ/ReadQ instead of Write).
    public bool IsQuantized => Bits > 0;
}

// One universal RPC attribute + a typed target (plan §4b). Reliable by default; To.Server is owner-checked
// by default (the closed trust boundary). RPCs are fire-and-forget — there is NO RPC return (L1): a
// request→response is RPC-up + [Networked] state-down + [OnChanged], never a return value. The generator
// emits a (typeId, methodId) integer-hash dispatch stub so there is NO runtime reflection (§11). P2
// generates the dispatch TABLE/stubs; the wire transport for RPCs is P4.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RpcAttribute : Attribute {
    public RpcTarget Target { get; }
    public bool Reliable { get; set; } = true;   // reliable by default; opt into unreliable for spammy FX

    public RpcAttribute(RpcTarget target) => Target = target;
}

// Change notification SEPARATED from the setter (plan §4b) — named so it survives a future prediction
// layer (a naive per-set callback would fire spuriously during rollback replay). Names a method on the
// same type that the framework invokes after a [Networked] field's value changes on apply.
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class OnChangedAttribute : Attribute {
    public string Method { get; }
    public OnChangedAttribute(string method) => Method = method;
}
