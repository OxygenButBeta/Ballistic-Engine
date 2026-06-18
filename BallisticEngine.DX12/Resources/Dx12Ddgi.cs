using System;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// DDGI world-probe radiance cache (GI plan P2 — the chosen P2, replacing the Lumen mesh-card surface cache;
// see Docs/Plans/dx12-lumen-gi-plan.md Phase 2). A camera-centered 3D grid of irradiance probes: each probe
// stores incoming radiance as a small OCTAHEDRAL irradiance map + a depth-moments map (mean + mean-squared
// distance) for the Chebyshev visibility (leak) test. Probes are updated by tracing rays against the scene
// TLAS and shading the hit with the EXISTING P1 world-radiance path (DxrGi.hlsl), blended over time with a
// hysteresis EMA — so multi-bounce is free (update rays read last frame's probe field) and the result is
// stable under camera motion. A shading point gathers the 8 enclosing probes, trilinear + Chebyshev-weighted.
// Published technique (Majercik et al. 2019 JCGT; NVIDIA RTXGI) — no bake, no authoring, fully dynamic.
//
// P2.0 (this file's first cut): the GRID + the two atlas textures (irradiance + depth) as UAV/SRV, the
// constants, and camera-centered placement. The update/blend/gather compute passes land in P2.1+.
public sealed class Dx12Ddgi : IDisposable {
    readonly Dx12Device dev;

    // --- Grid dimensions (probes per axis). Start modest; tune to the GTX-1660 VRAM/ray budget in P2.5.
    // 16 x 8 x 16 = 2048 probes. Camera-centered, snapped to the probe spacing so it slides smoothly.
    public const int ProbesX = 16, ProbesY = 8, ProbesZ = 16;
    public const int ProbeCount = ProbesX * ProbesY * ProbesZ;

    // --- Octahedral tile sizes (interior texels; a 1px border is added for correct bilinear wrap at edges).
    public const int IrradianceTexels = 6;    // 6x6 octahedral irradiance per probe (RGBA16F)
    public const int DepthTexels = 16;         // 16x16 octahedral depth moments per probe (RG16F)
    const int Border = 1;
    const int IrrTile = IrradianceTexels + 2 * Border;   // 8
    const int DepthTile = DepthTexels + 2 * Border;       // 18

    // Atlas layout: probes flattened as a 2D grid of tiles, (ProbesX*ProbesZ) columns x ProbesY rows. So a
    // probe (px,py,pz) → tile column = pz*ProbesX + px, tile row = py. One draw/dispatch covers the atlas.
    public const int TilesWide = ProbesX * ProbesZ;       // 256
    public const int TilesHigh = ProbesY;                  // 8
    public static int IrradianceAtlasW => TilesWide * IrrTile;      // 2048
    public static int IrradianceAtlasH => TilesHigh * IrrTile;      // 64
    public static int DepthAtlasW => TilesWide * DepthTile;         // 4608
    public static int DepthAtlasH => TilesHigh * DepthTile;         // 144

    // Atlas textures (compute-written UAV + gather-read SRV). The atlases are PERSISTENT resources; their
    // descriptors are created per-dispatch into a shader-visible heap by the update/gather passes (P2.1+) —
    // NOT registered in Dx12Backend.BindlessHeap, which the material table Resets (would clobber them).
    public ID3D12Resource IrradianceTex => irradianceTex;
    public ID3D12Resource DepthTex => depthTex;
    ID3D12Resource irradianceTex, depthTex;

    // GPU address of the per-probe ProbeState buffer (relocation offset + active flag), for OTHER passes that
    // sample the DDGI field (Phase 4 screen-probe trace's far-field handoff). Null until Build().
    public ulong ProbeStateGpuAddress => probeState?.GPUVirtualAddress ?? 0;

    // The grid description for THIS frame (origin/spacing/dims + the irrTexels & normalBias the gather uses) —
    // so a consumer that samples the DDGI field (Phase 4) can build the matching grid CBV. Cheap struct copy;
    // intensity/feedback/round-robin fields are irrelevant to a pure field SAMPLE and left at neutral.
    public DdgiConstants GridConstants() => Constants(frameCounter, 0.97f, 1f, false, true);

    // --- Grid placement (world space). Origin = the corner probe; spacing = metres between probes. The grid
    // is camera-centered: re-snapped each frame to the camera so coverage follows the view (a single clipmap
    // cascade for now). ProbeSpacing sets the covered volume = spacing * (probes-1) per axis.
    public Vector3 Origin { get; private set; }
    // Live spacing default = 2m → ~30x14x30m covered volume. In BAKED mode we tighten to 1.2m: with no per-frame
    // ray budget to respect, denser probes (the most visible quality lever — finer indirect detail, less trilinear
    // blur between probes) are free at runtime. Covered volume shrinks to ~18x8x18m, which is fine for an interior;
    // a future cascade extends the far field. BALLISTIC_DX12_DDGI_SPACING overrides (metres).
    public Vector3 Spacing { get; private set; } = new(2.0f, 2.0f, 2.0f);
    float BakedSpacing {
        get => float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_SPACING"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s) && s > 0.1f
            ? s : 1.2f;
    }

    public bool Allocated => irradianceTex != null;

    // Per-pass constants shared by update/blend/gather (std140-ish; matches Ddgi.hlsl). Kept here so every
    // pass sees ONE grid definition.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct DdgiConstants {
        public Vector4 OriginSpacingX;   // xyz = grid origin (world), w = spacing.x
        public Vector4 SpacingYZ;        // x = spacing.y, y = spacing.z, z/w = pad
        public Vector4 ProbeDims;        // xyz = (ProbesX,ProbesY,ProbesZ) as floats, w = ProbeCount
        public Vector4 Params0;          // x=irrTexels y=depthTexels z=hysteresis w=frameIndex
        public Vector4 Params1;          // x=maxRayDist y=normalBias z=feedbackEnable w=intensity
        public Vector4 Params2;          // P2.5 round-robin: x=updateFraction(N) y=phase(0..N-1) z=fullUpdate(1/0) w=emissiveEnable
        // CHUNK 1 GPU progressive bake. The probe-SELECTION decision lives entirely on the GPU (per-probe
        // camera-distance band + a converged-frame counter in ProbeBakeState) — the CPU only advances bakeWave by
        // frame, so there is NO per-probe CPU work / readback to pick probes (user: "işin ne kadarı gpu da olursa").
        public Vector4 Params3;          // xyz = camera world pos, w = band width (metres per distance band)
        public Vector4 Params4;          // x=bakeEnable(1/0) y=bakeWave(max open band this frame) z=convergeTarget w=pad
    }

    // --- P2.1 probe-update plumbing ---
    public const int RaysPerProbe = 144;   // MUST match DdgiTrace/DdgiBlend HLSL (SphericalFibonacci ray count)

    // --- P2.5 GTX-1660 budget lock: ROUND-ROBIN probe update. Trace/blend/classify only the probes whose phase
    // matches this frame (probe % UpdateFraction == phase); the rest keep last frame's atlas (DDGI is built for
    // exactly this — the field is temporally stable, so a probe that's a few frames stale is fine). This cuts
    // the per-frame ray + shade cost by UpdateFraction (1/8 default = 256 of 2048 probes/frame, ~37k rays vs
    // ~295k), to hold ≤~2ms GI on a weak-RT card (the 1660 has ~5-8x weaker RT than the RX 9070 XT dev card, so
    // the dev-card cost is targeted very low — conservative extrapolation). Env door retunes it (1/4 if motion
    // lag shows). UpdateFraction=1 → every probe every frame (the pre-P2.5 behaviour). The full grid refreshes
    // every UpdateFraction frames; convergence is well under 0.5s at 60fps for the default. ---
    int? updateFractionEnv;
    int UpdateFraction {
        get {
            if (updateFractionEnv == null) {
                updateFractionEnv = int.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_UPDATE_FRACTION"),
                    out int n) && n >= 1 ? n : 8;
            }
            return updateFractionEnv.Value;
        }
    }

    // P2.5 determinism: on the CAPTURE path (BALLISTIC_SCREENSHOT set), the EMA-accumulated probe field is only
    // partially converged at a paused frame N → the captured image depends on the exact SCREENSHOT_FRAME and is
    // a transient, not the steady state. Warm-up runs the probe update many times (each its own submit) in the
    // FIRST DDGI frame, FULL-grid (round-robin disabled so every probe converges), so the captured image is the
    // converged field — byte-deterministic + independent of the capture frame. Default iterations cover the
    // hysteresis 0.97 settling (~1/(1-0.97)=33 effective samples; 64 gives margin). Off in play (cost is one-
    // shot, not per-frame). BALLISTIC_DX12_DDGI_WARMUP=0 disables; =N overrides the count.
    int? warmupEnv;
    int WarmupIterations {
        get {
            if (warmupEnv == null) {
                string raw = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_WARMUP");
                if (int.TryParse(raw, out int n)) warmupEnv = Math.Max(0, n);
                // BAKED mode: converge DEEPER than the capture default (128 vs 64). The whole point of baking is we
                // pay this once and then run free — so we spend the converge budget on QUALITY. With hysteresis
                // 0.97 the field needs ~33 samples to settle; 128 over-converges so even slow-fading bounce + the
                // multi-bounce feedback (each iteration reads the last field) fully resolves → a cleaner, deeper
                // indirect than the live round-robin ever reaches. (This is the "sample quality must go up" ask.)
                else if (BakedMode) warmupEnv = 128;
                // Default ON only for a PAUSED capture (the deterministic-diff use case — static camera, so the
                // converged-then-frozen field is sampled at the right probe positions). A moving/play screenshot
                // gets live round-robin updates instead (no point converging a field the camera will leave).
                else warmupEnv = (Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT") != null
                    && Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT_PAUSED") == "1") ? 64 : 0;
            }
            return warmupEnv.Value;
        }
    }
    bool warmedUp;   // the one-shot warm-up has run (guards the first DispatchDdgi only)

    // --- BAKED (frozen) MODE: the user-facing "compute GI once on scene open, then freeze" mode. This is the
    // SAME machinery as the capture-path warm-up (TryWarmUp → converge full-grid → then skip the per-frame
    // update), but driven explicitly in PLAY/EDITOR instead of being locked to a paused screenshot. Why this is
    // the right answer to "performanssız + ghosting": once frozen the per-frame ray + EMA temporal cost is ZERO
    // (the field is a static SRV the gather samples), so ghosting is structurally impossible and the GI is ~free.
    // The trade: a frozen field doesn't follow moving lights / a sun cycle — Rebake() re-converges on demand
    // (scene load, light change, or a manual editor button). BALLISTIC_DX12_DDGI_BAKED=1 forces it on headless.
    bool bakeModeEnv, bakeModeEnvRead;
    public bool BakedMode {
        get {
            if (!bakeModeEnvRead) {
                bakeModeEnvRead = true;
                bakeModeEnv = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_BAKED") == "1";
            }
            return bakeModeEnv || bakedModeRequested;
        }
    }
    bool bakedModeRequested;            // set by the volume/editor bridge (BakedMode on without the env door)
    public void SetBakedMode(bool on) { if (on != bakedModeRequested) { bakedModeRequested = on; if (on) Rebake(); } }

    // Force a fresh converge: clears the warmed-up latch so the NEXT DispatchDdgi re-runs the full-grid warm-up
    // (TryWarmUp). Call on scene open, a light/sun change, or the editor "Rebake GI" button. Cheap to call — the
    // actual cost is the one-shot converge on the next frame, not here.
    public void Rebake() {
        warmedUp = false;
        bakeFrozen = false; bakeWave = 0; bakeWaveFrames = 0; bakeFrameCounter = 0;
        bakeProbeStateCleared = false;   // ch1: re-clear the GPU converged-counters on the next dispatch
    }
    bool bakeProbeStateCleared;          // the GPU ProbeBakeState counters have been zeroed for this bake run

    // Emissive-as-GI-source: when set, the trace's ShadeHit adds the hit surface's self-emission L_e so
    // emissive surfaces act as area lights in the probe field (rides Params2.w → DdgiTrace emissiveEnable).
    // Set by the renderer from BALLISTIC_DX12_GI_EMISSIVE (default ON). Default true so a missing setter
    // = the correct on behaviour.
    public bool EmissiveEnabled = true;

    // RayData[probe*RaysPerProbe + ray] = (radiance.rgb, hitDistance), written by the trace pass, read by the
    // blend pass. ProbeCount*144*16B = ~4.7 MB — sized once for the static grid; persistent UAV.
    ID3D12Resource rayData;

    // CHUNK1 GPU progressive bake: per-probe converged-frame counter (uint). Trace bumps it (u1); blend/classify
    // read it (t1) to mirror the eligibility test. ProbeCount*4B = ~8KB. Persistent; zeroed on each (re)bake.
    ID3D12Resource probeBakeState;
    ID3D12Resource bakeZeroUpload;   // persistent zero upload, copied over probeBakeState on Rebake

    // Trace pass (DdgiTrace.hlsl): inline RayQuery in a COMPUTE PSO (not an RT PSO — RayQuery needs no SBT).
    // Root sig mirrors DxrGi exactly so the bindless hit-shading is byte-identical: CBV b0/b1, a table for
    // {t0 TLAS, t3 irradiance cube} living in the BindlessHeap's reserved tail (so ResourceDescriptorHeap[]
    // geo reads share the one bound heap), root SRV t5/t6/t7, root UAV u0 RayData, static samplers s0/s1.
    ID3D12RootSignature traceRootSig;
    ID3D12PipelineState tracePso;

    // Blend pass (DdgiBlend.hlsl): two compute entry points (CSIrradiance→u0 irr atlas, CSDepth→u1 depth
    // atlas). Self-contained — no bindless: CBV b0, root SRV t0 RayData, and the atlas UAV via this own tiny
    // shader-visible heap (irr at slot 0 = u0, depth at slot 1 = u1).
    ID3D12RootSignature blendRootSig;
    ID3D12PipelineState blendIrrPso, blendDepthPso;
    ID3D12PipelineState borderIrrPso, borderDepthPso;   // P2.2 octahedral border-wrap (same root sig as blend)
    ID3D12PipelineState classifyPso;    // P2.4 probe classification + relocation (same root sig as blend)
    Dx12DescriptorHeap blendHeap;       // 3 UAVs: [0]=irradiance (u0), [1]=depth (u1), [2]=ProbeState (u2)
    ID3D12Resource probeState;          // P2.4: ProbeCount float4 {relocation offset.xyz, active}

    // P2.2 GATHER pass (DdgiGather.hlsl): per-pixel, reads G-buffer + the two atlases → albedo*E into
    // ssgiTarget. Self-contained heap (5 SRVs depth/normal/albedo/irrAtlas/depthAtlas + 1 UAV ssgiTarget),
    // rebuilt per frame because ssgiTarget can resize. Root sig: CBV b0 (grid) + CBV b1 (InvVP+preExp) +
    // table {t0..t4 SRV, u0 UAV} + static linear-clamp sampler.
    ID3D12RootSignature gatherRootSig;
    ID3D12PipelineState gatherPso;
    Dx12DescriptorHeap gatherHeap;       // 6 descriptors, rebuilt each gather
    ID3D12Resource gatherCb;             // DdgiGatherExtra (InvViewProj + preExp + screen)
    unsafe byte* gatherCbMapped;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct DdgiGatherExtra { public Matrix4x4 InvViewProj; public Vector4 GParams; }   // GParams: preExp, w, h, _

    // CBV for the per-dispatch DdgiConstants (upload heap, mapped, refilled each frame).
    ID3D12Resource constCb;
    unsafe byte* constCbMapped;

    bool built;
    int frameCounter;   // drives ray-rotation jitter + the first-frame hard-set in the blend EMA

    // --- STABILITY: stop the field from "boiling" (visibly changing every frame) while the camera/scene is
    // static. The boiling was three compounding causes: (1) a NEW per-frame jitter seed → different ray dirs →
    // a different probe estimate every update; (2) the 1/8 round-robin → a different probe SUBSET each frame, so
    // even with a frozen field the visible set keeps shifting; (3) a slow EMA that never settles. The fix: track
    // camera motion; when static, FREEZE the jitter seed (rays stop rotating → each probe converges to one
    // estimate) and let the EMA finish settling. A real move resumes rotation immediately (disocclusion needs
    // fresh samples). `Origin` snapping already filters sub-cell motion; this filters sub-frame jitter churn.
    Vector3 lastCamPos;
    bool haveLastCam;
    int staticFrames;        // consecutive frames the camera barely moved
    int jitterSeed;          // the seed actually fed to the ray jitter — frozen while static
    public bool IsStatic => staticFrames >= 8;

    // --- CHUNK 1 GPU progressive bake state (CPU side is just scalars; the per-probe decision is on the GPU). ---
    Vector3 bakeCamPos;          // camera pos captured in Update(), fed to the GPU band test via Params3
    int bakeWave;                // highest distance band opened so far — advances one band every few frames
    int bakeWaveFrames;          // frames spent on the current wave (paces how fast bands open)
    bool bakeFrozen;             // all probes converged → CPU stops dispatching trace/blend (the freeze)
    int bakeFrameCounter;        // frames since the bake (re)started — used by the CPU-only completion fallback
    // ConvergeTarget = how many times a probe must trace before it freezes. Deep (the field is one-shot in baked
    // mode, so we over-converge for quality). Env BALLISTIC_DX12_DDGI_CONVERGE overrides (ch2 raises it).
    int? convergeTargetEnv;
    public int ConvergeTarget {
        get {
            if (convergeTargetEnv == null)
                convergeTargetEnv = int.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_CONVERGE"),
                    out int n) && n >= 1 ? n : 48;
            return convergeTargetEnv.Value;
        }
    }
    // Distance band width (metres). A new band opens every BandFrames frames, so the bake ripples outward from the
    // camera. Total bands ≈ grid half-extent / bandWidth → the whole grid is opened in (bands * BandFrames) frames.
    float BandWidth => MathF.Max(Spacing.X * 1.5f, 1.0f);
    const int BandFrames = 2;    // frames before opening the next outward band (paces the ripple — not too eager)
    // Max band = furthest probe corner / band width (so the wave eventually covers every probe).
    int MaxBand => (int)MathF.Ceiling(new Vector3(Spacing.X * ProbesX, Spacing.Y * ProbesY, Spacing.Z * ProbesZ).Length() * 0.5f / BandWidth) + 1;
    public bool IsBakeComplete => bakeFrozen;
    // CPU-only completion estimate (no GPU readback needed for correctness): the bake is done once every band has
    // been open long enough for its probes to hit ConvergeTarget. Conservative upper bound = all bands opened
    // (MaxBand*BandFrames) + the convergence tail (ConvergeTarget frames for the last-opened band). A throttled GPU
    // readback (ch1 success log / editor progress) can confirm earlier, but this guarantees the freeze happens.
    int BakeCompleteFrameEstimate => MaxBand * BandFrames + ConvergeTarget + 4;

    public Dx12Ddgi(Dx12Device device) { dev = device; }

    public void EnsureAllocated() {
        if (Allocated) return;
        irradianceTex = CreateAtlas(IrradianceAtlasW, IrradianceAtlasH, Format.R16G16B16A16_Float);
        depthTex = CreateAtlas(DepthAtlasW, DepthAtlasH, Format.R16G16_Float);
    }

    // Camera-centered snap: place the grid so the camera sits near its centre, snapped to whole probe
    // spacings (so probes don't swim under sub-cell camera motion → temporal stability). Call per frame.
    bool bakedSpacingApplied;
    public void Update(Vector3 cameraPos) {
        // BAKED mode (CHUNK 0 stopgap, single dense grid — superseded by the cascade in ch3): tighten the probe
        // spacing ONCE so denser probes give finer indirect. Applied once (a one-shot, not per-frame) so it never
        // thrashes the snap, and only in BakedMode so the live path is byte-identical.
        if (BakedMode && !bakedSpacingApplied) {
            bakedSpacingApplied = true;
            float s = BakedSpacing;
            Spacing = new Vector3(s, s, s);
        }
        Vector3 half = new(
            Spacing.X * (ProbesX - 1) * 0.5f,
            Spacing.Y * (ProbesY - 1) * 0.5f,
            Spacing.Z * (ProbesZ - 1) * 0.5f);
        Vector3 snapped = new(
            MathF.Round(cameraPos.X / Spacing.X) * Spacing.X,
            MathF.Round(cameraPos.Y / Spacing.Y) * Spacing.Y,
            MathF.Round(cameraPos.Z / Spacing.Z) * Spacing.Z);
        Origin = snapped - half;

        // STABILITY: detect a (near-)static camera. Threshold ~2cm/frame — below it the field should converge and
        // hold, not boil. A move resets the static counter, so jitter rotation resumes the instant the camera moves.
        float moved = haveLastCam ? (cameraPos - lastCamPos).Length() : 1e9f;
        lastCamPos = cameraPos; haveLastCam = true;
        if (moved < 0.02f) staticFrames++;
        else { staticFrames = 0; jitterSeed = frameCounter; }   // moving → keep advancing the seed with the frame

        // CHUNK 1 progressive bake wave: capture the camera for the GPU band test, and ripple the open-band
        // frontier outward (one band every BandFrames frames) until the whole grid is reachable. When the bake
        // has run long enough for the furthest band to converge, latch bakeFrozen → the renderer stops tracing.
        if (BakedMode && !bakeFrozen) {
            bakeCamPos = cameraPos;
            bakeFrameCounter++;
            if (++bakeWaveFrames >= BandFrames) { bakeWaveFrames = 0; if (bakeWave < MaxBand) bakeWave++; }
            if (bakeFrameCounter >= BakeCompleteFrameEstimate) bakeFrozen = true;
        }
    }

    // Params1 = (maxRayDist, normalBias, feedbackEnable, intensity). feedbackEnable>0.5 → the trace reads
    // last frame's irradiance FIELD at each hit (P2.3 multi-bounce); 0 → flat IBL-cube ambient (1-bounce).
    // Params2 = round-robin (updateFraction N, phase = frameIndex % N, fullUpdate flag). fullUpdate=1 → every
    // probe traces/blends this dispatch (warm-up + UpdateFraction==1); else only probes with probe%N==phase.
    public DdgiConstants Constants(int frameIndex, float hysteresis, float intensity, bool feedback, bool fullUpdate) {
        int n = UpdateFraction;
        // STABILITY: while the camera is static, FULL-update every frame with a FROZEN jitter seed. Full-update
        // kills the round-robin's per-frame subset churn; the frozen seed (jitterSeed stops advancing in Update)
        // stops the ray dirs rotating, so each probe converges to ONE estimate and the EMA settles to a steady
        // value instead of boiling. Combined with a higher static hysteresis (set by the caller), the field
        // visibly STOPS changing a fraction of a second after the camera stops — the Lumen "converge then hold".
        bool staticNow = IsStatic;
        bool full = fullUpdate || n <= 1 || staticNow;
        int phase = full ? 0 : ((frameIndex % n) + n) % n;
        int jit = staticNow ? jitterSeed : frameIndex;   // frozen seed while static → no ray-rotation churn
        return new() {
            OriginSpacingX = new Vector4(Origin, Spacing.X),
            SpacingYZ = new Vector4(Spacing.Y, Spacing.Z, 0, 0),
            ProbeDims = new Vector4(ProbesX, ProbesY, ProbesZ, ProbeCount),
            Params0 = new Vector4(IrradianceTexels, DepthTexels, hysteresis, jit),
            Params1 = new Vector4(40f, 0.25f, feedback ? 1f : 0f, intensity),
            // Params2.w = emissiveEnable (was pad) — the trace adds emissive self-emission at hits when >0.5.
            Params2 = new Vector4(full ? 1 : n, phase, full ? 1f : 0f, EmissiveEnabled ? 1f : 0f),
            // CHUNK 1 progressive bake: camera pos + band width feed the GPU's per-probe distance-band test; the
            // bake params (enable, open-band frontier, converge target) tell the trace/blend/classify which probes
            // are eligible THIS frame. bakeEnable is 0 in the live path → the band test is bypassed (Params2 round-
            // robin governs), so the non-baked render is byte-identical.
            Params3 = new Vector4(bakeCamPos, BandWidth),
            Params4 = new Vector4(BakedMode ? 1f : 0f, bakeWave, ConvergeTarget, 0f),
        };
    }

    // World position of probe (px,py,pz) — for the debug gizmo + the update pass.
    public Vector3 ProbePosition(int px, int py, int pz) =>
        Origin + new Vector3(px * Spacing.X, py * Spacing.Y, pz * Spacing.Z);

    // P2.5 budget readout: how many probes trace per frame at the current round-robin setting, and the GRID's
    // persistent VRAM footprint (atlases + RayData + ProbeState) — checked FIRST per the plan (VRAM-budget the
    // grid before cutting cost), so the GTX-1660's smaller VRAM is never blown. RayData is sized for the FULL
    // grid (round-robin reuses its slots), so it doesn't shrink with UpdateFraction — but it's small (~4.7MB).
    public int CurrentUpdateFraction => UpdateFraction;
    public int ProbesPerFrame => Math.Max(1, ProbeCount / Math.Max(1, UpdateFraction));
    public long GridVramBytes {
        get {
            long irr = (long)IrradianceAtlasW * IrradianceAtlasH * 8;   // RGBA16F
            long dep = (long)DepthAtlasW * DepthAtlasH * 4;            // RG16F
            long ray = (long)ProbeCount * RaysPerProbe * 16;          // float4
            long st = (long)ProbeCount * 16;                          // float4 ProbeState
            return irr + dep + ray + st;
        }
    }

    // P2.5 capture-path determinism. WarmupEnabled (BALLISTIC_SCREENSHOT set, or _WARMUP=N) means we converge
    // the field once and then FREEZE it — the per-frame round-robin update is skipped so the captured image is
    // byte-IDENTICAL regardless of SCREENSHOT_FRAME (each render frame would otherwise nudge the field by one
    // more round-robin step → frame-dependent). In play this is false (the field updates live every frame).
    public bool WarmupEnabled => WarmupIterations > 0;

    // Warm-up driver: on the FIRST DDGI frame, replay the FULL-grid update WarmupIterations times (each its own
    // submit — never one giant command list, TDR watchdog) so the EMA field converges within one rendered frame.
    // Returns true if warm-up ran (this call only). Idempotent: subsequent calls return false.
    public bool TryWarmUp(Action runOneFullUpdate) {
        if (warmedUp) return false;
        warmedUp = true;
        int iters = WarmupIterations;
        if (iters <= 0) return false;
        for (int i = 0; i < iters; i++) runOneFullUpdate();
        return true;
    }

    // Build the trace + blend compute PSOs and the RayData buffer (once). The atlas UAVs are registered into
    // blendHeap here too (persistent atlases → persistent descriptors). Idempotent.
    public unsafe void Build() {
        if (built) return;
        built = true;
        EnsureAllocated();

        // RayData UAV buffer (DEFAULT heap, AllowUnorderedAccess), zero-seeded.
        var zero = new Vector4[ProbeCount * RaysPerProbe];
        rayData = dev.CreateUavBuffer<Vector4>(zero, ResourceStates.UnorderedAccess);

        // CHUNK1 ProbeBakeState: per-probe converged-frame counter (uint), zero-seeded. Trace bumps (u1), blend
        // reads (t1). Re-zeroed on Rebake via a CopyBufferRegion from the persistent zero upload below.
        var bakeZero = new uint[ProbeCount];
        probeBakeState = dev.CreateUavBuffer<uint>(bakeZero, ResourceStates.UnorderedAccess);
        // Persistent zero-filled upload buffer (~8KB) so Rebake() can reset the counters with one copy (no per-
        // rebake allocation). Upload heap, mapped once + zeroed.
        bakeZeroUpload = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)ProbeCount * sizeof(uint)), ResourceStates.GenericRead);
        unsafe { byte* zp = bakeZeroUpload.Map<byte>(0); for (int i = 0; i < ProbeCount * sizeof(uint); i++) zp[i] = 0; bakeZeroUpload.Unmap(0); }

        // --- TRACE root sig (mirrors DxrGi). The TLAS + irr cube + prev-irradiance-atlas are NON-CONTIGUOUS
        // registers (t0,t3,t4) so the table holds THREE ranges, written to ADJACENT bindless-tail slots so one
        // GPU base handle covers all (range 0→slot+0=t0 TLAS, 1→slot+1=t3 cube, 2→slot+2=t4 prev irr atlas).
        // t4 = LAST frame's irradiance atlas for the P2.3 multi-bounce feedback. ---
        var t_cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var t_cbv1 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var tlasRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0,  // t0
            registerSpace: 0, offsetInDescriptorsFromTableStart: 0);
        var cubeRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 3,  // t3
            registerSpace: 0, offsetInDescriptorsFromTableStart: 1);
        var prevIrrRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 4,  // t4
            registerSpace: 0, offsetInDescriptorsFromTableStart: 2);
        var prevDepthRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 11,  // t11 prev-depth (Chebyshev leak gate)
            registerSpace: 0, offsetInDescriptorsFromTableStart: 3);
        var t_table = new RootParameter1(new RootDescriptorTable1(tlasRange, cubeRange, prevIrrRange, prevDepthRange), ShaderVisibility.All);
        var t_mat = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(5, 0), ShaderVisibility.All);
        var t_inst = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(6, 0), ShaderVisibility.All);
        var t_light = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All);
        var t_probe = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(8, 0), ShaderVisibility.All);  // t8 ProbeState (P2.4)
        var t_uav = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All);  // u0 RayData
        var t_bake = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0), ShaderVisibility.All);  // u1 ProbeBakeState (CHUNK1)
        var clampSamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var wrapSamp = new StaticSamplerDescription(ShaderVisibility.All, 1, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        traceRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { t_cbv0, t_cbv1, t_table, t_mat, t_inst, t_light, t_probe, t_uav, t_bake },
                new[] { clampSamp, wrapSamp })));

        string traceHlsl = EmbeddedShaderSource.ReadHlsl("DdgiTrace.hlsl");
        byte[] traceCs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, traceHlsl, "CSMain", "DdgiTrace.hlsl");
        tracePso = dev.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription { RootSignature = traceRootSig, ComputeShader = traceCs });

        // --- BLEND root sig: CBV b0, root SRV t0 RayData, table covering 3 UAVs (u0 irr + u1 depth + u2
        // ProbeState) so ONE root sig serves all blend-family entry points. Table base = blendHeap slot 0, so
        // u0→heap[0]=irr, u1→heap[1]=depth, u2→heap[2]=ProbeState; each shader writes only its own register
        // (CSIrradiance u0, CSDepth u1, CSClassify u2; CSBorder* read+write u0/u1). ---
        var b_cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var b_srv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);  // t0 RayData
        var b_bake = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All); // t1 ProbeBakeState (CHUNK1)
        var b_uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 3, baseShaderRegister: 0);  // u0 irr + u1 depth + u2 ProbeState
        var b_table = new RootParameter1(new RootDescriptorTable1(b_uavRange), ShaderVisibility.All);
        blendRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { b_cbv, b_srv, b_bake, b_table })));

        string blendHlsl = EmbeddedShaderSource.ReadHlsl("DdgiBlend.hlsl");
        byte[] irrCs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, blendHlsl, "CSIrradiance", "DdgiBlend.hlsl");
        byte[] depCs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, blendHlsl, "CSDepth", "DdgiBlend.hlsl");
        byte[] borIrrCs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, blendHlsl, "CSBorderIrr", "DdgiBlend.hlsl");
        byte[] borDepCs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, blendHlsl, "CSBorderDepth", "DdgiBlend.hlsl");
        byte[] classifyCs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, blendHlsl, "CSClassify", "DdgiBlend.hlsl");
        blendIrrPso = dev.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription { RootSignature = blendRootSig, ComputeShader = irrCs });
        blendDepthPso = dev.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription { RootSignature = blendRootSig, ComputeShader = depCs });
        borderIrrPso = dev.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription { RootSignature = blendRootSig, ComputeShader = borIrrCs });
        borderDepthPso = dev.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription { RootSignature = blendRootSig, ComputeShader = borDepCs });
        classifyPso = dev.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription { RootSignature = blendRootSig, ComputeShader = classifyCs });

        // blendHeap: 2 persistent UAV descriptors for the two atlases (irradiance@slot0 = u0, depth@slot1 = u1)
        // laid out CONTIGUOUSLY so the blend root sig's 2-descriptor table (base = slot 0) maps u0→irr,
        // u1→depth for BOTH entry points. CSIrradiance writes only u0; CSDepth writes only u1.
        blendHeap = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3, shaderVisible: true);
        dev.Device.CreateUnorderedAccessView(irradianceTex, null, new UnorderedAccessViewDescription {
            Format = Format.R16G16B16A16_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, blendHeap.Cpu(0));
        dev.Device.CreateUnorderedAccessView(depthTex, null, new UnorderedAccessViewDescription {
            Format = Format.R16G16_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, blendHeap.Cpu(1));
        // P2.4 ProbeState UAV (u2, blendHeap slot 2): ProbeCount float4 {offset.xyz, active}, seeded active=1
        // so all probes light correctly BEFORE the first classify pass runs.
        var probeSeed = new Vector4[ProbeCount];
        for (int i = 0; i < ProbeCount; i++) probeSeed[i] = new Vector4(0, 0, 0, 1);
        probeState = dev.CreateUavBuffer<Vector4>(probeSeed, ResourceStates.UnorderedAccess);
        dev.Device.CreateUnorderedAccessView(probeState, null, new UnorderedAccessViewDescription {
            Format = Format.Unknown, ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = ProbeCount, StructureByteStride = 16 },
        }, blendHeap.Cpu(2));

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<DdgiConstants>() + 255) & ~255;
        constCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        constCbMapped = constCb.Map<byte>(0);

        // --- P2.2/P2.4 GATHER root sig: CBV b0 (grid) + CBV b1 (extra) + table {t0..t5 SRV, u0 UAV} +
        // linear-clamp. t5 = ProbeState (P2.4 relocation offset + active flag). ---
        var g_cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var g_cbv1 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var g_srv = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 6, baseShaderRegister: 0);   // t0..t5
        var g_uav = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        var g_table = new RootParameter1(new RootDescriptorTable1(g_srv, g_uav), ShaderVisibility.All);
        var g_samp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        gatherRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { g_cbv0, g_cbv1, g_table }, new[] { g_samp })));
        string gatherHlsl = EmbeddedShaderSource.ReadHlsl("DdgiGather.hlsl");
        byte[] gatherCs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, gatherHlsl, "CSGather", "DdgiGather.hlsl");
        gatherPso = dev.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription { RootSignature = gatherRootSig, ComputeShader = gatherCs });
        gatherHeap = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 7, shaderVisible: true);  // 6 SRV + 1 UAV
        int gcbSize = (System.Runtime.InteropServices.Marshal.SizeOf<DdgiGatherExtra>() + 255) & ~255;
        gatherCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)gcbSize), ResourceStates.GenericRead);
        gatherCbMapped = gatherCb.Map<byte>(0);
    }

    // Run the probe update: TRACE (rays/probe → RayData) then BLEND (RayData → irradiance + depth atlases).
    // Must be called from inside DrawRtGi AFTER EnsureMaterialTable + rtGeometry.Ensure (bindless ids fresh)
    // and AFTER the RtGi reserved-tail descriptors are written, with the SAME bindless heap bound. The caller
    // supplies the shared root-SRV addresses (materials/instances/lights), the irradiance cube SRV, and the
    // RtGiSun CBV address. `traceTableGpu` is the GPU handle of the 3-descriptor bindless-tail base ([0]=TLAS,
    // [1]=irr cube, [2]=prev irradiance atlas) the caller has already written; the trace table root param
    // points there. The atlases stay UnorderedAccess on exit (caller transitions to SRV for the gather — P2.2).
    // P2.3: when `feedback` is true the trace reads the irradiance atlas as SRV t4 (last frame's field) for
    // multi-bounce — so it's transitioned UnorderedAccess→NonPixelShaderResource for the trace, then back to
    // UnorderedAccess for the blend write within this same command list.
    public unsafe void DispatchDdgi(ID3D12GraphicsCommandList4 cl,
        Dx12DescriptorHeap bindless, GpuDescriptorHandle traceTableGpu,
        ulong sunCbAddress, ulong materialsAddr, ulong instancesAddr, ulong lightsAddr,
        float hysteresis, float intensity, bool feedback, bool fullUpdate = false) {
        // Frame 0 has no field yet → force the 1-bounce IBL ambient regardless of the requested feedback.
        bool fb = feedback && frameCounter > 0;
        *(DdgiConstants*)constCbMapped = Constants(frameCounter, hysteresis, intensity, fb, fullUpdate);
        frameCounter++;

        // CHUNK1: zero the per-probe converged counters at the START of a (re)bake run, so the wave restarts from
        // band 0 with every probe un-converged. One small CopyBufferRegion from the persistent zero upload.
        if (BakedMode && !bakeProbeStateCleared) {
            bakeProbeStateCleared = true;
            cl.ResourceBarrierTransition(probeBakeState, ResourceStates.UnorderedAccess, ResourceStates.CopyDest);
            cl.CopyBufferRegion(probeBakeState, 0, bakeZeroUpload, 0, (ulong)ProbeCount * sizeof(uint));
            cl.ResourceBarrierTransition(probeBakeState, ResourceStates.CopyDest, ResourceStates.UnorderedAccess);
        }

        // P2.3 multi-bounce: the trace reads the irradiance atlas (t4) → SRV state for the trace dispatch.
        // The leak gate also reads the DEPTH-moments atlas (t11) → same SRV transition (both feed the field read).
        if (fb)
        {
            cl.ResourceBarrierTransition(irradianceTex, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            cl.ResourceBarrierTransition(depthTex, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        }
        // P2.4: the trace reads ProbeState (t8, last frame's classification) as a root SRV → NonPixelSRV state;
        // the classify pass writes it back as a UAV at the end of this command list.
        cl.ResourceBarrierTransition(probeState, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);

        // --- TRACE: ProbeCount*RaysPerProbe threads, 64/group. RayData UAV starts in UnorderedAccess. ---
        cl.SetComputeRootSignature(traceRootSig);
        cl.SetPipelineState(tracePso);
        cl.SetComputeRootConstantBufferView(0, constCb.GPUVirtualAddress);  // b0 DdgiConstants
        cl.SetComputeRootConstantBufferView(1, sunCbAddress);              // b1 RtGiSun
        cl.SetComputeRootDescriptorTable(2, traceTableGpu);               // t0 TLAS + t3 irr cube + t4 prev irr atlas
        cl.SetComputeRootShaderResourceView(3, materialsAddr);            // t5 GpuMaterials
        cl.SetComputeRootShaderResourceView(4, instancesAddr);           // t6 RtInstance[]
        cl.SetComputeRootShaderResourceView(5, lightsAddr);             // t7 Lights
        cl.SetComputeRootShaderResourceView(6, probeState.GPUVirtualAddress);  // t8 ProbeState (P2.4)
        cl.SetComputeRootUnorderedAccessView(7, rayData.GPUVirtualAddress);  // u0 RayData
        cl.SetComputeRootUnorderedAccessView(8, probeBakeState.GPUVirtualAddress);  // u1 ProbeBakeState (CHUNK1)
        int totalThreads = ProbeCount * RaysPerProbe;
        cl.Dispatch((uint)((totalThreads + 63) / 64), 1, 1);

        // Trace done reading the irradiance + depth atlases → back to UnorderedAccess for the blend write below.
        if (fb)
        {
            cl.ResourceBarrierTransition(irradianceTex, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            cl.ResourceBarrierTransition(depthTex, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        }
        // Trace done reading ProbeState → back to UnorderedAccess for the classify write at the end.
        cl.ResourceBarrierTransition(probeState, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);

        // RayData write → read barrier before blend.
        cl.ResourceBarrierUnorderedAccessView(rayData);
        // CHUNK1: the trace bumped ProbeBakeState (u1); the blend reads it (t1) → UAV write must complete first.
        cl.ResourceBarrierUnorderedAccessView(probeBakeState);

        // --- BLEND: switch heaps to blendHeap (its own shader-visible heap). RayData must be readable as a
        // root SRV (t0): it was created in UnorderedAccess — a root SRV reads it fine in any GenericRead-
        // compatible state, but to be correct transition it to NonPixelShaderResource for the read, then back.
        cl.ResourceBarrierTransition(rayData, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        cl.ResourceBarrierTransition(probeBakeState, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        cl.SetDescriptorHeaps(blendHeap.Heap);
        cl.SetComputeRootSignature(blendRootSig);
        cl.SetComputeRootConstantBufferView(0, constCb.GPUVirtualAddress);   // b0
        cl.SetComputeRootShaderResourceView(1, rayData.GPUVirtualAddress);   // t0 RayData
        cl.SetComputeRootShaderResourceView(2, probeBakeState.GPUVirtualAddress);  // t1 ProbeBakeState (CHUNK1)

        // The 3-UAV table base (slot 0): u0→irr, u1→depth, u2→ProbeState. Each shader writes only its own
        // register. (Param index 3 now — t1 ProbeBake was inserted as param 2.)
        cl.SetComputeRootDescriptorTable(3, blendHeap.Gpu(0));

        cl.SetPipelineState(blendIrrPso);
        cl.Dispatch((uint)((IrradianceAtlasW + 7) / 8), (uint)((IrradianceAtlasH + 7) / 8), 1);

        cl.SetPipelineState(blendDepthPso);
        cl.Dispatch((uint)((DepthAtlasW + 7) / 8), (uint)((DepthAtlasH + 7) / 8), 1);

        // Interior must be done before the border copies READ it → UAV barrier between blend and border.
        cl.ResourceBarrierUnorderedAccessView(irradianceTex);
        cl.ResourceBarrierUnorderedAccessView(depthTex);

        // --- P2.2 octahedral BORDER-WRAP: replicate edge texels so the gather's bilinear sampling wraps. Same
        // root sig + heap as blend (each border shader reads+writes only its own atlas UAV). ---
        cl.SetPipelineState(borderIrrPso);
        cl.Dispatch((uint)((IrradianceAtlasW + 7) / 8), (uint)((IrradianceAtlasH + 7) / 8), 1);
        cl.SetPipelineState(borderDepthPso);
        cl.Dispatch((uint)((DepthAtlasW + 7) / 8), (uint)((DepthAtlasH + 7) / 8), 1);

        cl.ResourceBarrierUnorderedAccessView(irradianceTex);
        cl.ResourceBarrierUnorderedAccessView(depthTex);

        // --- P2.4 CLASSIFY (1 thread/probe): reduce RayData → ProbeState (active + relocation offset). Same
        // blend root sig + heap (t0 RayData still NonPixelSRV, u2 ProbeState in UnorderedAccess). The 3-UAV
        // table is already bound at blendHeap.Gpu(0); CSClassify writes only u2. ---
        cl.SetPipelineState(classifyPso);
        cl.Dispatch((uint)((ProbeCount + 63) / 64), 1, 1);
        cl.ResourceBarrierUnorderedAccessView(probeState);

        // Restore the bindless heap for whatever the caller does next (it bound bindless before us).
        cl.SetDescriptorHeaps(bindless.Heap);
        cl.ResourceBarrierTransition(rayData, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        cl.ResourceBarrierTransition(probeBakeState, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
    }

    // P2.2 GATHER (own ExecuteSync, called by DrawRtGi when DDGI is on). Reads the G-buffer (depth/normal/
    // albedo SRVs supplied by the caller) + the two probe atlases, writes albedo*E pre-exposed into ssgiTarget
    // (the caller then runs SsgiResolveAndCombine). The atlases are in UnorderedAccess on entry (left so by
    // DispatchDdgi) → transitioned to NonPixelShaderResource for the SRV read here, then back to UnorderedAccess
    // for next frame's blend. ssgiTarget must be in UnorderedAccess on entry (caller's ColorToUnorderedAccess).
    public unsafe void DispatchGather(ID3D12GraphicsCommandList4 cl,
        CpuDescriptorHandle depthSrv, CpuDescriptorHandle normalSrv, CpuDescriptorHandle albedoSrv,
        ID3D12Resource ssgiTargetRes, int screenW, int screenH,
        Matrix4x4 invViewProjTransposed, float preExposure) {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        *(DdgiGatherExtra*)gatherCbMapped = new DdgiGatherExtra {
            InvViewProj = invViewProjTransposed,
            GParams = new Vector4(preExposure, screenW, screenH, 0f),
        };
        // Build the gather heap: t0 depth, t1 normal, t2 albedo, t3 irrAtlas, t4 depthAtlas, t5 ProbeState,
        // u0 ssgiTarget (slot 6).
        gatherHeap.Reset();
        dev.Device.CopyDescriptorsSimple(1, gatherHeap.Cpu(0), depthSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, gatherHeap.Cpu(1), normalSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, gatherHeap.Cpu(2), albedoSrv, heapType);
        dev.Device.CreateShaderResourceView(irradianceTex, new ShaderResourceViewDescription {
            Format = Format.R16G16B16A16_Float, ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, gatherHeap.Cpu(3));
        dev.Device.CreateShaderResourceView(depthTex, new ShaderResourceViewDescription {
            Format = Format.R16G16_Float, ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, gatherHeap.Cpu(4));
        dev.Device.CreateShaderResourceView(probeState, new ShaderResourceViewDescription {   // t5 ProbeState
            Format = Format.Unknown, ViewDimension = ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = ProbeCount, StructureByteStride = 16 },
        }, gatherHeap.Cpu(5));
        dev.Device.CreateUnorderedAccessView(ssgiTargetRes, null, new UnorderedAccessViewDescription {
            Format = Format.R16G16B16A16_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, gatherHeap.Cpu(6));

        cl.ResourceBarrierTransition(irradianceTex, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        cl.ResourceBarrierTransition(depthTex, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        cl.ResourceBarrierTransition(probeState, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        cl.SetDescriptorHeaps(gatherHeap.Heap);
        cl.SetComputeRootSignature(gatherRootSig);
        cl.SetPipelineState(gatherPso);
        cl.SetComputeRootConstantBufferView(0, constCb.GPUVirtualAddress);    // b0 grid (filled by DispatchDdgi this frame)
        cl.SetComputeRootConstantBufferView(1, gatherCb.GPUVirtualAddress);   // b1 extra
        cl.SetComputeRootDescriptorTable(2, gatherHeap.Gpu(0));
        cl.Dispatch((uint)((screenW + 7) / 8), (uint)((screenH + 7) / 8), 1);
        cl.ResourceBarrierTransition(irradianceTex, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        cl.ResourceBarrierTransition(depthTex, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        cl.ResourceBarrierTransition(probeState, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
    }

    // DEBUG (BALLISTIC_DX12_DDGI_DEBUG=1): read the irradiance atlas back to the CPU and report min/max/mean +
    // non-zero fraction, so we can confirm the probe-update pipeline produced sensible, non-zero, smooth data
    // (the P2.1 success gate) WITHOUT a gather pass yet. CPU-side readback, called once after Dispatch; not in
    // the hot path. The atlas is left in UnorderedAccess by DispatchDdgi → transition to CopySource here + back.
    // PROBE-COLOUR READBACK for the editor gizmo (ShowProbeSpheres). Copies the irradiance atlas to the CPU,
    // averages each probe's 6x6 interior octahedral tile to ONE mean RGB, and publishes it to GiDebugGrid so the
    // gizmo can tint each probe sphere with the real bounce colour it caches. Called at a THROTTLED cadence (a
    // few Hz — see the renderer gate) because a texture readback is a full GPU sync; not per-frame. The atlas is
    // in UnorderedAccess (left by DispatchDdgi); transitioned to CopySource and back. No-op until the grid is
    // allocated. Probe flat index = (pz*ProbesY+py)*ProbesX+px (the trace/blend flattening); the atlas tile for a
    // probe is at (col,row) = (pz*ProbesX+px, py), interior origin (col*IrrTile+Border, row*IrrTile+Border).
    public unsafe void ReadbackProbeColors() {
        if (!built || irradianceTex == null) return;
        int w = IrradianceAtlasW, h = IrradianceAtlasH;
        const int bpp = 8;   // RGBA16F
        var footprints = new PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1]; var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(irradianceTex.Description, 0, 1, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);
        PlacedSubresourceFootPrint fp = footprints[0];
        int rowPitch = (int)fp.Footprint.RowPitch;
        var rb = dev.Device.CreateCommittedResource(HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);
        // ExecuteSyncImmediate: the readback must observe the blend writes finished THIS frame (a recorded-only
        // copy under the pipelined frame would read the atlas mid-frame / before the blend). Throttled, so the
        // sync cost is paid only a few times a second.
        dev.ExecuteSyncImmediate(cl => {
            cl.ResourceBarrierTransition(irradianceTex, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            cl.CopyTextureRegion(new TextureCopyLocation(rb, fp), 0, 0, 0,
                new TextureCopyLocation(irradianceTex, 0), null);
            cl.ResourceBarrierTransition(irradianceTex, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
        });
        byte* p = rb.Map<byte>(0);
        var colors = new Vector3[ProbeCount];
        for (int pz = 0; pz < ProbesZ; pz++)
        for (int py = 0; py < ProbesY; py++)
        for (int px = 0; px < ProbesX; px++) {
            int col = pz * ProbesX + px, row = py;
            int ox = col * IrrTile + Border, oy = row * IrrTile + Border;   // interior origin (skip border)
            float r = 0, g = 0, b = 0; int n = 0;
            for (int ty = 0; ty < IrradianceTexels; ty++) {
                byte* line = p + (long)(oy + ty) * rowPitch + (long)ox * bpp;
                for (int tx = 0; tx < IrradianceTexels; tx++) {
                    Half* texel = (Half*)(line + tx * bpp);
                    float vr = (float)texel[0], vg = (float)texel[1], vb = (float)texel[2];
                    if (float.IsNaN(vr) || float.IsInfinity(vr)) continue;
                    r += vr; g += vg; b += vb; n++;
                }
            }
            if (n > 0) { float inv = 1f / n; colors[(pz * ProbesY + py) * ProbesX + px] = new Vector3(r * inv, g * inv, b * inv); }
        }
        rb.Unmap(0); rb.Dispose();
        GiDebugGrid.PublishProbeColors(colors);
    }

    public unsafe void DumpIrradianceStats() {
        if (!built || irradianceTex == null) { Console.WriteLine("[DDGI-DBG] not built"); return; }
        int w = IrradianceAtlasW, h = IrradianceAtlasH;
        const int bpp = 8;   // RGBA16F = 8 bytes/texel
        // Placed footprint of subresource 0 (D3D12 fills the 256-byte-aligned row pitch).
        var footprints = new PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1]; var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(irradianceTex.Description, 0, 1, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);
        PlacedSubresourceFootPrint fp = footprints[0];
        int rowPitch = (int)fp.Footprint.RowPitch;
        var rb = dev.Device.CreateCommittedResource(HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);
        dev.ExecuteSync(cl => {
            cl.ResourceBarrierTransition(irradianceTex, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            cl.CopyTextureRegion(new TextureCopyLocation(rb, fp), 0, 0, 0,
                new TextureCopyLocation(irradianceTex, 0), null);
            cl.ResourceBarrierTransition(irradianceTex, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
        });
        byte* p = rb.Map<byte>(0);
        double sum = 0; float mn = float.MaxValue, mx = float.MinValue; long nonzero = 0, total = 0;
        for (int y = 0; y < h; y++) {
            byte* row = p + (long)y * rowPitch;
            for (int x = 0; x < w; x++) {
                Half* px = (Half*)(row + x * bpp);
                for (int c = 0; c < 3; c++) {   // RGB only (A is the blend's written-flag)
                    float v = (float)px[c];
                    if (float.IsNaN(v) || float.IsInfinity(v)) { Console.WriteLine($"[DDGI-DBG] NaN/Inf at ({x},{y}) ch{c}"); continue; }
                    sum += v; if (v < mn) mn = v; if (v > mx) mx = v; if (v > 1e-6f) nonzero++; total++;
                }
            }
        }
        rb.Unmap(0); rb.Dispose();
        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[DDGI-DBG] irradiance atlas {w}x{h}: mean={sum / Math.Max(total, 1):0.000000} min={mn:0.000000} max={mx:0.000000} nonzero={100.0 * nonzero / Math.Max(total, 1):0.0}% ({nonzero}/{total} RGB samples)"));
    }

    ID3D12Resource CreateAtlas(int w, int h, Format fmt) {
        var desc = ResourceDescription.Texture2D(fmt, (uint)w, (uint)h, 1, 1);
        desc.Flags = ResourceFlags.AllowUnorderedAccess;
        return dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.UnorderedAccess);
    }

    public void Dispose() {
        irradianceTex?.Dispose(); irradianceTex = null;
        depthTex?.Dispose(); depthTex = null;
        rayData?.Dispose(); rayData = null;
        probeBakeState?.Dispose(); probeBakeState = null;
        if (bakeZeroUpload != null) { bakeZeroUpload.Dispose(); bakeZeroUpload = null; }
        tracePso?.Dispose(); tracePso = null;
        traceRootSig?.Dispose(); traceRootSig = null;
        blendIrrPso?.Dispose(); blendIrrPso = null;
        blendDepthPso?.Dispose(); blendDepthPso = null;
        borderIrrPso?.Dispose(); borderIrrPso = null;
        borderDepthPso?.Dispose(); borderDepthPso = null;
        classifyPso?.Dispose(); classifyPso = null;
        probeState?.Dispose(); probeState = null;
        blendRootSig?.Dispose(); blendRootSig = null;
        blendHeap?.Dispose(); blendHeap = null;
        gatherPso?.Dispose(); gatherPso = null;
        gatherRootSig?.Dispose(); gatherRootSig = null;
        gatherHeap?.Dispose(); gatherHeap = null;
        if (gatherCb != null) { gatherCb.Unmap(0); gatherCb.Dispose(); gatherCb = null; }
        if (constCb != null) { constCb.Unmap(0); constCb.Dispose(); constCb = null; }
        built = false;
    }
}
