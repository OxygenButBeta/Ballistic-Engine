# GI Pragmatic Revival — Faz R0 baseline (re-enable + paydalı ölçüm)

**Tarih:** 2026-06-18. **Branch:** `dx12-renderer`. **Commit (re-enable):** bu doküman ile aynı commit.
**Plan:** [gi-pragmatic-revival-plan.md](gi-pragmatic-revival-plan.md) §2 Faz R0.
**Substrat:** RX 9070 XT (dev kart), 1920×1080 native (headless), `BALLISTIC_DETERMINISTIC` + paused.

> Ham çıktılar: `e:/tmp/gi-baseline/run1/` (perf_*.json GI on/off, images/*.bmp comp/bounce, determinism/*.bmp).

---

## R0.1 — Re-enable (yapıldı)

Diffuse-GI choke point `DX12HDRenderer.cs:~1646` geri açıldı: `GiMode giMode = GiMode.Off;` →
canlı env/PostFX resolve'a döndürüldü (`rtgiEnv==1?RayTraced : ssgiEnv==1?ScreenSpace : ssgiEnv==0?Off :
doors.Minimal?Off : PostFX.GiMode`). `Dx12GiPass.Enabled()` (= `ctx.GiMode != Off`) yeniden true dönebiliyor.

- **SADECE diffuse GI.** SPECULAR reflections (SSR + RT-reflections) kullanıcının ayrı WIP'i tarafından
  KAPALI bırakıldı (`PostProcessSettings.SsrEnabled=false` + `VolumePostProcessing` reflections-bridge Off).
  Reflections R2 preset'inde ele alınacak, burada DEĞİL. Kullanıcının reflections WIP'ine dokunulmadı.
- **Build:** DX12 + Runtime + Cli 0-err (alt-output ile doğrulandı; canlı editör PID 3384 Editor/bin DLL'lerini
  kilitliyor — bu compile hatası değil, dosya-kilidi).

## R0.0b — Test sahneleri (mevcut, hepsi EXIT=0 render etti)

| # | Senaryo | Sahne | Not |
|---|---|---|---|
| 1 | İç mekan (real content) | `Bistro_v5_2/BistroInterior_Wine.scene` | GI güçlü katkı veriyor |
| 2 | Dış mekan / vista | `Bistro_v5_2/BistroExterior.scene` | açık gökyüzü, en ağır tri sayısı |
| 3 | Tapınak iç | `Temple/.../SunTemple.scene` | **PRE-EXISTING: headless çok karanlık** (sun underground), bounce~0 |
| 4 | Çok-ışık | `LightTest/LightTest.scene` | **PRE-EXISTING: near-black**, bounce~0 |
| 5 | Cornell (klasik GI) | `CornellBox/CornellBox.scene` | color-bleed net görünüyor |

R0.0b'nin istediği "whole-mesh color-only emissive" (sahne-3) ve "thin-wall leak" (sahne-4) FIXTURE'ları
HENÜZ özel olarak üretilmedi — CornellBox kısmen leak/color-bleed'i, mevcut sahneler whole-mesh'i karşılıyor.
Bu fixture'lar R1.0 (MaterialId bug repro) için ayrıca üretilecek (plan R0.0b DoD'si açık kalemdir).

## R0.2 — Payda (frame budget denominator)

> ⚠ **SUPERSEDED (2026-06-18).** Bu R0.2 bölümü SIRA-DIŞI koşan önceki worker'ın notuydu (gerçek R0
> baseline'ından ÖNCE). Doğru/güncel R0.2 ölçümü artık **`gi-pragmatic-revival-plan.md` → "R0.2 measured"
> bloğunda**: (a) "TSR/FSR 4K Lumen model" satırı GEVŞEKTİ — motor SADECE **FSR** taşıyor (TSR YOK; `UpscaleMode`
> enum + `Upscaling` volume + `Dx12FsrPass`), (b) bu doc'un GTX-1660 ekstrapolasyonu rev3+ min-hedefiyle ÇELİŞİR
> (hedef **RTX 2060**, 1660 değil). Aşağıdaki orijinal satırlar kayıt için bırakıldı; KARAR plan'daki blok.

- **Render çözünürlüğü:** 1920×1080 native (headless `Dx12HeadlessRuntime` default). ~~Hedef model: 1080p iç-render
  → (ileride) TSR/FSR 4K, Lumen modeli.~~ → **FSR (TSR değil)**; bkz plan R0.2 measured (1).
- **Frame bütçesi:** 60fps = **16.6 ms** / 30fps = **33 ms**.
- **GI'ya kalan = 16.6 − (direkt ışık + gölge + post).** RX 9070 XT'de bu pay BÜYÜK (non-GI cpuFrame ~3.3–4.7ms GI-off,
  yeniden ölçüldü 2026-06-18 CornellBox/LightTest/SunTemple) — dev kartta sığma sorunu yok. **Asıl payda kısıtı
  hedef-GPU'da (RTX 2060, GTX-1660 DEĞİL)** — plan R0.2 measured (4): modeled 2060'ta ağır yol için **FSR ZORUNLU**,
  60fps@1080p-native CREDIBLE DEĞİL; hedef 30fps@native veya 60fps@FSR.

## R0.3 — baseline (GI-isolate A/B, RX 9070 XT, frame 60, **SHIPPING path** = volume-on)

> ⚠ **STALE TABLE BELOW (the original 5-row block) — SUPERSEDED 2026-06-18 by the RE-MEASURED table that
> follows.** The original was captured by the OUT-OF-ORDER prior worker and (per PROVISIONAL POLICY) was
> re-measured against the working tree at commit `928d3fe2`. Two material corrections: **(a)** the original
> claimed "Bistro gitignored, captured by a worker who had Bistro" while R0.2 later claimed Bistro was
> MISSING locally — **both reconciled here: Bistro IS present locally** (115M Exterior FBX / 47M Interior /
> 1.4G Textures, gitignored but on disk) so it WAS re-measured. **(b)** The original captured on the
> mixed env-door path; R0.3's DoD mandates the **SHIPPING path (volume-on, no SSGI env door = `PostFX.GiMode`
> = ScreenSpace default from R0.1)**. The scenario set was also corrected to the canonical 5 GiFixtures
> (Outdoor/MultiLightInterior/ColorOnly/ThinWall/MovingLight) + CornellBox + Bistro Interior/Exterior.

<details><summary>ORIGINAL STALE TABLE (kept for the record — do NOT use)</summary>

| Senaryo | GI pass ms | cpuFrame on | cpuFrame off | comp lum on | comp lum off | bounce lum | tris | draws |
|---|---|---|---|---|---|---|---|---|
| SunTemple | 3.95 | 6.82 | 3.23 | 6.35 | 6.35 | 0.0 | 606k | 1 |
| BistroInterior | 3.65 | 7.40 | 4.01 | 37.0 | 18.6 | 22.0 | 797k | 1 |
| BistroExterior | 4.16 | 6.99 | 3.99 | 105.0 | 93.5 | 46.6 | 2.83M | 1 |
| LightTest | 3.22 | 5.53 | 2.36 | 6.19 | 6.14 | 0.0 | 1.5k | 2 |
| CornellBox | 3.57 | 7.60 | 4.07 | 64.3 | 37.2 | 41.2 | 86 | 1 |

</details>

### R0.3 RE-MEASURED (2026-06-18, commit `928d3fe2`, SHIPPING path, frame 60, paused)

> ‼ **PRE-R1.0 — both these GI-pass ms numbers AND the noise-floor are pre-R1.0 → RE-MEASURE after R1.0**
> (R1.0 lights color-only/whole-mesh surfaces → more bounce hits → cost + temporal-noise shift). Re-freeze
> the §4 imgdiff gate against the post-R1.0 floor (`Docs/Validation/gi-noise-floor.json`).

| Senaryo (fixture) | GI pass ms | cpuFrame on | cpuFrame off | comp lum on | comp lum off | **isolate lum** | tris | GI A/B |
|---|---|---|---|---|---|---|---|---|
| Outdoor (sc-1, cascaded) | 3.46 | 6.34 | 4.73 | 0.00 | 0.00 | 0.00 | 1562 | **NO-OP (black scene)** |
| MultiLightInterior (sc-2, ≥8 punct) | 4.36 | 7.09 | 5.22 | 193.1 | 184.6 | 115.3 | 1546 | ACTIVE |
| ColorOnly (sc-3, whole-mesh) | 3.58 | 6.06 | 4.91 | 104.5 | 102.7 | **2.30** | 16 | **WEAK = R1.0 bug** |
| ThinWall (sc-4, leak) | 4.46 | 6.86 | 4.56 | 25.43 | 25.43 | **0.00** | 36 | LEAK-PASS (no bleed) |
| MovingLight (sc-5, two-rate) | 5.07 | 7.75 | 5.17 | 174.1 | 165.2 | 65.1 | 6 | ACTIVE |
| CornellBox (color-bleed) | 3.28 | 5.56 | 4.82 | 66.3 | 37.9 | 43.1 | 86 | **STRONG** |
| BistroInterior_Wine (heavy) | 3.87 | 6.80 | 3.63 | 41.5 | 20.4 | 24.4 | 797k | STRONG |
| BistroExterior (heaviest) | 4.14 | 6.98 | 3.84 | 25.0 | 12.5 | 12.3 | 2.83M | STRONG |

**GI-isolate A/B (oracle GEÇTİ — judged on the GI-ISOLATE, never composite mean, §4):**
- **GI pass PRESENT in ALL GI-ON captures (3.3–5.1ms), ABSENT in ALL GI-OFF captures** — `BALLISTIC_DX12_SSGI=0`
  makes `Dx12GiPass.Enabled()` false so the pass doesn't record. Definitive per-pass A/B signal.
- comp_gion SHA ≠ comp_gioff SHA on 7/8 scenes (Outdoor = NO-OP, see below). SHAs in `gi-noise-floor.json`.
- **STRONG, correct-direction GI:** CornellBox (37.9→66.3, isolate 43.1 color-bleed), BistroInterior
  (20.4→41.5), BistroExterior (12.5→25.0), MultiLight (184.6→193.1, isolate 115.3), MovingLight (165.2→174.1).
- **ColorOnly = the R1.0 MaterialId bug signature** — isolate bounce 2.30 (near-black) on a whole-mesh
  color-only surface: it barely bounces because MaterialId is degenerate (R0.0b assert 1). EXPECTED pre-R1.0.
- **ThinWall = leak-test PASS direction** — GI-isolate fully BLACK (lum 0.00, no indirect light THROUGH the
  wall); composite identical 25.43. Re-confirm with culling ON at R2.4.
- **Outdoor = GI NO-OP because the scene renders fully BLACK** (lum 0.00 everywhere; grazing/underground sun,
  same scene-data pattern as SunTemple/LightTest). The GI pass still RUNS (3.46ms) — not a GI regression.
  **OPEN: re-light the Outdoor fixture** so cascaded-shadow + bounce is visible (R0.0b fixture follow-up).
- ⚠ **Per-pass GPU ms caveat (re-measured):** `gpuPasses[]` is a CPU stopwatch (submit+fence-wait), NOT a GPU
  timestamp; `gpuFrameMs` is ALWAYS 0 (DX12 follow-up). Most passes show ~0.02ms (fence-wait≈0, under-reported)
  but the **GI pass shows real cost (3.3–5.1ms)** because it does a readback/OIDN round-trip that forces a
  real fence-wait. Real frame signal = `cpuFrameMs` + GI-pass presence; treat the per-pass ms as a floor.

**Determinizm KARAKTERİZE EDİLDİ (DoD part 1 — re-measured, varsayılmadı):**
- **PAUSED, same frame, run-to-run → BYTE-IDENTICAL.** CornellBox f60 GI-ON = `cc80835d72f5` ×2 runs;
  BistroInterior f60 = `290f54982066` ×2. SSGI temporal + GI/probe/DDGI passes are deterministic run-to-run.
- **PLAY mode (scripts+physics ACTIVE, MovingLight, NOT paused) → BYTE-IDENTICAL.** f120 = `86052bd20dcc` ×2.
  Even with the light-sweep script + physics, the headless path is deterministic (scripted input is
  deterministic; matches `dx12-noise-floor.json` sigma≈0).
- **f60 ≠ f240** (CornellBox `cc80835d72f5` vs `91f2ad42eeb2`): EXPECTED — temporal still converging across
  frames; the VALID determinism test is "same-frame, two runs", NOT "f24==f240".
- **Motion-dump run-to-run → BYTE-IDENTICAL** even with temporal active (CornellBox GI-isolate frame_07
  = `4e672f068b2ab024` ×2) → the noise-floor measurement itself is reproducible.
- **VERDICT:** GI passes are deterministic run-to-run (paused AND play) → byte-identical (SHA, same-frame,
  2 runs) is a VALID SMOKE-CHECK for the GI passes; NOT a hard gate across-frames; BLIND for the
  `DeterministicCapture` denylist passes per §4 (real oracle stays GBV + GI-isolate A/B + R1.0 explain-diff).

**Per-pass perceptual NOISE-FLOOR (DoD part 2 — GI-isolate output; full json `Docs/Validation/gi-noise-floor.json`):**
- Metric = mean abs per-channel delta (0..255) over 7 consecutive frame pairs of an 8-frame motion-dump
  (`BALLISTIC_DX12_GI_MOTION_DUMP`, GI-isolate ON, frame 180 = converged).
- **Resting floor (static, converged):** CornellBox **0.027**, BistroInterior **0.084**. This is the floor.
- Under motion (orbit=2°/frame): CornellBox 5.8, BistroInterior 17.2 — disocclusion converge-cost, **NOT a
  floor**; feeds R2.2 on-screen latency budget.
- **§4 imgdiff gate = noise-floor + margin:** static converged GI-isolate boiling must stay within
  `max(floor*3, floor+0.2)` ≈ **≤ 0.3 mean per-channel delta**. A real temporal-feedback regression
  (history boiling / NaN-grow / leak) pushes this up by orders of magnitude, far beyond 0.3 — caught while
  tolerating sub-ULP substrate noise. ‼ **RE-MEASURE + re-freeze post-R1.0.**

**GPU-güvenliği:** **8 senaryo × 3 captures + smoke + 4 determinism + 5 motion-dump = ~34 headless launch,
HEPSİ EXIT=0, ZERO device-removal**, DRED on (`BALLISTIC_DX12_DRED=1`). **The documented `DrawSsgi 0xC0000005`
crash (memory `dx12-procedural-sky-cloud-veil-fix`) did NOT reproduce** on this tree — it is resolved here
(consistent with the prior baseline's "~25 launches EXIT=0"). NO code change needed for R0.3. RT_GI/RT_SHADOWS=1
**NOT opened** (the known device-remove path); only the ScreenSpace SSGI shipping path was exercised — safe.

## R0.4 — GTX-1660 ekstrapolasyon (RT-core'suz floor)

> ⚠⚠ **STALE — DO NOT USE. This entire R0.4 section is the prior OUT-OF-ORDER worker's GTX-1660 model.**
> It is WRONG on two counts: (1) the rev3+ min-target is **RTX 2060**, not GTX-1660 (the 1660 extrapolation
> is irrelevant to the RT-capable floor); (2) its GI-pass ms inputs (3.2–4.2) are STALE — the RE-MEASURED
> R0.3 numbers are **3.3–5.1ms** (table above). The actual R0.4 chunk (NEXT) must rewrite this against the
> RTX 2060 class, model the RT budget separately+conservatively (RX9070XT→2060 RT ratio is non-linear), and
> close via the user-decided **'dev-enable' (modeled-only, no real 2060 on hand)** path. Left in place only
> as the record of what NOT to carry forward.

SSGI bir **compute** pass'i (RT-core'a bağımlı DEĞİL) → 1660 çarpanı bandwidth+ALU ile yönetilir, RT değil.
RX 9070 XT (~1557 GB/s, ~48 TFLOPS FP32) vs GTX 1660 (~336 GB/s, ~5 TFLOPS FP32). Bandwidth+ALU-bound bir
compute pass için temkinli **6×–9×** yavaşlama:

| Senaryo | GI ms (9070 XT) | ~1660 6× | ~1660 9× |
|---|---|---|---|
| SunTemple | 3.95 | 23.7 | 35.6 |
| BistroInterior | 3.65 | 21.9 | 32.9 |
| BistroExterior | 4.16 | 25.0 | 37.4 |
| LightTest | 3.22 | 19.3 | 29.0 |
| CornellBox | 3.57 | 21.4 | 32.1 |

**★ KRİTİK R0.4 BULGUSU (R2 bütçe kilidi için):** "Low/no-RT floor" hedefi olan **ScreenSpace SSGI bile**
GTX-1660'ta ~**20–37 ms** — TEK BAŞINA 16.6 ms (60fps) bütçesini katlıyor. Yani:
- 1660-sınıfı floor'da mevcut SSGI 1080p'de 60fps'e SIĞMAZ. R2.1 "Low = SSGI+IBL+SSAO @ 60fps" hedefi
  bu haliyle gerçekçi DEĞİL → R2'de ya (a) SSGI yarı-res/daha-az-slice, ya (b) Low'u 30fps'e, ya (c) Low'u
  SSGI'sız (IBL+SSAO only) tanımlamak gerekecek. Bu, R2'nin çözeceği gerçek tradeoff.
- RT preset'leri (High/Epic) için 1660 İLGİSİZ (1660 RT-core'suz, RayTraced→ScreenSpace auto-downgrade zaten var,
  P7.0). RT bütçesi RTX-2060/3070 referansıyla R2.1'de modellenir, 1660 çarpanıyla DEĞİL.

**DÜRÜST AÇIK-UÇ:** gerçek GTX-1660 donanımı elde YOK → yukarısı ekstrapolasyon. Gerçek-hw doğrulaması
ele geçince yapılacak (plan R0.4 açık-uç maddesi).

---

## R0.4 RE-WRITTEN — RTX 2060 class, RT modeled separately (2026-06-18, MODELED-ONLY)

> ★ **This replaces the STALE GTX-1660 model above** (kept for the record, not deleted). Min target =
> **RTX 2060 class** (rev3+, plan §5). **NO code, NO capture** — pure model/document chunk (PROVISIONAL
> POLICY: every input re-grepped from the tree + the R0.3 RE-MEASURED table + the §0 GI-component ms from
> memory `dx12-lumen-gi-p0-2026-06-16`). All RT-min numbers are a **MODEL** (no real 2060 on hand). Per the
> user decision this run, the **two-stage closure picks (a) dev-enable** (model suffices to close R3) and
> marks **(b) target-met PERMANENTLY MODELED** (no real 2060/3060 on hand → not ship-gating; see §closure).
>
> ‼ **PRE-R1.0.** Every GI-side input below is the R0.3 RE-MEASURED ms which is itself pre-R1.0. R1.0 lights
> color-only/whole-mesh surfaces → more lit RT hits → GI cost rises → **RE-MEASURE this entire model after
> R1.0** (the §4 gate + the dev-enable closure both re-validate at R2.5, plan ORDER FINDING line 44).

### (A) The two compute-vs-RT classes are modeled SEPARATELY (the core R0.4 mandate)

The GI stack splits into **two hardware classes** that scale to a 2060 by **different multipliers** —
modeling them with one TFLOPS-divide (the GTX-1660 model's mistake) is WRONG:

| Pass | HW class (re-grepped) | Why | Scaling basis to 2060 |
|---|---|---|---|
| **SSGI (SSILVB)** | **COMPUTE** (no RT cores) | screen-space horizon march, `Ssgi.hlsl`; reads depth/normal/color, no BVH | bandwidth+ALU only → R0.2's **5–8×** interval |
| **Screen-space probes (gather)** | **COMPUTE** | `ScreenProbeTrace` + DDGI gather is screen-space sampling of the atlas | bandwidth+ALU → **5–8×** |
| **DDGI trace/blend** | **RT-CORE** (DXR) | `DdgiTrace`/`DxrGi.ClosestHit` cast BVH rays per probe (inline/closest-hit) | **RT-core throughput, modeled separately+conservatively (below)** |
| **RT-GI hit shading** | **RT-CORE** (DXR) | `DxrGi` 1-bounce world ray + inline shadow rays | **RT-core, separate** |
| **RT reflections** | **RT-CORE** (DXR) | `DxrReflections` closest-hit off the world cache | **RT-core, separate** |

**Why the RT ratio is NON-LINEAR (not a TFLOPS-divide).** The RX 9070 XT is RDNA4 — **2nd-gen+ ray
accelerators** (RDNA4 ~doubled RT throughput vs RDNA3: denser BVH8 traversal, hardware instance/OBB
transform, more box/tri tests per RA per clock). The RTX 2060 is Turing TU106 — **1st-gen RT cores**
(no concurrent RT+shading on the same SM, no Shader Execution Reordering, no opacity micromaps,
nominal ~5 GRays/s). The generational RT-feature gap means **RT-bound work scales WORSE than the
ALU/bandwidth ratio** (a Turing 1st-gen RT core does materially less ray work per FLOP than an RDNA4 RA).
So RT passes get a **separate, MORE PESSIMISTIC multiplier than the compute 5–8×.**

### (B) Hardware specs (the divide bases — modeled, public figures, knowledge-cutoff)

| GPU | FP32 TFLOPS | Bandwidth | RT generation | Role |
|---|---|---|---|---|
| RX 9070 XT (dev) | ~48.7 | ~640–672 GB/s (256-bit GDDR6) | RDNA4 2nd-gen+ RA | measurement substrate |
| **RTX 2060 (min target)** | **~6.5** | **~336 GB/s** | **Turing 1st-gen RT** | **the budget gate** |
| RTX 3060 (next-up ref) | ~12.7 | ~360 GB/s | Ampere 2nd-gen RT (~2× tri) | sanity upper-bound |

- **Compute ratio (RX9070XT→2060):** FP32 ~48.7/6.5 ≈ **7.5×**; bandwidth ~660/336 ≈ **2.0×**. R0.2 already
  fixed the compute interval at **5× (optimistic) … 8× (pessimistic)** for the bandwidth+ALU-bound mix —
  R0.4 ADOPTS it unchanged for the COMPUTE passes (SSGI, screen-probe gather).
- **RT ratio (separate + conservative):** the raw FP32 divide (7.5×) is the FLOOR for RT, not the ceiling.
  Add the 1st-gen-RT penalty (no concurrent RT+shading serializes trace behind shading on Turing;
  smaller ray-tri rate per RA). R0.4 models RT at **8× (optimistic) … 14× (pessimistic)** — strictly
  worse than the compute interval, per the §2 R0.4 "RT ratio non-linear / model RT separately+conservatively"
  mandate. (Sanity: the 3060 with 2nd-gen RT + ~2× FP32 would land ~½ the 2060's RT slowdown — consistent.)

### (C) GI-on cost on the modeled 2060 (1080p NATIVE — the pessimistic gate)

Inputs (RX 9070 XT, re-grepped, NOT stale): per-component **§0** — SSGI **~4.2ms** (memory line 27),
screen-probe gather **~0.18ms** (line 158), DDGI trace+blend+gather **~0.41ms** (line 213), RT-GI hit
**~4.0–4.79ms** (lines 28/P2.1), RT-refl **1.51–2.07ms** (line 398). R0.3 RE-MEASURED whole **GI-pass
3.3–5.1ms** (this doc, table above — the SSGI ScreenSpace shipping path the baseline actually ran).

Apply the SEPARATE multipliers (compute 5–8×, RT 8–14×):

| Component | RX9070XT ms | class | 2060 optimistic | 2060 pessimistic |
|---|---|---|---|---|
| SSGI | 4.2 | compute ×5 / ×8 | 21.0 | 33.6 |
| Screen-probe gather | 0.18 | compute ×5 / ×8 | 0.9 | 1.4 |
| DDGI (trace+blend+gather) | 0.41 | RT ×8 / ×14 | 3.3 | 5.7 |
| RT-GI hit (if RayTraced GI) | 4.0 | RT ×8 / ×14 | 32.0 | 56.0 |
| RT reflections | 2.07 | RT ×8 / ×14 | 16.6 | 29.0 |

- **The SHIPPING High path is NOT "all of the above".** R2.1 High = screen-probe + SSGI (on-screen) +
  DDGI far-field + RT-refl (roughness-split) + emissive — it does **NOT** also run the full RT-GI hit
  per-pixel march (DDGI gather REPLACES it as the GI source, memory line 152). So the gate stack is:
  **SSGI + screen-probe gather + DDGI + RT-refl.**
- **Modeled High GI-on cost @ 1080p native, 2060:** optimistic 21.0+0.9+3.3+16.6 ≈ **~42 ms**;
  pessimistic 33.6+1.4+5.7+29.0 ≈ **~70 ms**. (Diffuse-only, refl-off: ~25–41 ms.)

### (D) Total frame on the modeled 2060 + the FSR verdict

R0.2 non-GI floor (RX 9070 XT) = **3.3–4.7 ms** cpuFrame (local floor scenes) → **2060 non-GI ~17–38 ms**
(compute 5–8×, R0.2). Add GI-on (C):

- **1080p NATIVE, High (SSGI+probe+DDGI+RT-refl):** non-GI 17–38 + GI 42–70 = **~59 .. 108 ms** →
  **9–17 fps.** Blows 60fps (16.6ms) AND 30fps (33.3ms) by a wide margin. **60fps@1080p-native on a 2060
  is NOT credible — confirmed, and now QUANTIFIED (R0.2 said it pre-GI; R0.4 confirms it with GI on top).**
- **FSR is MANDATORY, not optional** (R0.2's crux, now reinforced). The COMPUTE + screen-space passes
  (non-GI raster/post, SSGI, screen-probe gather, RT-refl's screen-space resolve) scale with internal
  pixel count: **FSR-Quality = 720p internal ≈ 2.25× fewer pixels → those passes ÷~2.25.** The **RT-core
  passes that are resolution-independent stay fixed:** DDGI is a fixed probe grid (res-independent), and
  the RT trace itself (ray count) is per-probe / per-reflection-pixel — DDGI does NOT scale with screen
  res; RT-refl's RAY cost scales with its (internal) pixel count but its BVH-traverse floor does not.
- **Modeled FSR-Quality (720p internal), High, 2060:**
  - non-GI: 17–38 ÷2.25 ≈ **8–17 ms**
  - SSGI: 21.0–33.6 ÷2.25 ≈ **9–15 ms**
  - screen-probe gather: 0.9–1.4 ÷2.25 ≈ **0.4–0.6 ms**
  - DDGI: **3.3–5.7 ms** (res-independent, UNCHANGED)
  - RT-refl: 16.6–29.0 ÷2.25 ≈ **7–13 ms** (screen-space-resolve part scales; BVH floor a touch higher)
  - **Total: ~28 .. 51 ms → fits 30fps (33ms) at the OPTIMISTIC end, misses it at the pessimistic end;
    misses 60fps (16.6ms) either way.**
- **★ PRE-R1.0 GATE VERDICT (modeled, pessimistic, 2060):** **the credible ship target is 30fps@1080p via
  FSR-Quality (720p internal), High-preset = screen-probe + SSGI + DDGI + RT-refl.** 60fps@1080p needs
  EITHER a 3060-class GPU (2nd-gen RT + ~2× FP32 ≈ halves the RT terms → ~14–26ms FSR-Quality) OR a Low
  preset (SSGI half-res + RT-refl off) — this is exactly the **R2.1 preset-math crux** to resolve with the
  real per-preset FSR internal-res numbers. R0.4 supplies the PRE-MODEL; **R2.1 finalizes it.**

### (E) Two-stage closure — DECIDED this run (user)

- **(a) dev-enable — SELECTED.** The model above is sufficient to CLOSE the R3 work: it establishes the
  target (30fps@1080p via FSR-Quality, High = probe+SSGI+DDGI+RT-refl on a 2060) and shows FSR is
  mandatory and RT-GI-per-pixel must stay OFF in favor of DDGI gather. Development proceeds on the dev
  card (RX 9070 XT) against this modeled budget; R2.1 tightens the preset math.
- **(b) target-met — PERMANENTLY MODELED.** No real 2060/3060 on hand (user decision this run: do not
  stop/ask for real hardware). The ship-gating measurement on a physical 2060 is **deferred indefinitely
  and explicitly marked MODELED-ONLY** — not a silent default, a recorded decision (plan §4 "target-met
  awaits real 2060/3060"; the closure options borrow/cloud/telemetry stay available if a card appears).
  Any "fits on RT-min HW" headline remains a MODEL until a physical 2060/3060 is measured.

**R0.4 DoD met:** RT vs compute classes modeled SEPARATELY (compute 5–8× adopted from R0.2; RT 8–14×
strictly more pessimistic, non-linear, justified by RDNA4-2nd-gen-RA vs Turing-1st-gen-RT gap); inputs
are the R0.3 RE-MEASURED 3.3–5.1ms GI-pass + §0 re-grepped component ms (NOT the stale 3.2–4.2); FSR
mandatory + 30fps@1080p-FSR-Quality target derived; two-stage closure WRITTEN, **dev-enable SELECTED**,
**target-met marked PERMANENTLY MODELED**; STALE GTX-1660 section PRESERVED above under its banner, new
2060 section added below it; everything flagged PRE-R1.0 → re-measure post-R1.0. **No code, no capture.**

---

## R0 DoD durumu (mandated order: R0.0 → R0.1 → R0.2 → R0.3 → R0.4 → R1 …)

- [x] **R0.0a** Re-ground (PROVISIONAL POLICY) — committed `ef7f28c1`.
- [x] **R0.0b** 5 fixtures + `bal validate` + 2 DoD asserts — committed `e470cda2`.
- [x] **R0.1** Bridge flipped volume-driven (diffuse GI re-enabled, env/PostFX resolve) — committed `d8ee45a7`.
- [x] **R0.2** Denominator (FSR-not-TSR + 16.6/33.3ms budget + non-GI cost + preliminary X) — committed `928d3fe2`.
- [x] **R0.3** Baseline (this chunk) — SHIPPING-path GI-isolate A/B across 8 scenarios (incl. Bistro, NOW
      present locally) + determinism CHARACTERIZED (paused & play byte-identical run-to-run; f60≠f240 expected)
      + per-pass perceptual NOISE-FLOOR measured (resting GI-isolate boiling 0.027–0.084 → §4 gate ≤0.3) →
      `Docs/Validation/gi-noise-floor.json`. ‼ X AND noise-floor are PRE-R1.0 → re-measure post-R1.0.
- [x] **R0.4** Extrapolation (this chunk) — STALE GTX-1660 section PRESERVED under its banner; new
      **"R0.4 RE-WRITTEN — RTX 2060 class"** section added below it. RT vs compute modeled SEPARATELY
      (compute 5–8× from R0.2; RT 8–14× strictly more pessimistic — RDNA4 2nd-gen+ RA vs Turing 1st-gen
      RT, non-linear, NOT a TFLOPS-divide). Inputs = R0.3 RE-MEASURED 3.3–5.1ms + §0 re-grepped component
      ms (SSGI 4.2 / probe-gather 0.18 / DDGI 0.41 / RT-GI 4.0 / RT-refl 1.5–2.07). **Verdict: 60fps@1080p
      -native NOT credible on a 2060; FSR mandatory; target = 30fps@1080p via FSR-Quality (720p internal),
      High = screen-probe+SSGI+DDGI+RT-refl.** Two-stage closure: **(a) dev-enable SELECTED**; **(b)
      target-met PERMANENTLY MODELED** (no real 2060/3060, user decision). ‼ PRE-R1.0 → re-measure post-R1.0.
- [~] R0.0b özel fixture'lar — built (`GiFixtures/`), ColorOnly bug confirmed visible (isolate 2.30) → R1.0.

**★ FAZ R0 TAMAMLANDI (R0.0a→R0.4).** Sıradaki = **R1.0 (MaterialId)** — R1's biggest item, ALREADY
committed OUT-OF-ORDER (`e1ccbbf6`/`fa3d6bb6`/`6b7e9565`) WITHOUT an R0 baseline. Per ORDER FINDING
(plan line 44) + §2 R2.5, R1.0's "explain any nonzero diff" + re-measured X/noise-floor were done on a
MISSING denominator → R1.0 is re-validated against THIS now-existing R0 baseline (re-confirm at R2.5).

---

## R1.0 RE-VALIDATED against the now-existing R0 baseline (2026-06-18, GPU-safe ScreenSpace path)

> PROVISIONAL POLICY applied: every load-bearing claim (e1ccbbf6 content, the R1.0 fix code, the R0.3
> ColorOnly=2.30, the noise-floor, the ancestry of the R0.3 substrate) was re-measured against the working
> tree by fresh `git`/`grep`/`read`/headless-capture, NOT taken from memory or the handoff. **NO code
> change** — R1.0's code fix is already correct + committed; this chunk is pure re-validate + re-measure.
> Raw outputs: `e:/tmp/gi-r1validate/` (cornell/coloronly/thinwall GI-isolate bmp+json + motion_cornell/).

### (1) ★ ORDER-FINDING CORRECTION — the R0.3 baseline ALREADY contained the R1.0 fix (proven by ancestry)

The handoff/ORDER-FINDING premise was **"R1.0 was verified WITHOUT an R0 baseline → now re-validate against
the now-existing R0 baseline; ColorOnly isolate should RISE."** Re-measured (`git merge-base --is-ancestor`):

- The R1.0 **code fix** landed in `3f3406e9` (01:16) — the user's post-FX commit, which is the PARENT of
  the R1.0 doc-commit `e1ccbbf6` (01:28). `e1ccbbf6` itself touches **only** `gi-revival-R1.0-materialid.md`
  (`git show e1ccbbf6 --stat` = 1 doc file) — it is the repro+verify RECORD, the fix was in `3f3406e9`.
- `git merge-base --is-ancestor 3f3406e9 928d3fe2` → **YES.** So the **R0.2 (`928d3fe2`) AND R0.3
  (`321243f1`) baselines were BOTH captured on a tree that ALREADY had the R1.0 fix in it.** The R0 baseline
  was never a "pre-R1.0" denominator on the code level — `3f3406e9` predates every R0.x commit (`ef7f28c1`
  R0.0a onward). The "PRE-R1.0" flag on R0.3/R0.4/noise-floor referred to the *plan-phase ordering*, not to
  the code state. **The denominator was NOT missing on the code side.**

### (2) ★ The R1.0 fix is RAYTRACED-PATH ONLY → it is DEAD CODE on the ScreenSpace SSGI shipping path

Re-grepped from the tree (the load-bearing structural fact the handoff/R0.3-doc both missed):

- `ResolveOrRegisterMaterialId` (`Dx12GpuDrivenRenderer.cs:303`) is consumed by **exactly one caller**:
  `Dx12RtGeometry.BuildTriMaterials` (`Dx12RtGeometry.cs:128`). That per-triangle MaterialId buffer feeds
  the **DXR closest-hit shaders** (RT-GI hit shading + RT reflections) via `rtGeometry.InstancesGpuAddress`
  + `gpuDriven.MaterialsGpuAddress` (`Dx12GiPass.cs:635/811`). It is the **RayTraced GI / RT-reflection
  path** only.
- The R0.3 baseline + this re-capture both run the **ScreenSpace SSGI shipping path** (`BALLISTIC_DX12_SSGI=1`,
  `PostFX.GiMode=ScreenSpace`). The SSGI SSILVB horizon march reads screen depth/normal/color — it **never
  reads the RT MaterialId buffer.** So on the path R0.3 measured, the R1.0 fix is **dead code.**
- ‼ **Therefore the R0.3 doc's label "ColorOnly isolate=2.30 = the R1.0 MaterialId bug signature, EXPECTED
  pre-R1.0" is a MISLABEL.** ColorOnly's 2.30 isolate is the **ScreenSpace SSGI** indirect bounce off a
  small/oblique color-only emitter (screen-space coverage limited), NOT the RT MaterialId degenerate buffer.
  The MaterialId bug only manifests on the RayTraced path — which R0.3 deliberately never opened (device-
  remove safety). The handoff's "ColorOnly isolate should RISE on the ScreenSpace path post-R1.0" is **false
  by construction** — it cannot rise on a path the fix doesn't touch.

### (3) Re-captured GI-isolate A/B (commit HEAD `ab84015e` + the 8-file post-FX WIP, ScreenSpace, paused f60)

| Scenario | R0.3 isolate | Re-captured isolate | SHA | Verdict |
|---|---|---|---|---|
| CornellBox (GI-ON) | 43.1 | **43.154** | `81dbf7a5667f` | STRONG color-bleed, no regression |
| CornellBox (GI-OFF) | (comp 37.9) | 37.676 (comp fallback) | `4a50b5b7c70f` | A/B differs → GI active |
| ColorOnly (GI-ON) | 2.30 | **2.288** | `55ec21c5cffb` | **UNCHANGED** (fix dead on SSGI path — correct) |
| ColorOnly (GI-OFF) | (comp 102.7) | 102.172 (comp fallback) | `e42fe2013a73` | SSGI-off → isolate = composite |
| ThinWall (GI-ON, leak) | 0.00 | **0.000** | `30bc4b4368f5` | LEAK-PASS HOLDS (no bleed-through) |

- **GI-isolate A/B oracle GEÇTİ** — judged on the GI-isolate, never composite mean (§4). CornellBox 43.154
  matches R0.3's 43.1 to 3 digits; ColorOnly 2.288 == R0.3's 2.30; ThinWall 0.000 == 0.00.
- **Soft-gate (R1.0(c)) re-confirmed:** the only "nonzero diff" R1.0 was supposed to produce is on the
  **RayTraced** path (color-only/split children now resolve their real material). On the ScreenSpace
  shipping path there is **byte-zero diff** — ColorOnly/ThinWall/CornellBox isolate are unchanged vs R0.3.
  The fix is a **strict superset on the RT path + dead code on the SSGI path** → no unexplained regression.
  The RT-path "color-only now bounces" claim was proven at commit time by the **CPU harness CASE 2** (old
  buffer `[0,…,0]` degenerate → fix points to the real id) — the proper oracle, since RT_GI=1 headless
  SaveBmp is the device-remove path (NOT opened here, plan §4 PRE-EXISTING).

### (4) Re-measured X + noise-floor → §4 gate RE-FROZEN (both UNCHANGED post-R1.0)

- **Noise-floor (CornellBox, static motion-dump, temporal active, frame 180, 8-frame window):** mean
  boiling **0.0270** (perPair `[0.026, 0.027, 0.027, 0.026, 0.027, 0.027, 0.028]`) — **identical to R0.3's
  0.027**, perPair byte-for-byte. The post-R1.0 floor == the pre-flag floor (expected: the fix is dead on
  this path → temporal-noise characteristics unchanged). **§4 imgdiff gate stays ≤0.3** (`max(floor*3,
  floor+0.2)`); RE-FROZEN against the post-R1.0 measurement — no change needed.
- **Budget X (R0.4 modeled 2060):** the GI-side input is the R0.3 RE-MEASURED 3.3–5.1ms whole-GI-pass on
  the **ScreenSpace shipping path**, which is what ships at High (DDGI gather REPLACES per-pixel RT-GI, so
  the gate stack never runs the RT MaterialId path per-pixel). R1.0 lit the RayTraced path's color-only
  surfaces, but that path is OFF in the shipping High preset → **the X model's GI-pass input is unchanged
  post-R1.0** (the ScreenSpace cost is what was measured; the RT cost rise only matters if a future preset
  enables per-pixel RT-GI, which R0.4 ruled out). R0.4's 2060 verdict (FSR mandatory, 30fps@1080p-FSR-
  Quality, dev-enable selected, target-met permanently-modeled) **stands unchanged.** Determinism HOLDS
  (CornellBox GI-ON `81dbf7a5667f` byte-identical run-to-run).
- **GPU-safety:** **8 headless launches** (5 GI-isolate A/B + 1 determinism run-2 + 1 motion-dump),
  ALL **EXIT=0, ZERO device-removal**, DRED on. RT_GI/RT_SHADOWS=1 **NOT opened**. DrawSsgi 0xC0000005 did
  NOT repro. Build 0-err (Runtime+Cli, hence DX12+engine; the 2 MCP "errors" were running-server file-locks).

### R1.0 RE-VALIDATE DoD durumu

- [x] (a) e1ccbbf6 DoD verified FROM CODE (PROVISIONAL POLICY): R1.0 fix (`ResolveOrRegisterMaterialId` +
      `BuildTriMaterials`) present + correct in tree; e1ccbbf6 is the doc record, fix is in parent `3f3406e9`.
- [x] (b) ORDER-FINDING corrected: R0.3 substrate (`928d3fe2`) ALREADY had the fix (`3f3406e9` ancestor) →
      the denominator was NOT missing on the code side; "PRE-R1.0" was a plan-phase label, not a code state.
- [x] (c) Re-captured GI-isolate A/B: ColorOnly 2.288 (== R0.3, fix dead on SSGI path — EXPLAINED, not a
      regression); ThinWall 0.000 (leak-pass holds); CornellBox 43.154 (strong, no regress). The R0.3
      "ColorOnly=2.30 = MaterialId bug" label is a MISLABEL — corrected here (it's the SSGI bounce).
- [x] (d) Soft-gate: ScreenSpace path byte-zero diff (no unexplained regression); RT-path correctness proven
      by commit-time CPU harness CASE 2 (degenerate buffer → real id). RT_GI not opened (device-safe).
- [x] (e) Re-measured X + noise-floor: noise-floor 0.0270 (== R0.3 0.027), §4 gate ≤0.3 RE-FROZEN; X model
      GI-input unchanged (ScreenSpace shipping cost); R0.4 2060 verdict stands. Determinism + 8 clean
      launches no-removal + build 0-err.

**★ R1.0 RE-VALIDATED. Sıradaki = R1.1 + R1.2 re-validation** (bindless tail `fa3d6bb6` + barrier audit
`6b7e9565`, both committed OUT-OF-ORDER → re-validate against the R0 baseline; R1.1 offsets enumerated
from the post-R1.0 tree, R1.2 GBV+DRED clean launches). **KEY HANDOFF NOTE:** the R0.3 "PRE-R1.0 → re-
measure" flag is now DISCHARGED — the re-measure showed NO change because the R1.0 fix is RT-path-only +
dead on the shipping ScreenSpace path; the §4 gate (≤0.3) + R0.4 budget are confirmed valid post-R1.0.

---

## R1.1 + R1.2 RE-VALIDATED against the now-existing R0 baseline (2026-06-18, HEAD `96c41d4d` + 8-file post-FX WIP)

> PROVISIONAL POLICY applied: every load-bearing claim (fa3d6bb6 content, the bindless-tail code, the offset
> enumeration, 6b7e9565 = no-code, the §4 gate) re-measured against the WORKING TREE by fresh
> `git show`/`grep`/`read`/build/headless-capture — NOT from memory or the handoff. **NO code change** — both
> chunks are already committed + correct (R1.1 byte-identical pure refactor; R1.2 doc-only); this is pure
> re-validate. Raw outputs: `e:/tmp/gi-r1revalidate/` (cornell/coloronly/thinwall/multilight GI-isolate bmp +
> .stats.json + .log). Both `fa3d6bb6` and `6b7e9565` confirmed ANCESTORS of HEAD `96c41d4d` (`git merge-base
> --is-ancestor` = YES, ×2) → the rebuilt binary contains both.

### R1.1 (bindless tail `fa3d6bb6`) — DoD verified FROM CODE + byte-identical smoke

- **(a) `git show fa3d6bb6 --stat`** = 4 files: `Dx12BindlessTail.cs` (NEW, +105), `Dx12Backend.cs` (6),
  `Dx12GiPass.cs` (19), `Dx12ReflectionsPass.cs` (10). Matches the DoD (centralize the 4 RT/GI tail bases).
- **(b) Code re-read from tree (not the commit):** `Dx12BindlessTail.cs` is the single source —
  `HeapCapacity=16384` (the ONLY place the cap is named), four RESERVED counts (`RtRefl 16 / ScreenProbe 4 /
  DDGI 4 / RtGi 8`) + the cap are the ONLY layout inputs; **all four bases are DERIVED by cumulative
  subtraction from the cap** (`RtGiTableBase = HeapCapacity - RtGiReserved` → 16376; each lower base subtracts
  the blocks above → DDGI 16372 / ScreenProbe 16368 / RtRefl 16352). Eight **COMPILE-TIME asserts**
  (`1 / (cond ? 1 : 0)` CS0020 div-by-zero guards) verify: derived bases == historical 16352/16368/16372/16376,
  each block's USED ≤ RESERVED, the tail is < cap and > 0. The static-ctor `_ = A_… + …` touches all guards so
  the compiler must evaluate them.
- **(c) ★ Offsets ENUMERATED FROM THE TREE (never hand-listed) — "zero hand-listed magic number" PROVEN:**
  - `grep "16384"` over all `BallisticEngine.DX12/` → ONLY: `Dx12BindlessTail.cs:39` (the `HeapCapacity` def),
    the historical comments in that file + `Dx12GiPass.cs:150` / `Dx12ReflectionsPass.cs:85`, the **unrelated**
    `Dx12Backend.cs:73` `UiHeap` (a SEPARATE ImGui present heap, NOT the bindless tail — correctly out of the
    allocator's scope), and `IblBake.hlsl:112` (an unrelated radiance clamp `min(...,16384.0)`). **No
    `16384 - N` computation literal in ANY active code.**
  - `grep "Dx12BindlessTail|HeapCapacity"` → `Dx12GiPass.cs:155-157` reads `Dx12BindlessTail.{RtGi,Ddgi,
    ScreenProbe}TableBase`; `Dx12ReflectionsPass.cs:87` reads `Dx12BindlessTail.RtReflTableBase`;
    `Dx12Backend.cs:70` creates `BindlessHeap` with `Dx12BindlessTail.HeapCapacity`. All consumers read the
    single source.
  - `grep "16376|16372|16368|16352"` → the four base values appear ONLY in `Dx12BindlessTail.cs` (historical
    comments + the compile-time assert *equality checks*, which is the intended verification — the RUNTIME
    bases are the derived `HeapCapacity - …` expressions, not the literals). No consumer hand-lists a base.
- **(d) Build 0-err (compile-time asserts PASSED):** `dotnet build BallisticEngine.DX12 -t:Rebuild` (full
  rebuild → the static asserts are actually evaluated) → **0 Error(s)** (22 pre-existing warnings, not mine);
  Cli 0-err. The CS0020 guards hold → the derived layout matches the historical bases.
- **(e) GPU-safe byte-identical smoke (ScreenSpace shipping path, paused f60, DETERMINISTIC, GI_ISOLATE=1,
  SSGI=1, DRED on):** every value matches the R1.0 re-validate reference EXACTLY:
  - CornellBox GI-ON: lum **43.154**, sha **81dbf7a5667f** (== R0.3/R1.0 ref, byte-identical)
  - CornellBox GI-OFF: lum 37.676, sha 4a50b5b7c70f (A/B differs → GI active)
  - ColorOnly GI-ON: lum **2.288**, sha **55ec21c5cffb** (== ref); GI-OFF lum 102.172 sha e42fe2013a73 (== ref)
  - ThinWall GI-ON: lum **0.000**, sha **30bc4b4368f5** (leak-pass holds; run2 == run1 byte-identical)
  - MultiLightInterior GI-ON: lum 115.544 (== R0.3 isolate 115.3)
  - CornellBox GI-ON run2 sha == run1 → determinism holds run-to-run.
  - ★ EXPECTED: R1.1 only re-points the **RT-path** SRV table indices (RtGi/RtRefl/DDGI/ScreenProbe tail), which
    are inert on the ScreenSpace SSGI shipping path → byte-identical is correct, NOT a missed regression. (RT_GI
    visual A/B not run — device-unsafe headless SaveBmp path, §4 PRE-EXISTING; the compile-asserted equality to
    the historical bases is the RT-path correctness oracle.)

### R1.2 (barrier audit `6b7e9565`) — no-code-change verified + GBV/DRED clean launches

- **(d) `git show 6b7e9565 --stat`** = 1 file (`Docs/Plans/gi-revival-R1.2-barrier-audit.md`, +110), **NO code
  change CONFIRMED** (audit-only verify record). The audit doc maps 5 DDGI `irradianceTex` raw-barrier paths
  (DispatchDdgi/DispatchGather/screen-probe trace/RT-refl/DumpIrradianceStats) + the state-tracked
  idempotent helpers (Dx12OffscreenTarget/Dx12ScreenProbe) — ALL SYMMETRIC (UAV-on-entry → UAV-on-exit), no
  asymmetry/missing-restore found.
- **(e) GBV + DRED clean launches — UAV↔SRV asymmetry: NONE (by construction):**
  - **8 DRED-on headless launches** this re-validate (5 R1.1 smoke + ColorOnly-off + MultiLight + ThinWall-run2),
    ALL EXIT=0, **ZERO device-removal, ZERO DRED/fault/0xC0000005/TDR markers** in any log.
  - Since R1.1 (pure const centralization, byte-identical) + R1.2 (doc-only) changed **NO barrier code**, the
    GBV signature set is **invariant by construction** — no new ResourceBarrier, no new resource-state
    transition was introduced. The substrate-matched GBV baseline `Docs/Validation/dx12-gbv-baseline.json`
    (RX 9070 XT / driver 32.0.31019.2002, commit 9912b749) holds unchanged.
  - **‼ GBV LIVE RUN: SKIPPED per plan §4 HARD RULE (re-confirmed this session).** Raising `TdrDelay` to ~60s
    needs HKLM write = elevation; this session is **NOT admin** (`IsAdmin=False`; `TdrDelay NOT SET` = default
    2s). Running GBV (10-100× slower) at the default 2s TDR would trip a FALSE device-removal — the documented
    PC-freeze path (standing GPU-hang GOTCHA: never relaunch-loop, PC crashed once). This is a GPU-SAFETY
    constraint, not a solvable build/oracle issue. The substitute-evidence path (static audit + baseline
    invariance + DRED clean launches + byte-identical render) is plan-§4-SANCTIONED for an audit-only no-code
    chunk. **GBV-with-raised-TdrDelay stays OPEN for a privileged/real-HW closure** — unchanged from the prior
    R1.2 record (it was skipped for the SAME elevation reason then).

### R1.1 + R1.2 RE-VALIDATE DoD durumu

- [x] R1.1 (a) `fa3d6bb6 --stat` = 4 files (Dx12BindlessTail.cs NEW + 3 consumers); (b) code re-read = single
      source, derived bases, 8 compile-time asserts; (c) offsets ENUMERATED from tree — no `16384 - N` literal
      in active code, all consumers read `Dx12BindlessTail.*`, bases appear only in the allocator (comments +
      assert equalities); (d) build 0-err = asserts passed; (e) byte-identical ScreenSpace smoke (43.154 /
      2.288 / 0.000 == R1.0 ref) + determinism + 5 clean launches.
- [x] R1.2 (d) `6b7e9565 --stat` = 1 doc file, NO code change; (e) 8 DRED-on clean launches no-removal, UAV↔SRV
      asymmetry NONE (no barrier code changed → GBV signature set invariant; substrate-matched baseline holds).
- [~] R1.2 GBV LIVE: §4 HARD RULE (no elevation to raise TdrDelay) → SKIPPED, GPU-safe; substitute-evidence
      path used. GBV-with-raised-TdrDelay open for a privileged/real-HW closure.

**★ R1.1 + R1.2 RE-VALIDATED (no code change — both already correct + committed). FAZ R1 (R1.0/R1.1/R1.2)
all re-validated against the now-existing R0 baseline. Sıradaki = R1.3 (OIDN 2nd-run crash repro-first) →
R2.1 (presets).** Per the handoff, R1.3 may be re-ordered (the PID-handle fix is ALREADY in the tree per
R0.0a re-ground — `Dx12OidnGpuPath.cs:31` shareSeq + per-process unique names; R1.3 should re-verify the fix
HOLDS via two back-to-back zero-copy captures, NOT repro-from-scratch). The plan §2 next listed phase after
R1 is **R2.1 (presets: High = screen-probe+SSGI+DDGI+RT-refl roughness-split @ modeled 2060, calibrated to
the R0.4 budget — the preset-math crux with real FSR-Quality internal-res numbers).**

---

## R1.3 RE-VERIFIED — OIDN 2nd-run NAME_ALREADY_EXISTS fix HOLDS (2026-06-18, HEAD `3a33e3ac` + 8-file post-FX WIP)

> PROVISIONAL POLICY applied: every load-bearing claim (the shareSeq fix location, its commit, the readback
> fallback path) re-measured against the WORKING TREE by fresh `git blame`/`grep`/`read` + headless captures —
> NOT from memory or the handoff. **NO code change** — the fix is already in the tree + correct; this chunk is
> pure re-verify-it-HOLDS. Raw outputs: `e:/tmp/gi-r13/` (cap1.bmp / cap2.bmp / cap_guide.bmp + .stats.json).
> ‼ The handoff said the OIDN file is in `Resources/` — it is NOT; it is `BallisticEngine.DX12/Oidn/Dx12OidnGpuPath.cs`
> (PROVISIONAL POLICY catch: re-located via `Glob **/Dx12Oidn*.cs`).

### (1) The fix is in the tree AND committed (re-grepped + git blame, not assumed)

- **`Dx12OidnGpuPath.cs:31`** `static int shareSeq; // process-wide counter → unique shared-handle names (avoid 0x887A002C)`.
- **`:124-126`** the color buffer's shared-handle name = `$"BallisticOidnSharedFloat_{Environment.ProcessId}_{Interlocked.Increment(ref shareSeq)}"`
  → per-process (PID) + per-instance (monotonic counter) UNIQUE. The inline comment (`:119-123`) documents the
  exact failure mode: a FIXED name `"BallisticOidnSharedFloat"` fails with `DXGI_ERROR_NAME_ALREADY_EXISTS`
  (`0x887A002C`) if a prior/concurrent process still holds it OR if `Ensure()` re-creates on resize before the
  old handle is closed; the name is only used to OPEN-by-name (which the code NEVER does — it imports the IntPtr
  directly), so any unique string works.
- **`:163-168`** the two AUX buffers (albedo/normal guides, P6.1 guided denoise) use the SAME pattern:
  `BallisticOidnAlbedo_{PID}_{shareSeq++}` / `BallisticOidnNormal_{PID}_{shareSeq++}` — so a single process that
  runs Ensure + EnsureAux creates THREE shared handles, all on the same incrementing counter, all unique.
- **`git blame`:** the color-buffer fix (`:31`/`:124-126`) is commit **`b86e2b4a0`** (2026-06-16); the aux fix
  (`:163-168`) is **`9a799cec2`** (2026-06-16). BOTH are ancestors of HEAD `3a33e3ac` → the rebuilt binary has
  them. `git diff HEAD -- Dx12OidnGpuPath.cs` = EMPTY (file is committed/unmodified, NOT part of the 8-file WIP).

### (2) Readback fallback is DEFAULT-SAFE (re-read from `Dx12GiPass.cs`)

`Dx12GiPass.cs:345-389` is the OIDN dispatch. The zero-copy GPU path is tried first
(`ssgiOidn.SharedCapable && !ssgiSharedFailed && !ssgiOidnForceReadback`). On ANY failure it sets
`ssgiSharedFailed = true` ("readback from now on") — a STICKY one-way downgrade:
- `Ensure()` returns false (shared-handle create / OIDN import fail) → `ssgiSharedFailed = true` (`:388`).
- HIP execute fail → `ssgiSharedFailed = true` (`:383`).
- The CPU readback (`:392-397`) then runs whenever the GPU path produced no output (`giForCombine == histWrite`).
- The readback itself "degrades gracefully without DLLs" (`:319`), denoise-skip if `BALLISTIC_DX12_SSGI_OIDN=0`.
- `BALLISTIC_DX12_OIDN_READBACK=1` is the explicit A/B door to force the readback path (default unset = prefer
  zero-copy). So: zero-copy preferred, readback is the safe fallback, no DLL → graceful skip. **Default-safe.**

### (3) Two back-to-back zero-copy captures BOTH SUCCEED (the DoD — GPU-safe ScreenSpace path)

Recipe (GPU-safe, GI-isolate, paused f60, DRED on): `BALLISTIC_DETERMINISTIC=1 BALLISTIC_SCREENSHOT_PAUSED=1
BALLISTIC_SCREENSHOT_FRAME=60 BALLISTIC_DX12_DRED=1 BALLISTIC_DX12_GI_ISOLATE=1 BALLISTIC_DX12_SSGI=1
BALLISTIC_DX12_OIDN_TIMING=1` on `CornellBox.scene`. OIDN runs on the SSGI/GI denoise (ScreenSpace), which is
GPU-safe — NOT the RT_GI device-remove path (§4 PRE-EXISTING, NOT opened).

| Run | OIDN path | NAME_ALREADY_EXISTS / 0x887A002C | device-removal | EXIT | screenshot sha256 |
|---|---|---|---|---|---|
| cap1 (1st process) | **ZERO-COPY** (`sharedCapable=True`, `denoise avg 5.21ms/60 ZERO-COPY`) | NONE | NONE | 0 | `81DBF7A5667F…` |
| cap2 (2nd process, back-to-back) | **ZERO-COPY** (`denoise avg 5.52ms/60 ZERO-COPY`) | NONE | NONE | 0 | `81DBF7A5667F…` |
| cap_guide (guided: Ensure + EnsureAux = 3 shared handles in ONE process) | **ZERO-COPY** (`denoise avg 6.08ms/60 ZERO-COPY`) | NONE | NONE | 0 | (guided) |

- **Both back-to-back zero-copy captures SUCCEED** (the DoD): no NAME_ALREADY_EXISTS, no device-removal, EXIT=0.
- **cap1 sha256 == cap2 sha256** (`81DBF7A5667F…`, byte-identical) AND the prefix `81dbf7a5667f` is the EXACT
  R1.0/R1.1 CornellBox GI-ON reference → the fix HOLDS and the shipping-path GI image is UNCHANGED (no regress).
- **cap_guide** stresses the per-instance uniqueness HARDEST: the guided path creates 3 shared handles
  (1 color + 2 aux) in ONE process, all incrementing shareSeq — still ZERO-COPY, no collision, EXIT=0.

### R1.3 DoD durumu

- [x] (a) Fix in tree + committed: `Dx12OidnGpuPath.cs:31/124-126` (`b86e2b4a0`) + `:163-168` aux (`9a799cec2`),
      both HEAD ancestors; file unmodified vs HEAD (NOT part of the 8-file WIP). PROVISIONAL POLICY: re-grepped,
      git-blamed — not assumed. (File path corrected: `Oidn/`, not `Resources/`.)
- [x] (b) Readback fallback default-safe: `Dx12GiPass.cs:345-389` sticky one-way downgrade to CPU readback on any
      shared-path failure; readback degrades gracefully without DLLs; `BALLISTIC_DX12_OIDN_READBACK=1` is the
      explicit A/B force door (default unset = prefer zero-copy).
- [x] (c) Two back-to-back zero-copy captures BOTH SUCCEED (DoD): cap1 + cap2 both ZERO-COPY, EXIT=0, no
      NAME_ALREADY_EXISTS/0x887A002C, no device-removal; sha256 byte-identical == R1.0/R1.1 ref `81dbf7a5667f`;
      guided 3-handle path also clean. 3 headless launches, ALL EXIT=0, ZERO device-removal, DRED on.

**★ R1.3 RE-VERIFIED (no code change — fix already in tree + HOLDS). FAZ R1 (R1.0/R1.1/R1.2/R1.3) COMPLETE,
all re-validated against the now-existing R0 baseline. Sıradaki = R2.1 (presets — below).**

---

## R2.1 — Preset tables (High / Epic) calibrated to the R0.4 modeled-2060 budget (2026-06-18, HEAD `3a33e3ac`)

> PROVISIONAL POLICY applied: re-grepped every dial + budget input from the tree (`PostProcessSettings.cs`,
> `GlobalIllumination.cs` volume, `UpscaleMode` enum, `GiMode`/`ReflectionMode` enums, the R0.4 RE-WRITTEN
> model above) — NOT from memory. **NO code change** — R2.1 DEFINES the preset DATA + the calibration math;
> the `GiQuality` enum + dial-derivation WIRING is **R3.2** (this is the "preset tables WRITTEN" chunk, the
> knob is added later). **NO new GI technique** — a preset is a knob over the existing SSGI / DDGI /
> screen-probe / RT-refl / FSR passes only.

### (A) The dials a preset controls (re-grepped from the tree — the existing knobs, nothing new)

A preset is a fixed assignment over these EXISTING members (no new field needed for R2.1):

| Knob | Type / source | Role |
|---|---|---|
| `PostFX.GiMode` / volume `giMode` | `GiMode {Off,ScreenSpace,RayTraced}` | diffuse-GI technique select |
| `PostFX.SsgiEnabled` | bool | SSGI on-screen bounce |
| `PostFX.ScreenProbes` / volume `screenProbes` | bool (consumed when `GiMode==RayTraced`) | near/mid-field final gather |
| `PostFX.Ddgi` / volume `worldRadianceCache` | bool (consumed when `GiMode==RayTraced`) | off-screen far-field world cache |
| `PostFX.GiEmissive` / volume `emissiveAsGi` | bool | emissive-as-area-light in the bounce |
| `PostFX.ReflectionMode` / volume `reflectionsMode` | `ReflectionMode {ScreenSpace,RayTraced,Off}` | specular technique |
| `PostFX.SsrEnabled` | bool | SSR gate (reflections WIP — user's separate work, NOT touched here) |
| `PostFX.SsgiRayCount` / volume `rayCount` | int 1..16 (clamp ≤8 slices) | SSGI horizon slices |
| `PostFX.SsgiMaxHistory` / volume `maxHistory` | float 1..64 | temporal accumulation frames |
| `PostFX.UpscaleMode` | `UpscaleMode {Off,NativeAA,Quality(1.5×),Balanced(1.7×),Performance(2.0×),UltraPerformance(3.0×)}` | FSR internal-res ratio |

### (B) ‼ THE PRESET-MATH CRUX (flagged by the plan; resolved here as a DEFINITION + a wiring gap for R3.2/R2.2)

R0.4's High = "screen-probe + SSGI + DDGI far-field + RT-refl, **RT-GI per-pixel march STAYS OFF** (DDGI gather
replaces it)". But in the CURRENT tree, `ScreenProbes` + `Ddgi` are consumed **only when `GiMode==RayTraced`**
(`GlobalIllumination.cs:59/66` `[ShowIf(giMode, RayTraced)]`; `Dx12GiPass.DrawRtGi` is the per-pixel
cosine-hemisphere RT-GI trace AND it hosts the DDGI/screen-probe hierarchy, `:538-541`). So the exact R0.4
High configuration — **DDGI + screen-probe gather WITHOUT the brute per-pixel RT-GI march** — is **not yet a
single switch the code exposes.** Two ways to realize it, the choice is an R3.2/R2.2 wiring decision (NOT R2.1):

1. **`GiMode=RayTraced` with a "gather-only" sub-flag** that skips the per-pixel `DrawRtGi` cosine march and
   feeds the screen-probe → DDGI gather as the GI source (the literal R0.4 "DDGI gather replaces RT-GI" — needs
   a new code branch in `Dx12GiPass`, deferred to **R2.2 two-rates**, which is exactly where on-screen-fast vs
   off-screen-loose DDGI round-robin is built).
2. **`GiMode=ScreenSpace` (SSGI only)** as a pragmatic High-minus until the gather-only branch lands. This is
   what the **shipping path runs today** (the R0.3/R1.x baseline = `GiMode.ScreenSpace`, SSGI + screen-bounded
   probes). It is leak-free + GPU-safe + already validated, but it is screen-bounded (no off-screen far-field).

**R2.1 DECISION (recorded, not silent):** the preset TABLE below names the R0.4-intended end-state
(option 1 — DDGI-gather High), and explicitly marks the **gather-only RT-GI branch as the R2.2 wiring
dependency**. Until R2.2 lands that branch, the **runtime High preset degrades to option 2** (`GiMode=ScreenSpace`)
— byte-identical to today's shipping path, the only validated GPU-safe config. This keeps R2.1 a pure
DATA+CALIBRATION chunk (no code, no regression) while honestly flagging the one passmap gap R2.2/R3.2 closes.

### (C) The preset tables (DATA — the dial assignments per preset)

**Low = DEFERRED** (plan §0/§3 — RT-capable floor; No-RT Low is a measured-out, premature-opt deferral).

| Dial | **High** (RTX 2060, ship target) | **Epic** (RTX 3070+) | Notes |
|---|---|---|---|
| `GiMode` | `ScreenSpace` (today) → `RayTraced`+gather-only (R2.2 end-state) | `RayTraced` (full incl. per-pixel where it fits) | crux (B) |
| `SsgiEnabled` | true | true | on-screen fast bounce both |
| `ScreenProbes` | true (gather-only) | true | near/mid final gather |
| `Ddgi` (world cache) | true (off-screen far-field, loose round-robin) | true (denser round-robin / more probes) | R2.2 two-rate |
| `GiEmissive` | true | true | emissive-as-GI |
| `ReflectionMode` | `RayTraced` (roughness-split: rough→cache, sharp→re-shade) | `RayTraced` (more rays, lower roughness cutoff) | R2.3 builds the split |
| `SsrEnabled` | (governed by user's reflections WIP — untouched) | (same) | NOT set by R2.1 |
| `SsgiRayCount` (slices) | 4 | 8 (clamp) | Epic = more slices |
| `SsgiMaxHistory` | 24 (default) | 32 | Epic = smoother/laggier OK |
| `UpscaleMode` | **`Quality` (1.5×/dim → 720p internal @ 1080p) — MANDATORY** | `NativeAA` (1.0×) or `Quality` | High needs FSR per R0.4 |

### (D) Calibration — does High fit the modeled 2060 at target-fps? (per-preset FSR internal-res math)

The plan §2 R2.1 DoD calibration test = "all of High fits 2060 at target-fps." Using the R0.4 RE-WRITTEN
modeled-2060 budget (this doc §"R0.4 RE-WRITTEN") with the **High dial set above** (gate stack =
SSGI + screen-probe gather + DDGI + RT-refl; **NO per-pixel RT-GI hit** — DDGI gather replaces it):

**Per-preset FSR internal-res numbers (the crux the plan asked R2.1 to resolve):**
- **High `UpscaleMode=Quality` = 1.5× per dimension → 1280×720 internal @ 1920×1080 display = exactly
  2.25× fewer pixels** (1.5² = 2.25). This is the divisor R0.4 modeled. Screen-space/compute passes (non-GI
  raster+post, SSGI, screen-probe gather, RT-refl's screen-resolve) scale ÷2.25; DDGI (fixed probe grid) is
  res-independent (UNCHANGED); RT-refl BVH-traverse floor is res-independent (a touch higher than ÷2.25).

| High component | RX9070XT ms | 2060 native (R0.4) | **2060 @ FSR-Quality (720p)** |
|---|---|---|---|
| non-GI (raster+shadow+post) | 3.3–4.7 | 17–38 | ÷2.25 → **8–17** |
| SSGI (compute) | ~4.2 | 21.0–33.6 | ÷2.25 → **9–15** |
| screen-probe gather (compute) | ~0.18 | 0.9–1.4 | ÷2.25 → **0.4–0.6** |
| DDGI (RT, res-independent) | ~0.41 | 3.3–5.7 | **3.3–5.7** (unchanged) |
| RT-refl (RT; screen-resolve ÷2.25, BVH floor fixed) | ~2.07 | 16.6–29.0 | **7–13** |
| **TOTAL** | — | **~59–108 ms (9–17 fps native)** | **~28–51 ms** |

- **VERDICT (modeled, pessimistic 2060):** High @ FSR-Quality = **~28–51 ms → fits 30fps (33.3 ms) at the
  OPTIMISTIC end, misses it at the pessimistic end; misses 60fps (16.6 ms) either way.** This is IDENTICAL to
  R0.4's verdict (consistent — R2.1's dial set IS the R0.4 gate stack). **The credible ship target = 30fps@
  1080p via FSR-Quality, High preset = screen-probe+SSGI+DDGI+RT-refl, per-pixel RT-GI OFF.**
- **★ CALIBRATION RESULT (plan DoD):** "all of High fits 2060 at target-fps" → **TRUE only at the optimistic
  model end (30fps@FSR-Quality).** At the pessimistic end it overruns 33 ms → R2.5 VRAM/budget will re-measure
  POST-R1.0/R2.3 with the real refl cost (R0.2's "RE-RUN if R2.3 changes the reflection path" flag — RT-refl
  cache-vs-re-shade shifts the refl term). The plan's fallback "if R0.2 refutes, switch to Epic-only-3070" is
  NOT triggered: High DOES fit at the optimistic end on a 2060 @ FSR-Quality, so High-on-2060 stays the target;
  Epic is the 3070+ tier (more rays/res/history), NOT a 2060 demotion.
- **target-met = PERMANENTLY MODELED** (R0.4 (b), user decision this run — no real 2060/3070 on hand; the
  "fits at the optimistic end" headline stays a MODEL until a physical card is measured; dev proceeds on RX
  9070 XT against this budget).

### (E) ‼ Re-run / dependency flags carried forward (so R2.3/R2.5 key off them)

- **RT-refl term is PROVISIONAL** — R0.4/R2.1 use the current P8.0 "via world cache" cost (~2.07ms RX9070XT).
  **R2.3** builds the roughness-split (rough→cache cheap, sharp→re-shade-at-hit costlier) → the RT-refl term
  CHANGES → **RE-RUN this High-fit calibration at R2.5** (R0.2's flagged dependency).
- **DDGI/screen-probe round-robin RATE is unset here** — R2.1 names DDGI "loose round-robin" but the actual
  on-screen-fast (≤N frames / ~100ms) vs off-screen-loose (1/8→1/16, few-s) split + its cost is **R2.2**.
- **The gather-only RT-GI branch (crux B option 1) is the R2.2 wiring dependency** — until it lands, the
  runtime High preset = `GiMode=ScreenSpace` (option 2, byte-identical to today's validated shipping path).

### R2.1 DoD durumu

- [x] Preset tables WRITTEN (High / Epic; Low deferred) — dial assignments over the EXISTING knobs (re-grepped
      from `PostProcessSettings.cs` + `GlobalIllumination.cs`), NO new GI technique.
- [x] Calibrated to the R0.4 modeled-2060 budget WITH per-preset FSR internal-res numbers (High `Quality` =
      720p internal = 2.25× fewer pixels; per-component ÷2.25 except res-independent DDGI + RT BVH floor) →
      High @ FSR-Quality = ~28–51 ms = fits 30fps at the optimistic end (= R0.4 verdict, consistent).
- [x] Preset-math crux RESOLVED as a definition + recorded wiring gap (DDGI-gather-without-per-pixel-RT-GI is
      the R2.2 wiring dependency; runtime High degrades to `GiMode=ScreenSpace` until then — byte-identical to
      the validated shipping path, so NO behaviour regression, preset is a knob).
- [x] GI-isolate A/B unchanged on the shipping path (NO code → CornellBox GI-ON `81dbf7a5667f` byte-identical,
      verified post-R2.1) + build 0-err (DX12 -t:Rebuild + Runtime). NO new GI technique.
- [~] Re-run flags carried forward: RT-refl term PROVISIONAL (RE-RUN at R2.5 after R2.3 roughness-split);
      round-robin rate is R2.2; gather-only branch is R2.2.

**★ R2.1 PRESETS WRITTEN + CALIBRATED (no code change — DATA + calibration chunk; the GiQuality enum + dial
derivation is R3.2, the gather-only branch + two-rate is R2.2). Sıradaki = R2.2 (two rates: on-screen fast
SSGI+screen-probe + off-screen loose DDGI round-robin; budget the two latencies separately).**
