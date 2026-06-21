# kajiya Fidelity Uplift — otonom döngü

> **Durum:** AKTİF, otonom. Branch `dx12-perf-radical`, sık commit.
> **Kullanıcı kararı:** GI-önce→reflection-sonra→TAA/post cila. Mevcut DDGI'ye ENTEGRE et;
> yetmezse genişlet/değiştir ama **realtime kalmalı** (RX 9070 XT @ 1440p 60+ FPS taban).
> Maksimum kapsam — değer katan her şey. **ASLA kolay/düşük-fidelity yol YOK.** Full, düzgün, HQ
> implementasyon. "Bu daha easy" diye kestirme seçme — her parça doğru/tam yapılır. Fidelity her zaman iyidir.
> Kullanıcı "dur" diyene kadar DURMA. Full kontrol agent'ta.
> **Döngü:** implement → bug-hunt(agent) → geliştirme-önerisi(agent) → implement → bug-hunt …
> kullanıcı "dur" diyene veya gerçekten iş kalmayana kadar.
> **Kaynak:** `e:/tmp/kajiya` (klonlu). Port: `[[vk::binding]]`→`register()`, `.rgen`→inline RayQuery,
> Rust orchestration→C# pass. CLAUDE.md gotcha'ları: .hlsl re-embed (obj temizle), SM6.6 heap
> sırası, NaN ternary (lerp değil), fp16 clamp 60000, P0b atlas realloc WaitForFrame.

## Mevcut motor durumu (analiz edildi)
- DDGI: 64 Fibonacci ray/probe, inline RayQuery, oct 8x8 irradiance + 16x16 visibility moments
  (Chebyshev), tek EMA loop, multi-bounce prev-frame feedback, occupancy-aware placement.
  YOK: SH, blue-noise, enerji-korumalı BRDF (sadece Lambert /pi), screen-space spatial denoise.
- RT reflections: tek mirror ray, **roughness>0.6 TAMAMEN kesik**, temporal+motion-gate (spatial YOK),
  DDGI probe gather indirect.
- Sky: Hillaire 2020 (transmittance LUT + aerial froxel + baked env cube) — kajiya seviyesinde, DOKUNMA.
- Tonemap: AgX default + ACES, lux-kalibreli histogram exposure + EMA. 3D LUT yok.
- G-buffer: 5-MRT (SurfaceSkeleton). kajiya packed RGBA32F + ayrı geometric normal.

## Felsefe kararı (kullanıcı: "gi çözümü var, entegre et; yetmezse genişlet, ama realtime kalsın")
- DDGI cache-space EMA = tek temporal loop, KALIR.
- Spatial denoise (feedback YOK) + cache-space re-trace validation TERCİH → tek-loop korunur.
- Screen-space temporal GI feedback: SADECE gerekirse ve full clamp+variance+validation triad ile,
  door arkasında, default OFF (OIDN black-noise gotcha riski).

## Sıra (GI → reflection → cila). Her madde ayrı commit + BALLISTIC_DETERMINISTIC=1 byte-id + CarDemo doğrulama.

### FAZ A — GI fidelity (önce)
- **A1. Multi-scatter enerji-korumalı BRDF + FG-LUT** ⏳
  kajiya `inc/brdf_lut.hlsl` (Belcour/Kulla-Conty), `layered_brdf.hlsl`. 64x64 FG-LUT bake (bir kez).
  Hedef: `DeferredLighting.hlsl` spec/diffuse — rough metaller artık karanlık değil. Multiplicative,
  feedback YOK, sıfır felsefe riski. DDGI relight hit shading de aynı BRDF'i kullanır (`DdgiRelight.hlsl`).
  Doğrulama: rough metal A/B parlaklık; byte-id det capture (BRDF deterministik).
- **A2. Blue-noise ray dithering (R2 + opsiyonel Owen-Sobol)** ⏳
  kajiya `inc/blue_noise.hlsl` + `quasi_random.hlsl`. DDGI relight ray dağılımı. Det'te OFF (Fibonacci+rot).
  Hedef: `DdgiRelight.hlsl` SphereDir jitter → blue-noise. Banding/temporal-crawl ↓.
- **A3. Variance-driven adaptive SPATIAL GI denoise** ⏳ (felsefe-güvenli, en büyük GI gürültü kazanımı)
  kajiya `rtdgi/spatial_filter.hlsl`. Crunch firefly tonemap (`v/(max3+1)`) + depth-plane + SSAO-steering
  edge-stop + golden-spiral adaptive radius. SCREEN-SPACE ama SPATIAL (history yok, feedback yok).
  Hedef: DDGI sample/combine arası yeni `DdgiSpatialDenoise.hlsl`. GTAO zaten var (SSAO steering girdisi).
- **A4. SSGI near-field complement (split near/far)** ⏳ (DDGI'nin yapısal zayıflığını kapatır)
  kajiya `ssgi/ssgi.hlsl` (radyans taşıyan GTAO) + `near_field_settings.hlsl` blend. Probe ~2m → contact
  GI/crevice AO yok. Near-field screen-space, far-field DDGI; mesafeyle blend. Mevcut GTAO'yu radyans
  taşıyacak şekilde genişlet. Near-field kendi screen-temporal'ı DDGI EMA'sıyla ayrı kalır.
- **A5. Cache-space sample validation (re-trace) + M-clamp/luma-ratio damping** ⏳
  kajiya `diffuse_validate.rgen` + `temporal_validity_integrate.hlsl` KAVRAMINI cache-space'e uyarla.
  Periyodik (her 3. frame) probe ray re-trace → luma jump'ta EMA alpha boost / damping. Lumen runaway
  cure'u, tek-loop'u GÜÇLENDİRİR. Screen feedback YOK.
- **A6. L1 SH irradiance opsiyonu (oct yanında A/B)** ⏳
  kajiya `sh.hlsl` + `sum_up_irradiance.hlsl`. Oct atlas yeterli olabilir; SH daha ucuz/yumuşak olabilir.
  Door'lu A/B, default oct kalır.
- **A7. ircache leak-defense kavramları (rank + voting + normal-bias)** ⏳ (maksimum kapsam)
  kajiya `ircache/` rank sistemi + output-sensitive voting + normal-biasing. DDGI probe placement +
  multi-bounce gather'a uyarla (sızıntı azaltma). Mimari ama portable kavram.

### FAZ B — Reflections (sonra) — İLERLEME
- **B1+B2 ✓** VNDF GGX reflection sampling (Heitz/Falcor) + roughness>0.6 kesimi KALKTI (→1.0).
  `4ab9e3ca`. Per-pixel R2 blue-noise urand (det fixed), below-surface mirror fallback, gentle
  roughFade. Det A==B (Bistro ext), RT refl gerçek katkı (meanErr 0.007). Rough yüzeyler artık yansıyor.
- **[bug-hunt #3 ✓]** B1/B2 VNDF math DOĞRU, crash/NaN/layout/det temiz. 1 regresyon (F2): rough
  0.6-1.0 bandı kamera hareketinde gürültülü (tek-ray geniş cone + temporal motion'da kapanır).
  FİX `a7fde3ed`: multi-sample VNDF (roughness'a göre 1→4 ray, decorrelated R2, ortalama). F6:
  ctx.FrameCounter'a geçildi. Det A==B.
- **B4 ✓** soft_color_clamp reflection temporal'a (kajiya inc/soft_color_clamp). `7f5ea340`. Hard box
  clamp → soft 1σ→3σ ramp (anti-ghosting, rough VNDF history için). Det A==B.
- **B3 (RTR reservoir) DEĞERLENDİRME:** B1/B2+multiray rough reflection boşluğunu ZATEN kapattı (temiz
  glossy). Full RTR reservoir = ağır (screen-space reservoir history buffer'ları, reflection-only feedback).
  Marjinal getiri vs yüksek efor. Karar: checkpoint review sonrası — değerse yap, değmezse atla.

#### FAZ B kalan
- **B1. VNDF (Heitz) GGX importance sampling + correlated Smith** ⏳
  kajiya `inc/brdf.hlsl:184`. 2-4x spec varyans ↓. `roughness>0.6` kesimini AÇAR.
- **B2. Rough reflection (kesimi kaldır) + BRDF-footprint spatial resolve** ⏳
  kajiya `rtr/resolve.hlsl` (Zhdan self-stabilizing). Roughness lobe → kernel boyu. Spatial-only.
- **B3. RTR reservoir (ReSTIR reflections) — door'lu** ⏳
  kajiya `rtr/rtr_restir_temporal.hlsl`. Event 600, diffuse'a SIFIR temas. Dual reprojection
  (surface + virtual-hit). Tüm reflection path'inde kalır.
- **B4. soft_color_clamp + working-color-space (crunch) reflection temporal** ⏳
  kajiya `inc/soft_color_clamp.hlsl` + `working_color_space.hlsl`. Anti-ghosting drop-in.

### FAZ C — TAA + post cila — İLERLEME
- **Checkpoint review #1 ✓:** B3 RTR reservoir SKIP (B1/B2/multiray zaten çözdü), C3 SKIP (TAA pre-exposure
  HDR → pump yok), FAZ D hepsi SKIP (15ms headroom'da perf rework, 0 fidelity). DO-NOW: C1/C6/C4.
- **C1+C4+C6 ✓** TAA soft clamp + confidence-widened box + firefly input clamp. `70b1ed3c`. TAA det'te
  kapalı → goldens byte-id. Live run temiz. DO-LATER: C5 (dilated velocity), C2 (perceptual space), C7 (sharpen+TPDF).
- **[bug-hunt #4 sırada]** TAA C1/C4/C6 — sole-AA feedback loop, ghosting/instability/NaN denetimi.

### FAZ C — kalan
- **C1. soft_color_clamp → mevcut TAA clip** (drop-in, 5 satır)
- **C2. Perceptual (sqrt-luma/crunch) accumulation space TAA**
- **C3. Pre-exposure delta history compensation** (histogram exposure var → pump fix)
- **C4. Confidence-based history blend (input_prob)** — kajiya TAA stabilite keystone'u
- **C5. Dilated closest-depth velocity + velocity-history rejection**
- **C6. Firefly-clamped input pre-filter**
- **C7. Perceptual edge-aware sharpen (CAS-equiv) + TPDF dither**

### FAZ D — maksimum kapsam follow-up (değer kalırsa)
- Packed G-buffer + ayrı geometric normal (5-MRT rework — riskli, en son, door'lu A/B).
- WRC far-field probe grid (distant fallback).
- Half-res ray + bilateral upsample optimizasyonu (perf bütçesi için).

## Perf bütçe kuralı (RX 9070 XT @ 1440p 60+)
Her ağır parça (A3/A4/B3) half-res trace + bilateral upsample default. Toplam yeni GPU ms'i
`bal perf` / .stats.json ile ölç; 60 FPS'i kıran her şey door arkası default-OFF.

## İlerleme
- **A1 ✓** Multi-scatter BRDF (Belcour) deferred'a girdi. `d39d7c8b`. Door BALLISTIC_DX12_MS_BRDF
  (default ON). Det byte-id (ON_A==ON_B), OFF!=ON (CarDemo meanErr 0.0017/max 0.039 rough-metal
  lokalize). Sun+punctual+ambient-IBL lobe'ları reflMult + transFraction split + metalness boost.
  Not: DDGI relight diffuse-only olduğu için MS-BRDF orada gereksiz (dokunulmadı). VsmPad0→MsBrdfEnabled.
- **A2 ✓** Blue-noise R2 (plastic) ray dithering DDGI relight. `5bbf10c7`. 2D Cranley-Patterson
  (azimut+z-stratum), spatial+temporal decorrelation. Det fixed per-probe offset (byte-stable). A==B.
- **A3 ✓** Variance/validity-driven SPATIAL GI denoise (kajiya rtdgi/spatial_filter). `72407896`.
  Yeni DdgiSpatialDenoise.hlsl compute, Sample→Combine arası. Sample alpha'ya validity yazar; düşük-
  validity (seam/corner) pixeller adaptif golden-spiral blur + depth/normal/SSAO edge-stop + crunch
  firefly. Door BALLISTIC_DX12_DDGI_DENOISE (default ON, =0 byte-id). Feedback YOK → felsefe-güvenli.
  Det A==B, OFF==pre-A3, ON!=OFF (seam-lokalize). indirectFiltered 2. target.
- **[bug-hunt #1 ✓]** 3 adversarial agent (BRDF/denoise/blue-noise). A1,A2 temiz. A3'te 2 gerçek bug:
  denoise compute SRV'leri pixel-state (indirect t0 + GTAO t3) → non-pixel olmalı. FİX `1c877c81`.
  ctx.AoToNonPixelShaderResource action eklendi. Görsel byte-id (sadece hazard kapatıldı).
- **A4 İN PROGRESS — SSGI near-field complement** (DDGI coarse-probe zayıflığı: ~2m probe → contact
  GI/crevice yok). MİMARİ KARAR: kajiya SSGI'yi prev-frame radiance + reprojection ile DEĞİL, mevcut
  SceneColor'ı (event 500, direct+sky çoktan var) okuyan kendi pass'ıyla yapıyorum → temporal-history/
  motion-vector altyapısı GEREKMEZ, lag yok, deterministic-friendly, felsefe-güvenli (screen-temporal
  GI feedback YOK). Mevcut Gtao.hlsl'in KANITLANMIŞ horizon-march slice integrali genişletilir: aynı
  ufuk taramasında görünür sample'larda near-field radyans (SceneColor) toplanır (kajiya fetch_lighting).
  Sample/Combine arası blend: yakın=SSGI, uzak=DDGI mesafeyle (smoothstep). Door-gated, realtime (half-
  res march mümkün). Bu en büyük görsel-gap parçası, kolay yol YOK.
- **A4 ✓** SSGI near-field complement. `1657bbda`. DdgiNearField.hlsl (radyans-taşıyan horizon march,
  GTAO integralini reuse), current SceneColor okur (history YOK), Combine coverage-weighted additive
  blend (near=SSGI far=DDGI). Door _NEARFIELD (default ON) + _INTENSITY/_RADIUS/_BLEND. Det A==B,
  OFF byte-id, Bistro interior ON!=OFF (maxErr 0.13), açık CarDemo etkisiz (doğru). SceneColor
  NonPixel→RT restore. 3slice×6step realtime. nearField 3. target.
- **[bug-hunt #2 ✓]** A4 state/P0b denetlendi: crash/hang/race/leak YOK. 1 latent FSR bulgusu
  (pre-existing, reflections'la paylaşık): near-field gather ctx.SceneColor yerine ctx.Target okumalı
  (FSR'de SceneColor=empty fsrOutput). FİX `4be89c6b` (native byte-id, FSR-safe).
- **A5 ✓** Cache-space sample validation (per-texel luma-ratio EMA boost). `e0505a4f`. kajiya
  diffuse_validate kavramı cache-space'e. Staleness → alpha boost (lighting değişiminde hızlı yakınsama,
  Lumen-runaway cure). Door _VALIDATE (default ON). Det inert (HistoryValid=0) → byte-id A4. Felsefe GÜÇLENDİ.
- **A6 ATLANDI (gereksizlik kararı, kestirme DEĞİL):** L1 SH irradiance. Oct 8x8 atlas zaten HQ +
  Chebyshev visibility çalışıyor; SH net upgrade değil, "daha ucuz olabilir" marjinal. Maksimum-kapsam
  ama değer-katmayan → kullanıcının "değmeyenleri ele" yetkisiyle atlandı.
- **A7 KISMİ/ATLANDI:** ircache leak-defense. normal-bias DDGI'de ZATEN var (NormalBias). rank+voting
  ircache hash-grid'e özgü, DDGI probe-grid'e oturmaz (Chebyshev + occupancy-placement zaten leak-defense).
  Net değer yok → atlandı.
- **FAZ A KAPANDI.** GI fidelity: multi-scatter BRDF + blue-noise + spatial denoise + near-field SSGI +
  cache validation. Hepsi byte-id default-korumalı, deterministik, realtime, tek-loop felsefe-güvenli.
- **[geliştirme turu #1 ✓]** Agent quality review (FAZ A vs kajiya). P1 (near-field noise decorrelation),
  P3 (radius 0.8→1.5m bridge), P4 (A5 hue-aware per-channel metric) YAPILDI `aed5c73d`. P5 (ShadeHit
  Lambert KORUNDU — multi-scatter eklemek regresyon olurdu, agent doğruladı). P2/P6/P7 marjinal → ertelendi.
  Perf doğrulandı: DDGI total 0.13ms GPU, gpuFrame 1.81ms (Bistro int 797K tris) → 60fps bol başlık.
- **FAZ A TAM KAPANDI** (8 commit: A1-A5 + improve). GI fidelity büyük sıçrama, byte-id default, realtime.
- (FAZ B sırada — reflections: VNDF + rough>0.6 kesimini kaldır + BRDF-footprint resolve + RTR reservoir)
