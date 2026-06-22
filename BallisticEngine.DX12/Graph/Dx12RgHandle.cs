namespace BallisticEngine.DX12;

// FAZ -1 — Render-graph v2. Virtual resource handle.
//
// A handle is an opaque token a pass receives at setup. The real ID3D12Resource is resolved only
// at execute time (Dx12RgExecuteContext.Resolve) — for transients that resource does not even
// exist until Compile() places it on the aliasing heap. Mirrors UE-RDG's FRDGTextureRef pattern:
// passes traffic in handles, never raw resources.
//
// Generation guards against stale handles surviving across a graph Reset(): a handle minted in a
// previous frame's registry has a generation that no longer matches and Resolve() rejects it.

public enum Dx12RgHandleKind { Invalid, Texture, Buffer }

public readonly struct Dx12RgHandle : IEquatable<Dx12RgHandle> {
    public readonly int Id;
    public readonly int Generation;
    public readonly Dx12RgHandleKind Kind;

    public Dx12RgHandle(int id, int generation, Dx12RgHandleKind kind) {
        Id = id; Generation = generation; Kind = kind;
    }

    public bool IsValid => Kind != Dx12RgHandleKind.Invalid;
    public bool IsTexture => Kind == Dx12RgHandleKind.Texture;
    public bool IsBuffer => Kind == Dx12RgHandleKind.Buffer;

    public static readonly Dx12RgHandle Invalid = default;

    public bool Equals(Dx12RgHandle o) => Id == o.Id && Generation == o.Generation && Kind == o.Kind;
    public override bool Equals(object obj) => obj is Dx12RgHandle o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Id, Generation, (int)Kind);
    public override string ToString() => IsValid ? $"#{Id}.g{Generation}({Kind})" : "#invalid";
}

// Optional strongly-typed wrappers so a pass signature can read self-documentingly. They are thin
// value wrappers over a Dx12RgHandle and implicitly convert back, so the graph core only ever
// deals with the untyped handle.
public readonly struct Dx12RgTextureHandle {
    public readonly Dx12RgHandle Handle;
    public Dx12RgTextureHandle(Dx12RgHandle h) => Handle = h;
    public bool IsValid => Handle.IsValid && Handle.IsTexture;
    public static implicit operator Dx12RgHandle(Dx12RgTextureHandle t) => t.Handle;
    public static implicit operator Dx12RgTextureHandle(Dx12RgHandle h) => new(h);
}

public readonly struct Dx12RgBufferHandle {
    public readonly Dx12RgHandle Handle;
    public Dx12RgBufferHandle(Dx12RgHandle h) => Handle = h;
    public bool IsValid => Handle.IsValid && Handle.IsBuffer;
    public static implicit operator Dx12RgHandle(Dx12RgBufferHandle b) => b.Handle;
    public static implicit operator Dx12RgBufferHandle(Dx12RgHandle h) => new(h);
}
