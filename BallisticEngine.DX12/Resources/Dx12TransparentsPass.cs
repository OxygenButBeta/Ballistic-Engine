using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using BallisticEngine;         // IStaticMeshRenderer, Mesh, Material, RuntimeSet, DefaultTextures, TextureType, Texture2D
using Vortice.Direct3D;        // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;             // DxcShaderStage
using Vortice.DXGI;            // Format, SampleDescription

namespace BallisticEngine.DX12;

// Transparent forward pass: after deferred + sky, draw Material.Transparent submeshes back-to-front,
// alpha-blended over the HDR scene, depth-testing the G-buffer depth (LEqual, no write). Full forward PBR
// (sun + IBL + shadows + clustered punctual) sampling material maps directly (TransparentForward.hlsl).
//
// VERBATIM MOVE (chunk 8 of the pass-graph migration): the bodies of BuildTransparentPass/DrawTransparents
// are copied unchanged, only re-rooted onto `ctx`/this pass's own fields. No logic change → eyeball-unchanged
// + SHA==golden. Copies the Dx12SkyPass/Fog/AP template (draws into `target`, owns no resolution targets).
//
// Decision 4 / R2: the head resource transition (gbuffer.DepthToReadOnly) lives right before the draw — the
// inline sky block USED to do this unconditionally and transparents inherited DepthRead from it. Now Sky is a
// graph pass gated by doors.Sky (and AP may leave depth in PixelShaderResource), so transparents emits its OWN
// head DepthToReadOnly() — idempotent no-op when an upstream pass set it, the safety net when Sky is off.
//
// Event = Transparents (450) — after Sky (350) + AerialPerspective (400), before GI/Fog/SSR. Always enabled;
// Record gathers the transparent submeshes and early-returns when there are none (no draw → no transition).
public sealed class Dx12TransparentsPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.Transparents;
    public string Name => "Transparents";

    // The inline call had NO outer-if (it was always invoked; the work-or-not gate is the per-frame gather's
    // count==0 early-return inside Record). So the pass is always enabled.
    public bool Enabled(Dx12FrameContext ctx) => true;

    // PHASE-2 V1: reads the G-buffer depth (DepthToReadOnly head) and the sun shadow map (forward-lit
    // transparents sample the cascades), blends transparent geometry IN PLACE into the HDR scene color
    // (ReadWrite — preserves the opaque-lit + sky pixels underneath).
    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.Read(b.Resource("ShadowMap"));
        b.ReadWrite(b.Resource("SceneColor"));
        // PHASE-2 V3 (chunk 15): Transparents' ONE shared-resource head transition is `gbuffer.DepthToReadOnly()`
        // (same usage class as Sky — the LEqual-no-write forward draw binds depth as a read-only DSV). Derive it;
        // the manual head in Record is gated off when the barriers door is on. NOTE: the derived emit fires when
        // the pass is Enabled (always true) EVEN when there are no transparent submeshes — but it's an idempotent
        // state-tracked DepthToReadOnly with no draw, and every downstream consumer re-asserts its own depth state
        // (R2), so the extra transition is a harmless no-op (verified SHA==golden + GBV 0-NEW, sky-off included).
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.GBufferDepthReadOnly);
    }

    // The 6 material maps in HLSL register(t0..t5) order; camera near/far — mirror the orchestrator consts.
    const int MaterialSrvCount = 6;
    const float CameraNear = 0.1f, CameraFar = 1000f;

    [StructLayout(LayoutKind.Sequential)]
    struct TransparentConstants {
        public Matrix4x4 Mvp;
        public Matrix4x4 Model;
        public Matrix4x4 View;
        public Vector3 LightDir; public float Exposure;
        public Vector3 LightColor; public float Metallic;
        public Vector3 Ambient; public float Roughness;
        public Vector3 CameraPos; public float SpecularReflectance;
        public Vector4 BaseColorFactor;
        public Vector3 EmissiveFactor; public float HasEmissive;
        public float NormalStrength, NormalFlipY, HasMetallicMap, HasRoughnessMap;
        public float PackedOrm, Cutout, UseIBL, PrefilterMaxMip;
        public float Opacity, PunctualCount; public Vector2 ScreenSize;
        public Vector2 ClusterNearFar; public Vector2 Pad;
    }

    readonly Dx12Device dev;
    ID3D12RootSignature transparentRootSig;  // b0 TransparentConstants + b1 FrameConstants + 6-SRV material table + 7-SRV lighting table + 2 samplers
    ID3D12PipelineState transparentPso;
    ID3D12Resource transparentCb;            // per-draw TransparentConstants ring
    unsafe byte* transparentCbMapped;
    int transparentCbSlotSize, transparentCbSlotCount;
    Dx12DescriptorHeap transparentSrvVisible; // per frame: 7 lighting SRVs + 6 material SRVs per draw
    readonly List<(IStaticMeshRenderer r, int submesh, float dist)> transparentItems = new();

    // VERBATIM BuildTransparentPass. Owns rootsig/PSO/CB/heap (resolution-independent — no Resize body).
    public unsafe Dx12TransparentsPass(Dx12Device device) {
        dev = device;
        var drawCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var frameCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.Pixel);
        var matRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaterialSrvCount, baseShaderRegister: 0);
        var matTable = new RootParameter1(new RootDescriptorTable1(matRange), ShaderVisibility.Pixel);
        var lightRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 7, baseShaderRegister: 6);
        var lightTable = new RootParameter1(new RootDescriptorTable1(lightRange), ShaderVisibility.Pixel);
        var wrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var clamp = new StaticSamplerDescription(ShaderVisibility.Pixel, 1, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        transparentRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
                new[] { drawCbv, frameCbv, matTable, lightTable }, new[] { wrap, clamp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("TransparentForward.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "TransparentForward.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "TransparentForward.hlsl");
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 0, 3));
        // Depth test LEqual, NO write (the G-buffer depth occludes; transparents don't write depth — sort
        // handles their order). Straight alpha blend over the HDR scene (composite tonemaps later).
        var ds = DepthStencilDescription.Default;
        ds.DepthWriteMask = DepthWriteMask.Zero;
        ds.DepthFunc = ComparisonFunction.LessEqual;
        transparentPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = transparentRootSig, VertexShader = vs, PixelShader = ps, InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullClockwise,   // back-face cull (forward parity)
            BlendState = new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha),
            DepthStencilState = ds,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Dx12GBuffer.DepthFormat, SampleDescription = new SampleDescription(1, 0),
        });

        transparentCbSlotSize = (Marshal.SizeOf<TransparentConstants>() + 255) & ~255;
        transparentCbSlotCount = 2048;   // transparent submesh draws per frame ceiling
        transparentCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)((long)transparentCbSlotSize * transparentCbSlotCount)), ResourceStates.GenericRead);
        transparentCbMapped = transparentCb.Map<byte>(0);
        // Per frame: 7 lighting SRVs (bound once) + 6 material SRVs per draw.
        transparentSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            7 + transparentCbSlotCount * MaterialSrvCount, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    // VERBATIM DrawTransparents. Call-site args re-derived from ctx: view/viewProj/camPos =
    // ctx.View/ctx.ViewProj/ctx.CamPos; lightDir/lightColor/ambient = ctx.LightDir/ctx.LightColor/ctx.Ambient;
    // iblActiveThisFrame = ctx.IblActiveThisFrame; frameCb.GPUVirtualAddress = ctx.FrameCbAddress; the lighting
    // resources (ibl/shadowMap/clusteredLights) come from ctx. AabbInFrustum/ToNumerics/BindSrvInto are local
    // copies of the orchestrator helpers (pure — frustumPlanes comes from ctx).
    public unsafe void Record(Dx12FrameContext ctx) {
        Matrix4x4 view = ctx.View, viewProj = ctx.ViewProj;
        Vector3 camPos = ctx.CamPos;
        Vector3 lightDir = ctx.LightDir, lightColor = ctx.LightColor, ambient = ctx.Ambient;
        Dx12GBuffer gbuffer = ctx.GBuffer;
        Dx12OffscreenTarget target = ctx.Target;
        Dx12IblBaker ibl = ctx.Ibl;
        Dx12ShadowMap shadowMap = ctx.ShadowMap;
        Dx12ClusteredLights clusteredLights = ctx.ClusteredLights;
        Vector4[] frustumPlanes = ctx.FrustumPlanes;
        int targetW = ctx.TargetW, targetH = ctx.TargetH;
        bool iblActiveThisFrame = ctx.IblActiveThisFrame;

        // 1) Gather transparent submeshes (per-submesh frustum cull, like the geometry pass).
        transparentItems.Clear();
        foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection) {
            if (r is null || !r.IsActive || !r.IsRenderable) continue;
            Mesh mesh = r.SharedMesh;
            if (mesh is null) continue;
            Matrix4x4 model = ToNumerics(r.Transform.WorldMatrix);
            int only = r.SubMeshIndex;
            int first = only >= 0 ? only : 0;
            int last = only >= 0 ? only : mesh.SubMeshes.Length - 1;
            for (int s = first; s <= last; s++) {
                if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                if (mesh.SubMeshes[s].IndexCount <= 0) continue;
                Material mat = r.MaterialFor(s);
                if (mat is null || !mat.Transparent) continue;
                mesh.GetSubMeshBounds(s, out Vector3 lmin, out Vector3 lmax);
                if (!AabbInFrustum(lmin, lmax, model, frustumPlanes)) continue;
                // world-space submesh center for the back-to-front sort
                var localCenter = new Vector3((lmin.X + lmax.X) * 0.5f, (lmin.Y + lmax.Y) * 0.5f, (lmin.Z + lmax.Z) * 0.5f);
                Vector3 worldCenter = Vector3.Transform(localCenter, model);
                transparentItems.Add((r, s, (worldCenter - camPos).LengthSquared()));
            }
        }
        if (transparentItems.Count == 0) return;

        // Back-to-front: farthest first (descending squared distance).
        transparentItems.Sort((a, c) => c.dist.CompareTo(a.dist));

        // 2) Per-frame lighting SRVs (t6..t12: irradiance, prefilter, BRDF, shadow + cluster lights/grid/index).
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        transparentSrvVisible.Reset();
        int lightBase = transparentSrvVisible.AllocateRange(7);
        dev.Device.CopyDescriptorsSimple(1, transparentSrvVisible.Cpu(lightBase + 0), ibl.IrradianceSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, transparentSrvVisible.Cpu(lightBase + 1), ibl.PrefilterSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, transparentSrvVisible.Cpu(lightBase + 2), ibl.BrdfSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, transparentSrvVisible.Cpu(lightBase + 3), shadowMap.SrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, transparentSrvVisible.Cpu(lightBase + 4), clusteredLights.LightSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, transparentSrvVisible.Cpu(lightBase + 5), clusteredLights.GridSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, transparentSrvVisible.Cpu(lightBase + 6), clusteredLights.IndexSrvCpu, heapType);

        var fallbackDiffuse = DefaultTextures.Neutral(TextureType.Diffuse) as Dx12Texture2D;
        float prefMaxMip = ibl != null ? ibl.PrefilterMipCount - 1 : 0f;
        float useIbl = iblActiveThisFrame ? 1f : 0f;
        float punctualCount = clusteredLights.LightCount;
        int tslot = 0;

        // 3) Draw back-to-front into the HDR color, depth-testing the G-buffer depth. Head transition (R2,
        // Decision 4): emit our OWN DepthToReadOnly (the inline sky block used to do this unconditionally;
        // now Sky is gated, so transparents re-asserts DepthRead). Idempotent no-op when upstream already set it.
        // PHASE-2 V3: skip the manual head when derived barriers are active (the graph emitted it before Record).
        if (!ctx.BarriersDerived) gbuffer.DepthToReadOnly();
        target.RenderColorWithExternalDepth(gbuffer.DsvHandle, cl => {
            cl.SetGraphicsRootSignature(transparentRootSig);
            cl.SetPipelineState(transparentPso);
            cl.SetDescriptorHeaps(transparentSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(1, ctx.FrameCbAddress);
            cl.SetGraphicsRootDescriptorTable(3, transparentSrvVisible.Gpu(lightBase));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            foreach (var item in transparentItems) {
                if (tslot >= transparentCbSlotCount) break;
                IStaticMeshRenderer r = item.r;
                Mesh mesh = r.SharedMesh;
                var vb = mesh.VertexBuffer as Dx12Buffer<Vector3>;
                var ib = mesh.IndexBuffer as Dx12IndexBuffer;
                var nb = mesh.NormalBuffer as Dx12Buffer<Vector3>;
                var ub = mesh.UvBuffer as Dx12Buffer<Vector2>;
                var tb = mesh.TangentBuffer as Dx12Buffer<Vector4>;
                if (vb?.Resource is null || ib?.Resource is null ||
                    nb?.Resource is null || ub?.Resource is null || tb?.Resource is null) continue;
                SubMeshData sub = mesh.SubMeshes[item.submesh];
                Material mat = r.MaterialFor(item.submesh);
                if (mat is null) continue;

                Matrix4x4 model = ToNumerics(r.Transform.WorldMatrix);
                Matrix4x4 mvp = model * viewProj;
                bool hasMetal = mat.Metallic is not null;
                bool hasRough = mat.Roughness is not null;
                var c = new TransparentConstants {
                    Mvp = Matrix4x4.Transpose(mvp), Model = Matrix4x4.Transpose(model),
                    View = Matrix4x4.Transpose(view),
                    LightDir = lightDir, Exposure = 1f, LightColor = lightColor, Metallic = mat.MetallicFactor,
                    Ambient = ambient, Roughness = mat.RoughnessFactor,
                    CameraPos = camPos, SpecularReflectance = mat.SpecularReflectance,
                    BaseColorFactor = ToNumerics(mat.BaseColorFactor),
                    EmissiveFactor = ToNumerics(mat.EmissiveColor) * mat.EmissiveIntensity,
                    HasEmissive = mat.IsEmissive ? 1f : 0f,
                    NormalStrength = mat.NormalStrength, NormalFlipY = mat.NormalFlipY ? 1f : 0f,
                    HasMetallicMap = hasMetal ? 1f : 0f, HasRoughnessMap = hasRough ? 1f : 0f,
                    PackedOrm = mat.PackedOrm ? 1f : 0f, Cutout = mat.Cutout ? 1f : 0f,
                    UseIBL = useIbl, PrefilterMaxMip = prefMaxMip,
                    Opacity = mat.Opacity, PunctualCount = punctualCount,
                    ScreenSize = new Vector2(targetW, targetH), ClusterNearFar = new Vector2(CameraNear, CameraFar),
                };
                *(TransparentConstants*)(transparentCbMapped + (long)tslot * transparentCbSlotSize) = c;
                cl.SetGraphicsRootConstantBufferView(0,
                    transparentCb.GPUVirtualAddress + (ulong)((long)tslot * transparentCbSlotSize));

                int matBase = transparentSrvVisible.AllocateRange(MaterialSrvCount);
                BindSrvInto(transparentSrvVisible, matBase + 0, mat.Diffuse, TextureType.Diffuse, fallbackDiffuse);
                BindSrvInto(transparentSrvVisible, matBase + 1, mat.Normal, TextureType.Normal, null);
                BindSrvInto(transparentSrvVisible, matBase + 2, mat.Metallic, TextureType.Metallic, null);
                BindSrvInto(transparentSrvVisible, matBase + 3, mat.Roughness, TextureType.Roughness, null);
                BindSrvInto(transparentSrvVisible, matBase + 4, mat.AO, TextureType.AO, null);
                BindSrvInto(transparentSrvVisible, matBase + 5, mat.Emissive, TextureType.Emissive, null);
                cl.SetGraphicsRootDescriptorTable(2, transparentSrvVisible.Gpu(matBase));

                Span<VertexBufferView> vbViews = stackalloc VertexBufferView[4];
                vbViews[0] = new VertexBufferView(vb.GpuAddress, (uint)vb.ByteSize, (uint)vb.Stride);
                vbViews[1] = new VertexBufferView(nb.GpuAddress, (uint)nb.ByteSize, (uint)nb.Stride);
                vbViews[2] = new VertexBufferView(ub.GpuAddress, (uint)ub.ByteSize, (uint)ub.Stride);
                vbViews[3] = new VertexBufferView(tb.GpuAddress, (uint)tb.ByteSize, (uint)tb.Stride);
                cl.IASetVertexBuffers(0, vbViews);
                cl.IASetIndexBuffer(new IndexBufferView(ib.GpuAddress, (uint)ib.ByteSize, Format.R32_UInt));
                cl.DrawIndexedInstanced((uint)sub.IndexCount, 1, (uint)sub.IndexStart, 0, 0);
                tslot++;
            }
        });
    }

    // VERBATIM AabbInFrustum (frustumPlanes passed in from ctx instead of the orchestrator field). The
    // 8-corner world-AABB positive-vertex test, bit-identical to the geometry/shadow cull.
    static bool AabbInFrustum(Vector3 localMin, Vector3 localMax, Matrix4x4 model, Vector4[] frustumPlanes) {
        Vector3 wlo = new(float.MaxValue), whi = new(float.MinValue);
        for (int c = 0; c < 8; c++) {
            var lc = new Vector3((c & 1) == 0 ? localMin.X : localMax.X,
                                 (c & 2) == 0 ? localMin.Y : localMax.Y,
                                 (c & 4) == 0 ? localMin.Z : localMax.Z);
            Vector3 w = Vector3.Transform(lc, model);
            wlo = Vector3.Min(wlo, w); whi = Vector3.Max(whi, w);
        }
        for (int i = 0; i < 6; i++) {
            Vector4 p = frustumPlanes[i];
            Vector3 pv = new(p.X >= 0 ? whi.X : wlo.X, p.Y >= 0 ? whi.Y : wlo.Y, p.Z >= 0 ? whi.Z : wlo.Z);
            if (p.X * pv.X + p.Y * pv.Y + p.Z * pv.Z + p.W < 0f) return false;   // fully outside this plane
        }
        return true;
    }

    // VERBATIM BindSrvInto: copy one material texture's persistent SRV into the shader-visible table at
    // `slot`. A null texture resolves to that slot's neutral default so the descriptor is always valid.
    void BindSrvInto(Dx12DescriptorHeap heap, int slot, Texture2D tex, TextureType type, Dx12Texture2D explicitFallback) {
        var dx = (tex as Dx12Texture2D)
                 ?? explicitFallback
                 ?? (DefaultTextures.Neutral(type) as Dx12Texture2D);
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(slot), dx.SrvCpu,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    // GLMatrix4/GLVector3 are System.Numerics aliases in the engine math; ToNumerics is an identity copy.
    static Matrix4x4 ToNumerics(Matrix4x4 m) => m;
    static Vector3 ToNumerics(Vector3 v) => v;
    static Vector4 ToNumerics(Vector4 v) => v;

    public void Dispose() {
        transparentSrvVisible?.Dispose();
        transparentCb?.Dispose();
        transparentPso?.Dispose();
        transparentRootSig?.Dispose();
    }
}
