# DDGI Radiance Cache — Lumen'i SÖK, sıfırdan ayrı klasörde yaz

> **Durum:** PLAN (kod yok). Onay bekliyor.
> **Branch:** `dx12-perf-radical` üzerinde yeni `ddgi-rewrite` (worktree önerilir).
> **Kullanıcı kararı (net):**
> 1. Mevcut Lumen GI **rendering'den TAMAMEN sökülür** (door arkasına değil — gerçekten çıkar, emekliye ayrılır).
> 2. Yeni DDGI **sıfırdan, kendi ayrı klasöründe, ayrı dosyalarda** yazılır.
> 3. Eski sistemin bir parçası kullanılacaksa bile **yeni klasöre ayrı kopyalanır** — yeni kod hiçbir eski
>    Lumen dosyasına bağımlı olmaz.
>
> **Tek öngörülebilirlik ilkesi:** GI dünya-uzayı, view-independent bir cache'te yaşar; ekran-uzayı
> temporal/denoise YOK → tek feedback loop (probe irradiance EMA).

---

## 0. Neden (kök neden, tek cümle)

Mevcut Lumen çıktısı **5 bağımsız temporal feedback loop'un çarpımı** (card EMA + probe-grid EMA + per-pixel
alpha + SVGF temporal + motion-vector resolve) → tahmin edilemez, 57 env door, 3 history flag, ~4000 satır.
DDGI'da **tek loop** vardır, çıktı kameradan bağımsızdır → ghosting/disocclusion/SVGF sınıfı doğmadan ölür.

---

## 1. Klasör düzeni (yeni, izole)

```
BallisticEngine.DX12/
  Ddgi/                         ← YENİ, kendi klasörü, eski Lumen'e SIFIR bağımlılık
    Dx12DdgiPass.cs             ← tek product-facing pass (event 500)
    Dx12DdgiProbeGrid.cs        ← probe grid state: atlas alloc, dünya AABB→grid, EMA buffer'ları
    Dx12DdgiShading.cs          ← (gerekirse) RT-hit shading C# tarafı / constants
    Shaders/
      DdgiCommon.hlsl           ← probe oct enc/dec, grid index, trilinear+Chebyshev ağırlık (paylaşılan)
      DdgiRelight.hlsl          ← Pass 1: per-probe RT trace + shading + EMA (card-light'tan KOPYALANAN shading)
      DdgiSample.hlsl           ← Pass 2: full-res gather (8-probe trilinear + visibility)
      DdgiCombine.hlsl          ← Pass 3: E*albedo*ao/PI → HDR (One/One)
```

- **HLSL'ler `Ddgi/Shaders/` altında embedded resource** olur (mevcut Lumen .hlsl `Shaders/`'da; DDGI ayrı
  klasörde → karışmaz). Embed yolu csproj'da doğrulanır ([[dx12-shader-edit-build-gotcha]]).
- **Kopyalanan parça:** `LumenCardLight.hlsl` satır 211–279'daki RT-hit shading çekirdeği (sun+shadow-ray +
  punctual + emissive + sky + multi-bounce gather + EMA) → `DdgiRelight.hlsl`'e taşınıp sadeleştirilir.
  Orijinal dosyaya referans YOK, kopya.

---

## 2. Doğrudan kullanılan PAYLAŞILAN motor altyapısı (Lumen'e ait DEĞİL — kopyalanmaz)

Bunlar zaten genel DX12 altyapısı, Lumen'in malı değil; DDGI doğrudan kullanır:

| Altyapı | Nereden | Rol |
|---|---|---|
| BLAS/TLAS | `ctx.Dxr.SceneAS` | Probe ray trace hedefi (inline RayQuery). DDGI kendi AS kurmaz. |
| Bindless geo/material SRV | `Dx12RtGeometry` / `RtInstance[]` | RT hit'te albedo/normal/emissive. |
| N-buffered constants | `Dx12FrameCb<T>` | Probe grid + sun constants (P0b overlap güvenli). |
| Pass eventi | `Dx12RenderPassEvent.GlobalIllumination` (500) | Aynı slot (Lumen sökülünce boşalır). |
| HW-RT gate | `ctx.Dev.HasHardwareRayTracing` | Gizli SSGI fallback YOK. |

> Ayrım kuralı: **`Dx12Lumen*` ile başlayan hiçbir tipe DDGI dokunmaz.** `ctx.Dxr`, `Dx12RtGeometry`,
> `Dx12FrameCb`, `Dx12OffscreenTarget` gibi genel tipler serbest (bunlar Lumen değil).

---

## 3. SÖKÜM — Lumen'in rendering'den tam çıkarılması (somut dosya:satır)

Bu, kaynak ajan envanterinden çıkarılan kesim listesi. **D6'da değil, EN BAŞTA (D0) yapılır** — temiz zemin.

### 3.1 Pass kaydı (kaldır)
- `DX12HDRenderer.cs:239` — `Dx12LumenGiPass lumenGiPass` field **sil**.
- `DX12HDRenderer.cs:795` — `new Dx12LumenGiPass(...)` **sil** → yerine `new Dx12DdgiPass(...)`.
- `DX12HDRenderer.cs:796` — `graph.Add(lumenGiPass)` **sil** → `graph.Add(ddgiPass)`.

### 3.2 FrameContext / orchestrator flag'leri
- `Dx12FrameContext.cs:152` — `LumenActiveThisFrame` → **yeniden adlandır** `GiActiveThisFrame` (anlam
  korunur, isim Lumen'den arınır; deferred + reflections bunu okur).
- `DX12HDRenderer.cs:2400` — `ctx.LumenActiveThisFrame = Dx12LumenGiPass.WouldRun(ctx)` →
  `ctx.GiActiveThisFrame = Dx12DdgiPass.WouldRun(ctx)`.
- `DX12HDRenderer.cs:2376` — `LumenScene = lumenGiPass.Scene` → **sil** (DDGI ayrı köprü kurar, §3.4).

### 3.3 Deferred IBL suppress köprüsü (KORUNUR, isim değişir)
- `Dx12DeferredLightingPass.cs:198` — `... && !ctx.LumenActiveThisFrame` → `... && !ctx.GiActiveThisFrame`.
  Çift sayım önleme mantığı **aynen kalır** (DDGI de diffuse indirect ekliyor).

### 3.4 Reflections köprüsü
- `Dx12ReflectionsPass.cs:385` — `ctx.LumenScene is { Valid: true } && ctx.PostFX.LumenReflections` →
  DDGI'da reflections probe irradiance'ı örnekler. **D5'e ertelenir.** İlk fazda reflections IBL fallback'e
  düşer (Lumen scene null → mevcut kod zaten bunu kaldırır), GI'sız reflection — kabul edilebilir ara durum.
- `ctx.LumenScene` field → DDGI gerek duyarsa `ctx.DdgiGrid` olarak ayrı eklenir (eski field silinir).

### 3.5 Volume köprüsü (KORUNUR — kullanıcıya görünen ayar kırılmasın)
- `Engine/Rendering/Volumes/Components/GlobalIllumination.cs` — component **aynen kalır** (enabled/intensity/
  skyIntensity/rayCount + tier). DDGI bu alanları okur.
- `VolumePostProcessing.cs:115–133` — `LumenEnabled/Intensity/...` mapping **aynen kalır** (PostFX alan
  adları korunur ki `.volume` asset'leri ve serileştirme kırılmasın).
- `PostProcessSettings.cs:275` — `LumenEnabled` field **kalır** (DDGI `Armed()` bunu okur). İç isim "Lumen"
  kalsa da kullanıcıya etkisi yok; istenirse ayrı bir rename PR'ı.

### 3.6 Silinen Lumen dosyaları (D6'da, build yeşil olunca)
- `Lumen/Dx12LumenGiPass.cs`, `Lumen/Dx12LumenScene.cs`, `Lumen/Dx12LumenCluster.cs` → **klasör tamamen sil**.
- `Shaders/LumenGi.hlsl`, `LumenCardLight.hlsl`, `LumenScreenProbe.hlsl`, `LumenSvgf.hlsl`,
  `LumenTemporal.hlsl` → **sil**.
- `BALLISTIC_DX12_LUMEN*` env door referansları (57 adet) → DDGI door'ları (≤8) ile değişir.

> **Not:** §3.6'yı en sona bırakıyoruz ki DDGI çalışana kadar eski shading kodunu **referans** olarak
> (kopyalama kaynağı) açık tutabilelim. Sökmenin *rendering bağlantıları* (§3.1-3.4) ise D0'da kesilir.

---

## 4. DDGI mimarisi (sıfırdan, sade)

### 4.1 Probe grid (`Dx12DdgiProbeGrid.cs`)
- Dünya AABB'sini (`RuntimeSet` bounds) düzgün 3B ızgaraya böl. Default `16×8×16` (2048 probe).
- İki persistent atlas (oktahedral), pass-owned, cross-frame (NEVER pooled — tek "cache"):
  - **Irradiance atlas:** probe başına `6×6` (+1px gutter → `8×8`) RGB.
  - **Visibility atlas:** probe başına `14×14` (+gutter → `16×16`) `R16G16` (mean depth, mean depth²) → Chebyshev.
- Önceki-frame irradiance ayrı buffer (multi-bounce feedback + EMA kaynağı).

### 4.2 Pass 1 — Relight (`DdgiRelight.hlsl`)
- Dispatch: probe başına 1 grup, grup içinde N ray (default 64; tier 32/128).
- Ray: Fibonacci hemisfer, frame-rotated jitter (**deterministic capture'da OFF**).
- Inline `RayQuery` → TLAS:
  - **Hit:** card-light'tan kopyalanan shading (sun+shadow-ray + punctual + emissive) × bindless albedo
    **+ kaynak probe'un önceki-frame irradiance'ı** (ucuz multi-bounce, ekstra ray yok).
  - **Miss:** sky/IBL (fp16 clamp — [[CLAUDE.md]] sky Inf gotcha).
- Oktahedral hücreye topla → **tek EMA**: `irr_t = lerp(irr_{t-1}, new, α)` (α default 0.05, hysteresis).
  Visibility atlas aynı ray depth'lerinden EMA.
- Reprojection/motion-vector YOK (probe dünya-sabit).

### 4.3 Pass 2 — Sample (`DdgiSample.hlsl`)
- Ekran pikseli (G-buffer world pos + normal) → saran hücrenin 8 köşesi:
  - ağırlık = **trilinear** × **Chebyshev visibility** (sızıntı önleme) × **normal backface**.
  - ağırlıklı irradiance topla → `indirect E` (full-res RGBA16F). **Denoise yok.**

### 4.4 Pass 3 — Combine (`DdgiCombine.hlsl`)
- `E * albedo * ao / PI` additive One/One. GTAO opsiyonel. Debug door: raw E (OPAQUE replace).
- Deferred zaten `GiActiveThisFrame` → `UseIBLDiffuse=0` → çift sayım yok.

---

## 5. Env door'lar (≤8)

| Door | Default | Rol |
|---|---|---|
| `BALLISTIC_DX12_DDGI` | follow volume | on/off/follow |
| `BALLISTIC_DX12_DDGI_RAYS` | 64 | probe başına ray |
| `BALLISTIC_DX12_DDGI_GRID` | 16x8x16 | grid çözünürlüğü |
| `BALLISTIC_DX12_DDGI_ALPHA` | 0.05 | EMA hızı |
| `BALLISTIC_DX12_DDGI_INTENSITY` | 1.0 | GI şiddeti |
| `BALLISTIC_DX12_DDGI_DEBUG` | 0 | raw E / probe viz |
| `BALLISTIC_DX12_DDGI_NOBOUNCE` | 0 | multi-bounce kapat |
| `BALLISTIC_DX12_DDGI_NOVIS` | 0 | Chebyshev kapat (A/B) |

---

## 6. İmplementasyon aşamaları (her biri ayrı commit, doğrulanabilir)

| Aşama | İçerik | Doğrulama |
|---|---|---|
| **D0** | Lumen rendering bağlantılarını KES (§3.1-3.4) + boş `Dx12DdgiPass` iskeleti + door + `Ddgi/` klasörü. DDGI hiçbir şey çizmez (no-op). Eski Lumen dosyaları henüz diskte (referans), ama pass listesinden çıkık. | Build temiz. Door off → byte-identical no-GI frame. Door on → siyah, crash yok. |
| **D1** | Probe grid alloc + Relight: per-probe RT trace + EMA (visibility YOK, bounce YOK). | Kapalı oda kapalı; renkli duvar bleed; `bal render` deterministik. |
| **D2** | Sample + Combine: trilinear gather (Chebyshev YOK). | Görsel GI görünür; sızıntı var (beklenen). |
| **D3** | Visibility atlas + Chebyshev. | İnce duvar sızıntı YOK; `bal query visibility` teyit. |
| **D4** | Multi-bounce (önceki-frame feedback) + hysteresis EMA. | Çok-zıplama parlaklığı; statik kamerada yakınsar, hitch yok. |
| **D5** | Reflections köprüsü (rough → probe), `ctx.DdgiGrid`. | RT reflection rough yüzeyde GI ile tutarlı. |
| **D6** | Eski Lumen dosyalarını SİL (§3.6) + door temizliği. | Build temiz; `Dx12Lumen*` ve `Lumen*.hlsl` yok; satır/door hedefte. |

**Her aşama:** `BALLISTIC_DETERMINISTIC=1` + `BALLISTIC_SCREENSHOT_PAUSED=1` golden byte-id testi (jitter/EMA
deterministik capture'da off). `dotnet build BallisticEngine.slnx` temiz.

---

## 7. Riskler / gotcha

- **`.hlsl` re-embed** ([[dx12-shader-edit-build-gotcha]]): incremental build re-embed etmez → `obj/` temizle.
- **SM6.6 heap sırası** ([[dx12-bindless-heap-order-hang]]): `SetDescriptorHeaps` root-sig'den ÖNCE.
- **NaN scrub ternary** (CLAUDE.md): `lerp(v,0,flag)` değil component select.
- **P0b overlap:** atlas cross-frame; realloc `WaitForFrame` ardından.
- **D2'de sızıntı normal** (Chebyshev D3'te) — panikleme.
- **D5'e kadar reflections GI'sız** — kabul edilebilir ara durum.

---

## 8. Beklenen kazanç

| Metrik | Lumen | DDGI |
|---|---|---|
| Aktif kod | ~4000 satır | ~1200-1500 |
| Buffer | 17 | ~4 |
| Env door | 57 | ≤8 |
| Temporal loop | 5 | 1 |
| History flag | 3 | 1 |
| Ghosting/disocclusion | var | yok |
| Klasör izolasyonu | Lumen rendering'e dağılmış | tek `Ddgi/` klasörü |
