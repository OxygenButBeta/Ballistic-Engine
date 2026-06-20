# Lumen Fidelity Overhaul — kill the wandering blobs / firefly boil

**Branch:** `dx12-perf-radical` (mevcut) · **Tarih:** 2026-06-20
**Sahne:** `SampleProject/Assets/Bistro_v5_2/BistroExterior.scene` (karanlık dış mekân = en zorlu)

## Teşhis (bu chat'te kanıtlandı, tahmin değil)

Canlı + headless A/B ölçümleriyle elenen hipotezler:

| Test | Sonuç |
|---|---|
| `SCREENPROBE=0` (per-pixel) | Bloblar GİTTİ → kaynak **screen-probe**. Ama firefly + büyük perf maliyeti geldi. |
| `EMA=0.02` (max card accumulation) | Bloblar durmadı → card relight stride **değil**. |
| `BUDGET=0 PRIORITY=0` (her frame tüm card) | Bloblar durmadı → card budget **değil**. |
| `PROBE_EMA=0.02` (max probe accumulation) | Bloblar durmadı → probe temporal EMA tek başına yetersiz. |
| `FREEZE_JITTER=1` (ray jitter sabit) | Bloblar **dondu** (hareketsizken) → kaynak **per-frame ray jitter varyansı**. |
| `DETERMINISTIC=1 + DEBUG=1`, headless ardışık E diff | indirect E flicker = **0.000 mean** → GI kendi başına flicker etmiyor; TAA/jitter taşıyor. |
| `DETERMINISTIC=1` canlı (TAA off) | **Çıplak firefly noise her yere yayılmış** → TAA bunu gizliyor, normal modda "gezen blob"a çeviriyor. |
| `FIREFLY=2 + DENOISE=4` (TAA off) | Firefly azaldı ama **bitmedi**; renkli (color-bleed) firefly + "minicik harekette allak bullak". |

**Kök neden (birleşik, tek yama çözmez):**
1. **Few-ray trace gürültüsü** — probe başına oct²≈36 yön, integrate'te pixel başına etkin az örnek → yüksek varyans, özellikle KARANLIK alanda (sinyal≈gürültü → tek parlak ray patlıyor).
2. **Denoise yetersiz** — ortak à-trous **1 pass, radius 2** (`LumenGi.hlsl:CSDenoise`). Bu firefly yoğunluğunu temizleyemiyor.
3. **Temporal pass kendi içinde çelişkili** (`LumenTemporal.hlsl`) — AABB clamp KASITLI gevşek (`*1.5 + nmean*0.25`, satır 79) ki few-ray gürültüsünü re-inject etmesin; ama gevşek clamp **boiling'i de yakalayamıyor**. Çözülemez gerilim: girdi temizlenmeden clamp ayarlanamaz.
4. **Disocclusion reject** (`trust`, satır 66) minik harekette bile history atıyor → "allak bullak".

VXGI bu spesifik problemleri (özellikle #4) doğası gereği yaşamaz AMA kullanıcı **önce Lumen overhaul** istedi; RT-reflection + surface-card altyapısı korunacak.

## Strateji: gürültüyü KAYNAĞINDA düşür, sonra temporal'ı sıkılaştırabil

Sıra önemli — her adım bir öncekinin üzerine ölçülerek inşa edilir. Her adımdan sonra
`DETERMINISTIC=1` (TAA off, çıplak) + normal mod, headless ardışık-E diff + canlı bakış.

### Adım 0 — Teşhis temizliği (önce)
- Geçici door'ları kaldır: `BALLISTIC_DX12_LUMEN_DBG_HIST` log'u, `BALLISTIC_DX12_LUMEN_FREEZE_JITTER`.
- `FireflyClamp` + `SpPad2` (door `BALLISTIC_DX12_LUMEN_FIREFLY`, default 8) **kalsın** — işe yaradı, zarar yok; default'u Adım 2'de ayarlanacak.
- Build temiz (obj wipe + re-embed, `dx12-shader-edit-build-gotcha`).

### Adım 1 — Cosine-importance sampling (varyansı kaynakta yarıya indir, ray EKLEMEDEN)
- `CSProbeTrace` şu an oct cell merkezine **uniform** ray atıyor (`OctDecode(octUv)`, satır ~353).
- Karanlık alandaki varyansın yarısı uniform sampling'den: çoğu ray hiçbir şeye çarpmıyor, biri patlıyor.
- **Cosine-weighted hemisphere sampling**: ray yönlerini probe normaline göre cosine dağıt → katkısı yüksek yönlere daha çok örnek → **aynı ray sayısıyla daha düşük varyans**. Octahedral tile zaten full-sphere; sadece jitter dağılımını cosine'e çevir.
- Maliyet: sıfır ek ray. Beklenen: firefly yoğunluğu belirgin düşer.

### Adım 2 — Firefly clamp'i adaptif + default agresifleştir
- Şu an sabit luminance tavanı (8). Karanlıkta bu çok gevşek (sinyal 1e-3, tavan 8 = hiç bitmez).
- **Komşu-bağıl clamp**: tavanı probe'un kendi tile-mean'inin katı yap (örn. `max(localMean * 4, floor)`), böylece karanlıkta da bağlar, parlakta gerçek bounce'u kesmez.
- Default'u `FIREFLY` door'undan ölç-ayarla (muhtemelen ~3-4 sabit eşdeğeri).

### Adım 3 — à-trous denoise'i GERÇEK SVGF-lite yap
- Pass sayısını **1 → 3-5** çıkar (her pass step×2: 1,2,4,8,16 → geniş ama edge-aware).
- **Variance-guided**: per-pixel luminance varyansını (komşu spread) hesapla, denoise gücünü varyansa bağla → düz/gürültülü yerde geniş, edge'de dar.
- Karanlık alan için **relative (luminance-normalized) edge weight** — mutlak fark karanlıkta zayıf kalıyor.
- Bu, temporal'a giren E'yi gerçekten temiz yapar → Adım 4'ün ön koşulu.

### Adım 4 — Temporal pass'i sadeleştir + sıkılaştır (girdi temizken artık güvenli)
- E artık temiz olduğu için `LumenTemporal.hlsl`'deki **gevşek AABB clamp** (satır 79) artık SIKILABİLİR (`*1.5+nmean*0.25` → `*1.0` veya daha sıkı) — gürültü re-inject riski kalktı.
- **Disocclusion reject'i yumuşat** ("minicik harekette allak bullak" fix): hard `trust` yerine, reject olunca history'yi atmak yerine **komşu-clamp'lenmiş** history'ye düş (Adım 3'ün temiz E'si bunu mümkün kılar). Reprojection world-space depth+normal ile.
- Rate-limit (satır 106) muhtemelen artık gereksiz → kaldır/sadeleştir (karmaşıklık = bakım borcu).

### Adım 5 — Doğrulama
- **Byte-test**: `DETERMINISTIC=1` golden capture — her adım sonrası `bal render` SHA. Adım 1-2 golden'ı değiştirir (kasıtlı, daha temiz); Adım 0 byte-identical olmalı.
- **Headless flicker metriği**: play-mode ardışık E diff (mean error) → her adımda düşmeli.
- **Canlı**: kullanıcı Bistro'da hareketli + karanlık alan testi (asıl kabul kriteri).
- **Perf**: `bal perf` + per-pass GPU ms — denoise pass artışı bütçeyi ne kadar yiyor ölç; kabul edilemezse pass sayısı ölç-ayarla.

## Dosyalar
- `BallisticEngine.DX12/Shaders/LumenScreenProbe.hlsl` — CSProbeTrace (cosine sampling, firefly), CSProbeFilter.
- `BallisticEngine.DX12/Shaders/LumenGi.hlsl` — CSDenoise (SVGF-lite).
- `BallisticEngine.DX12/Shaders/LumenTemporal.hlsl` — clamp/reject sadeleştirme.
- `BallisticEngine.DX12/Lumen/Dx12LumenGiPass.cs` — denoise pass sayısı, door temizliği, CB alanları.

## Kabul kriteri
Bistro'da, KARANLIK alanda, kamerayı yavaş gezdirirken: gezen blob / firefly boil GÖZLE GÖRÜLÜR
şekilde yok (veya kabul edilebilir). Perf maliyeti `bal perf` ile ölçülmüş ve kullanıcı onaylı.
Bu kriter tutmazsa → VXGI prototipi (kullanıcının ilk sezgisi) ayrı worktree'de değerlendirilir.

## Geri çekme
Her adım ayrı commit. Golden SHA + flicker metriği her commit'te kayıtlı. Bir adım gerilettiyse
o commit revert. Firefly/clamp door'ları A/B için kalır.
