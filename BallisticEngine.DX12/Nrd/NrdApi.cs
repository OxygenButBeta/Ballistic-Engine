using System.Runtime.InteropServices;

namespace BallisticEngine.DX12;

// P/Invoke bindings for NVIDIA Real-Time Denoisers (NRD), built from NRD/Include/{NRD,NRDDescs,NRDSettings}.h.
// NRD's core API is `extern "C"` (NRD_API), so it P/Invokes directly — no C++/CLI wrapper (same pattern as the
// NGX/FSR/XeSS bindings in Fsr/). NRD is API-agnostic: it returns COMPUTE DISPATCHES (pipeline index + resource
// list + constants + grid) that the HOST executes on its own D3D12 device. We bind these against Vortice.
//
// Layout note: NRD enums are `uint32_t` unless marked `uint8_t` (NormalEncoding/RoughnessEncoding/CheckerboardMode/
// AccumulationMode/HitDistanceReconstructionMode). All structs are sequential, default packing (matches MSVC).
internal static class NrdApi {
    const string Lib = "NRD.dll";

    // ---- enums (mirror nrd:: exactly; values are ordinal) ----
    public enum Result : uint { Success, Failure, InvalidArgument, Unsupported, NonUniqueIdentifier, MaxNum }

    public enum ResourceType : uint {
        IN_MV, IN_NORMAL_ROUGHNESS, IN_VIEWZ,
        IN_DIFF_CONFIDENCE, IN_SPEC_CONFIDENCE, IN_DISOCCLUSION_THRESHOLD_MIX,
        IN_DIFF_RADIANCE_HITDIST, IN_SPEC_RADIANCE_HITDIST,
        IN_DIFF_HITDIST, IN_SPEC_HITDIST, IN_DIFF_DIRECTION_HITDIST,
        IN_DIFF_SH0, IN_DIFF_SH1, IN_SPEC_SH0, IN_SPEC_SH1,
        IN_PENUMBRA, IN_TRANSLUCENCY, IN_SIGNAL,
        OUT_DIFF_RADIANCE_HITDIST, OUT_SPEC_RADIANCE_HITDIST,
        OUT_DIFF_SH0, OUT_DIFF_SH1, OUT_SPEC_SH0, OUT_SPEC_SH1,
        OUT_DIFF_HITDIST, OUT_SPEC_HITDIST, OUT_DIFF_DIRECTION_HITDIST,
        OUT_SHADOW_TRANSLUCENCY, OUT_SIGNAL, OUT_VALIDATION,
        TRANSIENT_POOL, PERMANENT_POOL, MAX_NUM,
    }

    public enum Denoiser : uint {
        REBLUR_DIFFUSE, REBLUR_DIFFUSE_OCCLUSION, REBLUR_DIFFUSE_SH,
        REBLUR_SPECULAR, REBLUR_SPECULAR_OCCLUSION, REBLUR_SPECULAR_SH,
        REBLUR_DIFFUSE_SPECULAR, REBLUR_DIFFUSE_SPECULAR_OCCLUSION, REBLUR_DIFFUSE_SPECULAR_SH,
        REBLUR_DIFFUSE_DIRECTIONAL_OCCLUSION,
        RELAX_DIFFUSE, RELAX_DIFFUSE_SH, RELAX_SPECULAR, RELAX_SPECULAR_SH,
        RELAX_DIFFUSE_SPECULAR, RELAX_DIFFUSE_SPECULAR_SH,
        SIGMA_SHADOW, SIGMA_SHADOW_TRANSLUCENCY, REFERENCE, MAX_NUM,
    }

    public enum Format : uint {
        R8_UNORM, R8_SNORM, R8_UINT, R8_SINT,
        RG8_UNORM, RG8_SNORM, RG8_UINT, RG8_SINT,
        RGBA8_UNORM, RGBA8_SNORM, RGBA8_UINT, RGBA8_SINT, RGBA8_SRGB,
        R16_UNORM, R16_SNORM, R16_UINT, R16_SINT, R16_SFLOAT,
        RG16_UNORM, RG16_SNORM, RG16_UINT, RG16_SINT, RG16_SFLOAT,
        RGBA16_UNORM, RGBA16_SNORM, RGBA16_UINT, RGBA16_SINT, RGBA16_SFLOAT,
        R32_UINT, R32_SINT, R32_SFLOAT,
        RG32_UINT, RG32_SINT, RG32_SFLOAT,
        RGB32_UINT, RGB32_SINT, RGB32_SFLOAT,
        RGBA32_UINT, RGBA32_SINT, RGBA32_SFLOAT,
        R10_G10_B10_A2_UNORM, R10_G10_B10_A2_UINT, R11_G11_B10_UFLOAT, R9_G9_B9_E5_UFLOAT,
        MAX_NUM,
    }

    public enum DescriptorType : uint { TEXTURE, STORAGE_TEXTURE, MAX_NUM }
    public enum Sampler : uint { NEAREST_CLAMP, LINEAR_CLAMP, MAX_NUM }
    public enum NormalEncoding : byte { RGBA8_UNORM, RGBA8_SNORM, R10_G10_B10_A2_UNORM, RGBA16_UNORM, RGBA16_SNORM, MAX_NUM }
    public enum RoughnessEncoding : byte { SQ_LINEAR, LINEAR, SQRT_LINEAR, MAX_NUM }
    public enum CheckerboardMode : byte { OFF, BLACK, WHITE, MAX_NUM }
    public enum AccumulationMode : byte { CONTINUE, RESTART, CLEAR_AND_RESTART, MAX_NUM }
    public enum HitDistanceReconstructionMode : byte { OFF, AREA_3X3, AREA_5X5, MAX_NUM }

    // ---- descs (sequential layout; pointers as IntPtr) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct SpirvBindingOffsets { public uint Sampler, Texture, ConstantBuffer, StorageTextureAndBuffer; }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibraryDesc {
        public SpirvBindingOffsets SpirvBindingOffsets;
        public IntPtr SupportedDenoisers;   // const Denoiser*
        public uint SupportedDenoisersNum;
        public byte VersionMajor, VersionMinor, VersionBuild;
        public NormalEncoding NormalEncoding;
        public RoughnessEncoding RoughnessEncoding;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DenoiserDesc { public uint Identifier; public Denoiser Denoiser; }

    [StructLayout(LayoutKind.Sequential)]
    public struct AllocationCallbacks { public IntPtr Allocate, Reallocate, Free, UserArg; }

    [StructLayout(LayoutKind.Sequential)]
    public struct InstanceCreationDesc {
        public AllocationCallbacks AllocationCallbacks;
        public IntPtr Denoisers;   // const DenoiserDesc*
        public uint DenoisersNum;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TextureDesc { public Format Format; public ushort DownsampleFactor; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ResourceDesc { public DescriptorType DescriptorType; public ResourceType Type; public ushort IndexInPool; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ResourceRangeDesc { public DescriptorType DescriptorType; public uint DescriptorsNum; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ComputeShaderDesc { public IntPtr Bytecode; public ulong Size; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct PipelineDesc {
        public ComputeShaderDesc ComputeShaderDXBC;
        public ComputeShaderDesc ComputeShaderDXIL;
        public ComputeShaderDesc ComputeShaderSPIRV;
        public IntPtr ResourceRanges;   // const ResourceRangeDesc*
        public uint ResourceRangesNum;
        [MarshalAs(UnmanagedType.U1)] public bool HasConstantData;
        public fixed byte ShaderIdentifier[256];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DescriptorPoolDesc {
        public uint PerSetTexturesMaxNum, PerSetStorageTexturesMaxNum;
        public uint TotalTexturesNum, TotalStorageTexturesNum;
        public uint SetsMaxNum;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct InstanceDesc {
        public uint ConstantBufferAndSamplersSpaceIndex, ResourcesSpaceIndex;
        public uint ConstantBufferRegisterIndex, SamplersBaseRegisterIndex, ResourcesBaseRegisterIndex;
        public uint ConstantBufferMaxDataSize;
        public IntPtr Samplers;   // const Sampler*
        public uint SamplersNum;
        public IntPtr ShaderEntryPoint;   // const char*
        public IntPtr Pipelines;   // const PipelineDesc*
        public uint PipelinesNum;
        public IntPtr PermanentPool;   // const TextureDesc*
        public uint PermanentPoolSize;
        public IntPtr TransientPool;   // const TextureDesc*
        public uint TransientPoolSize;
        public DescriptorPoolDesc DescriptorPoolDesc;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DispatchDesc {
        public IntPtr Name;        // const char*
        public uint Identifier;
        public IntPtr Resources;   // const ResourceDesc*
        public uint ResourcesNum;
        public IntPtr ConstantBufferData;   // const uint8_t*
        public uint ConstantBufferDataSize;
        [MarshalAs(UnmanagedType.U1)] public bool ConstantBufferDataMatchesPreviousDispatch;
        public ushort PipelineIndex;
        public ushort GridWidth;
        public ushort GridHeight;
    }

    // ---- C-ABI entry points (NRD_API extern "C", NRD_CALL = __cdecl on x64 → default) ----
    [DllImport(Lib)] public static extern IntPtr GetLibraryDesc();   // const LibraryDesc*
    [DllImport(Lib)] public static extern Result CreateInstance(in InstanceCreationDesc desc, out IntPtr instance);
    [DllImport(Lib)] public static extern void DestroyInstance(IntPtr instance);
    [DllImport(Lib)] public static extern IntPtr GetInstanceDesc(IntPtr instance);   // const InstanceDesc*
    [DllImport(Lib)] public static extern Result SetCommonSettings(IntPtr instance, in NrdSettings.NrdCommonSettings commonSettings);
    [DllImport(Lib)] public static extern Result SetDenoiserSettings(IntPtr instance, uint identifier, IntPtr denoiserSettings);
    [DllImport(Lib)] public static extern Result GetComputeDispatches(IntPtr instance, IntPtr identifiers, uint identifiersNum,
                                                                       out IntPtr dispatchDescs, out uint dispatchDescsNum);

    // Smoke test (BALLISTIC_DX12_NRD_SELFTEST=1): proves the DLL loads + the C-ABI + struct layout are sane by
    // reading the library desc and creating a REBLUR_DIFFUSE instance, then logging its pipeline/pool counts.
    public static unsafe void SelfTest() {
        try {
            IntPtr ldPtr = GetLibraryDesc();
            if (ldPtr == IntPtr.Zero) { Console.WriteLine("[NRD selftest] GetLibraryDesc → null"); return; }
            var ld = Marshal.PtrToStructure<LibraryDesc>(ldPtr);
            Console.WriteLine($"[NRD selftest] NRD v{ld.VersionMajor}.{ld.VersionMinor}.{ld.VersionBuild} " +
                              $"normalEnc={ld.NormalEncoding} roughnessEnc={ld.RoughnessEncoding} supportedDenoisers={ld.SupportedDenoisersNum}");

            var dd = new DenoiserDesc { Identifier = 1, Denoiser = Denoiser.REBLUR_DIFFUSE };
            DenoiserDesc* ddp = &dd;
            var icd = new InstanceCreationDesc { AllocationCallbacks = default, Denoisers = (IntPtr)ddp, DenoisersNum = 1 };
            Result r = CreateInstance(in icd, out IntPtr inst);
            if (r != Result.Success || inst == IntPtr.Zero) { Console.WriteLine($"[NRD selftest] CreateInstance → {r}"); return; }

            var id = Marshal.PtrToStructure<InstanceDesc>(GetInstanceDesc(inst));
            Console.WriteLine($"[NRD selftest] REBLUR_DIFFUSE instance OK: pipelines={id.PipelinesNum} " +
                              $"permanentPool={id.PermanentPoolSize} transientPool={id.TransientPoolSize} " +
                              $"cbMaxSize={id.ConstantBufferMaxDataSize} samplers={id.SamplersNum}");
            DestroyInstance(inst);
            Console.WriteLine("[NRD selftest] PASS — DLL + C-ABI + struct layout sane.");
        } catch (Exception e) {
            Console.WriteLine($"[NRD selftest] FAIL: {e.GetType().Name}: {e.Message}");
        }
    }
}
