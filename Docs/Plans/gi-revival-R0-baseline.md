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

## R0 DoD durumu (mandated order: R0.0 → R0.1 → R0.2 → R0.3 → R0.4 → R1 …)

- [x] **R0.0a** Re-ground (PROVISIONAL POLICY) — committed `ef7f28c1`.
- [x] **R0.0b** 5 fixtures + `bal validate` + 2 DoD asserts — committed `e470cda2`.
- [x] **R0.1** Bridge flipped volume-driven (diffuse GI re-enabled, env/PostFX resolve) — committed `d8ee45a7`.
- [x] **R0.2** Denominator (FSR-not-TSR + 16.6/33.3ms budget + non-GI cost + preliminary X) — committed `928d3fe2`.
- [x] **R0.3** Baseline (this chunk) — SHIPPING-path GI-isolate A/B across 8 scenarios (incl. Bistro, NOW
      present locally) + determinism CHARACTERIZED (paused & play byte-identical run-to-run; f60≠f240 expected)
      + per-pass perceptual NOISE-FLOOR measured (resting GI-isolate boiling 0.027–0.084 → §4 gate ≤0.3) →
      `Docs/Validation/gi-noise-floor.json`. ‼ X AND noise-floor are PRE-R1.0 → re-measure post-R1.0.
- [ ] **R0.4** Extrapolation — RTX 2060 (NOT GTX-1660) RT budget modeled separately+conservatively;
      two-stage closure (dev-enable / target-met). ⚠ **The R0.4 section above is STALE** — it is the
      prior out-of-order worker's GTX-1660 model, which contradicts the rev3+ min-target (RTX 2060). The
      R0.4 worker must re-write it against the 2060 class and feed it the RE-MEASURED R0.3 GI-pass ms
      (3.3–5.1ms, above), NOT the stale 3.2–4.2 numbers in the old R0.4 table.
- [~] R0.0b özel fixture'lar — built (`GiFixtures/`), ColorOnly bug confirmed visible (isolate 2.30) → R1.0.

**Sıradaki:** **R0.4** (extrapolation/model chunk, no code). THEN R1.x (already committed `e1ccbbf6`/`fa3d6bb6`/
`6b7e9565` OUT-OF-ORDER) is re-validated against this now-existing R0 baseline at R2.5.
