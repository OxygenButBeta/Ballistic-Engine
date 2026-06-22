# DDGI Radiance Cache — Lumen'i SÖK, sıfırdan ayrı klasörde yaz

> **Durum:** UYGULAMADA. Worktree `ddgi-rewrite` (HEAD=dx12-perf-radical'dan dallandı).
> **Kullanıcı kararı:** (1) Lumen rendering'den TAM sökülür. (2) DDGI sıfırdan ayrı klasörde (`Ddgi/`).
> (3) Kullanılacak parça yeni klasöre kopyalanır — eski Lumen'e sıfır bağımlılık. (4) Full otonom.
> **İlke:** GI dünya-uzayı, view-independent cache; ekran-uzayı temporal/denoise YOK → tek feedback loop.

## Klasör
```
BallisticEngine.DX12/Ddgi/
  Dx12DdgiPass.cs          tek product pass (event 500)
  Dx12DdgiProbeGrid.cs     probe grid state + atlas alloc + dünya AABB→grid
  Shaders/ (Shaders\Ddgi\ altında embed)
    DdgiRelight.hlsl       Pass1: per-probe RT trace + shading(card-light'tan kopya) + EMA
    DdgiSample.hlsl        Pass2: full-res 8-probe trilinear gather
    DdgiCombine.hlsl       Pass3: E*albedo*ao/PI → HDR (One/One)
```

## Yeniden kullanılan PAYLAŞILAN altyapı (Lumen değil)
ctx.Dxr (SceneAS/Device5/RtGeometry), Dx12FrameCb, Dx12OffscreenTarget, Dx12DescriptorHeap,
Dx12BindlessTail (yeni DdgiTableBase), event 500, HW-RT gate. `Dx12Lumen*` tipine DOKUNMA.

## Söküm (D0)
- DX12HDRenderer: lumenGiPass field+new+Add → ddgiPass. ctx.LumenScene set'ini kaldır.
- ctx.LumenActiveThisFrame → GiActiveThisFrame (FrameContext + deferred:198 + orchestrator:2400).
- Reflections useCards: D5'e kadar zorla false (LumenScene yok).
- Volume köprüsü + PostFX.LumenEnabled KORUNUR (asset uyumu).

## Aşamalar — HEPSİ TAMAM (commit'ler)
- D0 ✓ Lumen söküm + DDGI iskelet (no-op). `351f1b23`
- D1+D2 ✓ probe grid + relight(RT+EMA) + sample(trilinear) + combine. İlk ışık. `b2baef38`
- D3 ✓ visibility moments + Chebyshev sızıntı önleme (+self-occlusion bias). `8cec24c2`
- D4 ✓ ucuz multi-bounce (prev-frame irradiance feedback) + hysteresis EMA. `e43ef8fd`
- D5 ✓ reflections DDGI probe GI paylaşımı (card cache → probe gather). `552b7af0`
- D6 ✓ eski Lumen D0'da silindi; kod'da sıfır Dx12Lumen ref; tam solution build temiz.

Her aşama: BALLISTIC_DETERMINISTIC=1 byte-id (det'te 2 run identical), dotnet build temiz, CarDemo raw-E doğrulandı.

## Sonuç (ölçülen)
- Aktif kod ~4000→~900 satır. Buffer 17→4 (irradA/B, visA/B). Door 57→10. Temporal loop 5→1. History flag 3→1.
- off≠on (GI etki: meanErr 0.008 CarDemo açık sahne). Det capture byte-stable.

## Açık / follow-up
- **Kapalı GI test sahnesi yok**: primitive Cube sahnesi (Cornell box) render edilmiyor (Cube.obj
  StaticMeshRenderer GPU'ya çizilmiyor — DDGI'dan bağımsız engine/asset sorunu, ayrı incelenecek).
  D3 Chebyshev'in asıl faydası (duvar sızıntısı) bu yüzden görsel doğrulanamadı; teknik doğruluk
  (matematik, NaN yok, açık sahnede artefakt min) CarDemo + raw-E ile doğrulandı.
- Door hedefi ≤8'di, 10 oldu (NORMALBIAS + BOUNCE_BOOST tuning door'ları). Kabul edilebilir.
- Async-compute decoupling yok (sade tutuldu); gerekirse follow-up.

## Door (≤8)
DDGI(follow volume) / DDGI_RAYS(64) / DDGI_GRID(16x8x16) / DDGI_ALPHA(0.05) / DDGI_INTENSITY(1) /
DDGI_DEBUG(0) / DDGI_NOBOUNCE(0) / DDGI_NOVIS(0).

## Gotcha
.hlsl re-embed (obj temizle) / SM6.6 heap sırası (SetDescriptorHeaps önce) / NaN ternary / P0b atlas realloc WaitForFrame.

---

# DDGI SONRASI — kajiya'dan kazanımlar (follow-up)

> **Kaynak:** `EmbarkStudios/kajiya` (MIT/Apache). Render shader'larının %97'si HLSL (177 dosya) —
> dil aynı, Vulkan binding modeli (`[[vk::binding(N)]]`) DX12 `register()`'a çevrilir. Rust kısmı
> sadece orchestration (kajiya-rg) → okunup C#'a yeniden yazılır, taşınmaz.
> **KUTSAL İLKE:** DDGI'nin TEK-loop felsefesi korunur. Hiçbir kajiya parçası ekran-uzayı
> temporal/denoise GETİRMEZ (Lumen'i o yüzden çöpe attık). Diffuse GI mimarisi DDGI'da kalır;
> kajiya = math + look + reflection + relight-kalite deposu, GI mimarisi DEĞİL.

## K-sırası (efor/risk/getiri — yukarıdan başla)

| Sıra | Parça | kajiya kaynağı | Hedef | Risk | Getiri | Loop felsefe |
|---|---|---|---|---|---|---|
| **K1** | `inc/` math lib | brdf/layered_brdf/sh/quasi_random/blue_noise/hash/oct enc-dec | DdgiRelight.hlsl, DdgiCommon | yok | orta | bozmaz |
| **K2** | Tonemap + look | post/, lut/, working_color_space (Tony McMapface) | Composite.hlsl, ExposureHistogram.hlsl | düşük | **yüksek** | bozmaz (final renk) |
| **K3** | Sky/atmosphere | inc/atmosphere.hlsl, sky/ | AerialPerspective*.hlsl, ProceduralSky.hlsl, SkyTransmittance.hlsl | düşük | orta | bozmaz |
| **K4** | RTR reflections | rtr/ (reservoir refl + denoise) | Dx12ReflectionsPass + yeni Rtr shader | orta | orta | **sadece reflection path'inde** |
| **K5** | ReSTIR → relight | rtdgi/restir_temporal+spatial, inc/reservoir.hlsl | DdgiRelight.hlsl ışın seçimi | yüksek | yüksek | **sadece doğru yapılırsa** |

## K1 — inc/ math kütüphanesi (HEMEN, risksiz)
- Binding yok, saf math → doğrudan kopya. `brdf.hlsl`/`layered_brdf.hlsl` (enerji-korumalı BRDF),
  `sh.hlsl` (SH irradiance), `quasi_random.hlsl`+`blue_noise.hlsl` (düşük-tutarsızlık ışın jitter),
  `hash.hlsl`, oct enc/dec.
- **Hedef:** DDGI relight ışın dağılımı + hit shading kalitesini ucuza yükseltir. Det capture'da
  blue-noise OFF (mevcut Fibonacci+frame-rot deterministik kuralı korunur).
- Doğrulama: byte-id det capture; CarDemo raw-E artefakt ≤ mevcut.

## K2 — Tonemap + look (DÜŞÜK risk, EN YÜKSEK görsel getiri)
- Tony McMapface tonemap LUT + exposure + working/display color space. GI'den bağımsız → tek-loop'a
  sıfır dokunur, sadece final renk dönüşümü.
- **Hedef:** `Composite.hlsl` tonemap aşaması + `ExposureHistogram.hlsl` ile uyum. "Render kalitesi
  çok iyi" hissinin yarısı bu. Mevcut histogram exposure (lumen-perf-uplift) korunur, sadece
  tonemap eğrisi + color management değişir.
- Doğrulama: A/B screenshot; SDR çıktı clip yok, nötr griler korunur.

## K3 — Sky / atmosphere (DÜŞÜK risk)
- kajiya atmosfer modeli (`inc/atmosphere.hlsl` + Felix varyantı) — DDGI miss'inde okunan sky/IBL'i
  besler. Sende zaten Hillaire aerial (`AerialPerspective*.hlsl`) var → kıyas + iyileştirme kaynağı.
- **Gotcha:** fp16 clamp (sky Inf → NaN, CLAUDE.md). DDGI relight miss'i de bunu okur → tutarlı.

## K4 — RTR reflections (ORTA, DİKKAT)
- kajiya `rtr/`: reservoir tabanlı stochastic reflections + rough denoise. DDGI D5'te reflections
  zaten probe gather'dan besleniyor; RTR rough yüzey reflection KALİTESİNİ artırır.
- **Kritik sınır:** RTR kendi temporal+denoise'unu getirir. SADECE reflection path'inde kalır;
  diffuse GI tek-loop DDGI olarak DOKUNULMAZ. Diffuse'a reservoir/denoise SIZDIRMA.
- Hedef: `Dx12ReflectionsPass.cs` + yeni `Rtr*.hlsl`; inline RayQuery'ye port (kajiya .rgen → inline).

## K5 — ReSTIR → DDGI relight (EN SON, OPSİYONEL, en dikkatli)
- DDGI stabil olunca: probe relight ışınlarını (`DdgiRelight.hlsl`) ReSTIR reservoir ile seç →
  az ışınla daha temiz probe irradiance.
- **KUTSAL KURAL:** reservoir SADECE relight trace'inde, world-space probe atlas'ına yazılır.
  Ekran-uzayına ReSTIR temporal/denoise SOKULMAZ → tek-loop (probe EMA) felsefesi korunur.
  Bu, kajiya tekniğini DDGI mimarisine UYDURMAK demek; rtdgi pipeline'ını KOPYALAMAK değil.
- `inc/reservoir.hlsl` (saf math, binding yok) doğrudan alınır; temporal/spatial reuse mantığı
  probe-uzayına yeniden yazılır.
- Doğrulama: aynı ışın bütçesinde DDGI relight gürültüsü ↓; det capture byte-stable (reservoir RNG
  det'te sabit-seed); EMA loop sayısı 1 KALIR (regress testi).

## Port mekaniği (her K parçası için ortak)
- `[[vk::binding(N)]]` → DX12 `register(tN/uN/bN)` + root sig param (mevcut DDGI/refl root sig'e ekle).
- `.rgen.hlsl` (Vulkan ray-gen pipeline) → inline `RayQuery` (motor inline kullanıyor, ayrı RT PSO yok).
- kajiya-rg orchestration (Rust) → ilgili C# pass'a elle yeniden yaz (pass-graph event sistemi).
- Her parça AYRI commit + `BALLISTIC_DETERMINISTIC=1` byte-id + CarDemo raw doğrulama.
