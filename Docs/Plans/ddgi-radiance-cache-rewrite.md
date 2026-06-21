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

## Aşamalar
D0 söküm+iskelet(no-op) → D1 relight(RT+EMA) → D2 sample+combine → D3 Chebyshev → D4 multi-bounce → D5 reflections → D6 eski Lumen sil.
Her aşama: BALLISTIC_DETERMINISTIC=1 byte-id, dotnet build temiz.

## Door (≤8)
DDGI(follow volume) / DDGI_RAYS(64) / DDGI_GRID(16x8x16) / DDGI_ALPHA(0.05) / DDGI_INTENSITY(1) /
DDGI_DEBUG(0) / DDGI_NOBOUNCE(0) / DDGI_NOVIS(0).

## Gotcha
.hlsl re-embed (obj temizle) / SM6.6 heap sırası (SetDescriptorHeaps önce) / NaN ternary / P0b atlas realloc WaitForFrame.
