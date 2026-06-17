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

- **Render çözünürlüğü:** 1920×1080 native (headless `Dx12HeadlessRuntime` default). Hedef model: 1080p iç-render
  → (ileride) TSR/FSR 4K, Lumen modeli.
- **Frame bütçesi:** 60fps = **16.6 ms** / 30fps = **33 ms**.
- **GI'ya kalan = 16.6 − (direkt ışık + gölge + post).** RX 9070 XT'de bu pay BÜYÜK (toplam non-GI pass ~0.05–0.3ms,
  cpuFrame ~2.4–4ms GI-off) — dev kartta sığma sorunu yok. **Asıl payda kısıtı hedef-GPU'da** (R0.4).

## R0.3 — 4(+1)-senaryo baseline (GI-isolate A/B, RX 9070 XT, frame 60, paused+deterministic)

| Senaryo | GI pass ms | cpuFrame on | cpuFrame off | comp lum on | comp lum off | bounce lum | tris | draws |
|---|---|---|---|---|---|---|---|---|
| SunTemple | 3.95 | 6.82 | 3.23 | 6.35 | 6.35 | 0.0 | 606k | 1 |
| BistroInterior | 3.65 | 7.40 | 4.01 | 37.0 | 18.6 | 22.0 | 797k | 1 |
| BistroExterior | 4.16 | 6.99 | 3.99 | 105.0 | 93.5 | 46.6 | 2.83M | 1 |
| LightTest | 3.22 | 5.53 | 2.36 | 6.19 | 6.14 | 0.0 | 1.5k | 2 |
| CornellBox | 3.57 | 7.60 | 4.07 | 64.3 | 37.2 | 41.2 | 86 | 1 |

**GI-isolate A/B doğrulaması (oracle GEÇTİ):**
- `BALLISTIC_DX12_SSGI=0` → GI pass **0 ms** (pass hiç kaydolmuyor; `allpasses_sum` ~4ms → ~0.06–0.3ms). Delta TEMİZ.
- Her sahnede comp_gion SHA ≠ comp_gioff SHA → GI gerçekten composite'i değiştiriyor (composite mean'e değil, izole
  bounce'a + SHA'ya bakıldı).
- **Güçlü, doğru-yönde GI:** BistroInterior (18.6→37.0), CornellBox (37.2→64.3, color-bleed), BistroExterior (93.5→105.0).
- **Bounce~0:** SunTemple + LightTest — bu GI regresyonu DEĞİL, sahne-data karanlık (GI pass koşuyor ama sahne siyah;
  memory `gdf-stuck-coarse` / `dx12-procedural-sky` not'larıyla uyumlu PRE-EXISTING).

**Determinizm (oracle GEÇTİ — varsayılmadı, ölçüldü):**
- **Run-to-run, aynı frame 240, iki bağımsız koşu → BYTE-IDENTICAL** (GI ON: `698d97…`, GI OFF: `e74736…`).
  `BALLISTIC_DETERMINISTIC`'in garantisi budur ve GI re-enable sonrası KORUNUYOR.
- f24 ≠ f240: BEKLENEN — SSGI temporal accumulation (SsgiMaxHistory=24) frame'ler boyunca yakınsar; "aynı-frame-iki-koşu"
  geçerli determinizm testidir ve GEÇER. (f24==f240 GI'lı temporal yolda doğru oracle DEĞİL.)
- GI-ON SHA ≠ GI-OFF SHA → GI katkısı SHA seviyesinde de kanıtlı.

**GPU-güvenliği:** 5 senaryo × (perf on/off + 3 render) = ~25 headless launch, HEPSİ EXIT=0, **sıfır device-removal**,
DRED breadcrumbs açık (`BALLISTIC_DX12_DRED=1`), canlı editör (PID 3384) GPU'da iken bile çakışma yok (ayrı-process
D3D12 device). RT_GI / RT_SHADOWS=1 **AÇILMADI** (memory'deki headless SaveBmp device-remove yolu); yalnız ScreenSpace
SSGI yolu koşturuldu — güvenli.

## R0.4 — GTX-1660 ekstrapolasyon (RT-core'suz floor)

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

## R0 DoD durumu

- [x] Sistem açık (diffuse GI re-enabled, env/PostFX resolve geri).
- [x] Payda çivilendi (1080p / 16.6ms / GI-payı dev-kartta bol, floor'da dar).
- [x] Determinizm DOĞRULANDI (run-to-run byte-identical, varsayılmadı).
- [x] 5-senaryo baseline (GI-isolate + composite + per-pass GPU ms + SHA).
- [x] GTX-1660 ekstrapolasyon (compute 6×/9×; RT ayrı, R2'ye bırakıldı).
- [~] R0.0b özel fixture'lar (color-only whole-mesh, thin-wall) → R1.0'a devredildi (açık kalem).

**Sıradaki:** R1 güvenilirlik sertleştirme — R1.0 MaterialId color-only/whole-mesh bug (alt-faz).
