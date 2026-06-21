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
