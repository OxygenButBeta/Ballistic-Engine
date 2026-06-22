using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// FAZ -1 — Render-graph v2 (Lumen GI foundation). Virtual-resource description.
//
// A Dx12RgResourceDesc is a DECLARATIVE recipe for a transient (graph-owned, pooled+aliased)
// resource. It carries no GPU object; the graph realises it as a placed ID3D12Resource during
// Compile() using GetResourceAllocationInfo (we never compute texture sizes ourselves — D3D12
// alignment/swizzle rules make that a foot-gun; see MS docs on GetResourceAllocationInfo).
//
// The footprint comparison (FootprintEquals) defines aliasing compatibility for the pool: two
// transients may share a heap region only when their allocation footprints match exactly, which
// here means identical desc (the placed-resource allocation info is a pure function of the desc).

public enum Dx12RgResourceType { Buffer, Texture2D, Texture3D }

[Flags]
public enum Dx12RgResourceFlags {
    None              = 0,
    AllowRenderTarget = 1 << 0,
    AllowDepthStencil = 1 << 1,
    AllowUnorderedAccess = 1 << 2,
}

public readonly struct Dx12RgClearValue {
    public readonly bool HasValue;
    public readonly Format Format;
    public readonly float R, G, B, A;     // color clear
    public readonly float Depth;          // depth clear
    public readonly byte Stencil;
    public readonly bool IsDepth;

    Dx12RgClearValue(bool isDepth, Format fmt, float r, float g, float b, float a, float depth, byte stencil) {
        HasValue = true; IsDepth = isDepth; Format = fmt;
        R = r; G = g; B = b; A = a; Depth = depth; Stencil = stencil;
    }

    public static Dx12RgClearValue Color(Format fmt, float r, float g, float b, float a)
        => new(false, fmt, r, g, b, a, 0, 0);
    public static Dx12RgClearValue DepthStencil(Format fmt, float depth, byte stencil)
        => new(true, fmt, 0, 0, 0, 0, depth, stencil);

    public ClearValue ToD3D() => IsDepth
        ? new ClearValue(Format, Depth, Stencil)
        : new ClearValue(Format, new Vortice.Mathematics.Color4(R, G, B, A));
}

public readonly struct Dx12RgResourceDesc : IEquatable<Dx12RgResourceDesc> {
    public readonly Dx12RgResourceType Type;
    public readonly string Name;

    // Texture extents (Width also doubles as the buffer byte size when Type == Buffer).
    public readonly long Width;
    public readonly int Height;
    public readonly int Depth;        // Texture3D depth slices
    public readonly Format Format;
    public readonly int MipLevels;
    public readonly int ArraySize;
    public readonly Dx12RgResourceFlags Flags;

    public readonly Dx12RgClearValue Clear;

    public bool IsBuffer  => Type == Dx12RgResourceType.Buffer;
    public bool IsTexture => Type != Dx12RgResourceType.Buffer;

    public bool AllowRenderTarget => (Flags & Dx12RgResourceFlags.AllowRenderTarget) != 0;
    public bool AllowDepthStencil => (Flags & Dx12RgResourceFlags.AllowDepthStencil) != 0;
    public bool AllowUav          => (Flags & Dx12RgResourceFlags.AllowUnorderedAccess) != 0;

    public long ByteSize => Width; // meaningful for buffers

    Dx12RgResourceDesc(Dx12RgResourceType type, string name, long width, int height, int depth,
        Format format, int mips, int arraySize, Dx12RgResourceFlags flags, Dx12RgClearValue clear) {
        Type = type; Name = name; Width = width; Height = height; Depth = depth;
        Format = format; MipLevels = mips; ArraySize = arraySize; Flags = flags; Clear = clear;
    }

    // --- factory helpers (terse call sites at pass setup) -----------------------------------

    public static Dx12RgResourceDesc Buffer(string name, long bytes, bool uav = false)
        => new(Dx12RgResourceType.Buffer, name, bytes, 1, 1, Format.Unknown, 1, 1,
               uav ? Dx12RgResourceFlags.AllowUnorderedAccess : Dx12RgResourceFlags.None,
               default);

    public static Dx12RgResourceDesc Texture2D(string name, int w, int h, Format fmt,
        Dx12RgResourceFlags flags = Dx12RgResourceFlags.None, int mips = 1, int arraySize = 1,
        Dx12RgClearValue clear = default)
        => new(Dx12RgResourceType.Texture2D, name, w, h, 1, fmt, mips, arraySize, flags, clear);

    public static Dx12RgResourceDesc Texture3D(string name, int w, int h, int d, Format fmt,
        Dx12RgResourceFlags flags = Dx12RgResourceFlags.None, int mips = 1,
        Dx12RgClearValue clear = default)
        => new(Dx12RgResourceType.Texture3D, name, w, h, d, fmt, mips, 1, flags, clear);

    // --- D3D12 description (fed to GetResourceAllocationInfo + CreatePlacedResource) ---------

    public ResourceDescription ToD3D() {
        ResourceFlags rf = ResourceFlags.None;
        if (AllowRenderTarget) rf |= ResourceFlags.AllowRenderTarget;
        if (AllowDepthStencil) rf |= ResourceFlags.AllowDepthStencil;
        if (AllowUav)          rf |= ResourceFlags.AllowUnorderedAccess;

        switch (Type) {
            case Dx12RgResourceType.Buffer:
                // optimizedClearValue MUST be null for buffers (D3D12 rule); flags carry UAV only.
                return ResourceDescription.Buffer((ulong)Width, rf);
            case Dx12RgResourceType.Texture3D: {
                var d = ResourceDescription.Texture3D(Format, (uint)Width, (uint)Height, (ushort)Depth,
                    (ushort)MipLevels);
                d.Flags = rf;
                return d;
            }
            default: {
                var d = ResourceDescription.Texture2D(Format, (uint)Width, (uint)Height,
                    (ushort)ArraySize, (ushort)MipLevels);
                d.Flags = rf;
                return d;
            }
        }
    }

    // Footprint identity — two transients are aliasing-compatible only if this returns true.
    // (Placed-resource allocation is a deterministic function of the desc, so exact-desc equality
    //  is the strictest-correct compatibility predicate; the heap region just needs max-size.)
    public bool FootprintEquals(in Dx12RgResourceDesc o) =>
        Type == o.Type && Width == o.Width && Height == o.Height && Depth == o.Depth &&
        Format == o.Format && MipLevels == o.MipLevels && ArraySize == o.ArraySize && Flags == o.Flags;

    public bool Equals(Dx12RgResourceDesc o) => FootprintEquals(o) && Name == o.Name;
    public override bool Equals(object obj) => obj is Dx12RgResourceDesc o && Equals(o);
    public override int GetHashCode() =>
        HashCode.Combine((int)Type, Width, Height, Depth, (int)Format, MipLevels, ((int)Flags << 8) | ArraySize);

    public override string ToString() => Type switch {
        Dx12RgResourceType.Buffer => $"{Name}(buf {Width}B{(AllowUav ? " uav" : "")})",
        Dx12RgResourceType.Texture3D => $"{Name}({Width}x{Height}x{Depth} {Format}{FlagStr()})",
        _ => $"{Name}({Width}x{Height} {Format}{FlagStr()})",
    };
    string FlagStr() => (AllowRenderTarget ? " rt" : "") + (AllowDepthStencil ? " ds" : "") + (AllowUav ? " uav" : "");
}
