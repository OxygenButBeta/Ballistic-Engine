using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

internal sealed class Dx12MeshletData {
    public ID3D12Resource Meshlets;
    public ID3D12Resource Bounds;
    public ID3D12Resource Verts;
    public ID3D12Resource Prims;
    public int MeshletCount;
    public int VertCount;
    public int PrimCount;
}
