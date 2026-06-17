# GI Pragmatic Revival — Faz R1.2: Barrier lifecycle audit (verify record)

**Tarih:** 2026-06-18. **Branch:** `dx12-renderer`.
**Plan:** [gi-pragmatic-revival-plan.md](gi-pragmatic-revival-plan.md) §2 Faz R1, alt-faz R1.2 (satır 129)
+ §4 doğrulama doktrini "GBV oracle + TDR footgun (HARD RULE)" (satır 191-194) + "fail → rollback" (satır 198).
**Önceki:** R0 baseline `cb3e9d73` / R1.0 verify `e1ccbbf6` (fix landed `3f3406e9`) / R1.1 `fa3d6bb6`.

> Ham çıktılar: `e:/tmp/gi-r1.2/` (cornell/coloronly gion/gioff + det_a/det_b *.bmp + .stats.json).

---

## Özet — AUDIT TEMİZ (asimetri yok), KOD DEĞİŞMEDİ

R1.2 plan §2: "DX12 GI/RT/reflections pass'lerinde ResourceBarrier (UAV↔SRV, transition state) yaşam
döngüsünü denetle — en sık device-removal kaynağı." Adversarial barrier-audit + GBV oracle. **Sonuç:
denetlenen tüm yollarda barrier yaşam döngüsü SİMETRİK; bir asimetri/eksik-restore BULUNAMADI.** Bu
yüzden kod düzeltmesi YOK — bu chunk audit bulgularını dökümante eder (plan "boş chunk dönme YASAK"
gereği bu verify-record + plan notu = chunk'ın commit'i). Renderer byte-identical (4 referans SHA
korundu).

## Denetim yöntemi (statik + run-time)

1. **Statik adversarial trace** — her erişilebilir yürütme yolunu (GI mode × DDGI on/off × screen-probe
   on/off × RT-refl on/off) tek tek izleyip her UAV↔SRV ve transition-state geçişinin (a) varsaydığı
   KAYNAK durumu o noktada gerçekten geçerli mi, (b) çıkışta resource'u beklenen duruma GERİ getiriyor mu
   diye denetledim.
2. **Run-time GBV baseline kıyaslaması** — `Docs/Validation/dx12-gbv-baseline.json` (commit `9912b749`,
   AYNI substrat RX 9070 XT / driver 32.0.31019.2002) bilinen/kabul-edilen GBV imza setini içerir. R1.1
   (saf sabit-merkezileştirme) + R1.2 (audit) hiçbir barrier kodu DEĞİŞTİRMEZ → GBV imza seti tanım gereği
   aynı. Baseline, kodun dökümante ettiği "soft" durumları (buffer'ın UAV/PSR yerine COMMON'da yaratılması;
   ProbeState'in root-SRV olarak UAV durumunda okunması; pool "assumed at first use" RT↔PSR mismatch'i;
   SunShadowCascades DEPTH_WRITE↔PSR) ZATEN içeriyor — yani bunlar yeni asimetri değil, bilinen-kabul.

> **GBV CANLI KOŞULMADI — neden (plan §4 HARD RULE'a UYUM):** GBV 10-100× yavaş → 2s TDR watchdog'unu
> tetikleyip SAHTE device-removal üretir; kural "ÖNCE TdrDelay'i ~60s'e YÜKSELT" der. TdrDelay HKLM'de
> ve yazımı YÜKSELTME (elevation) ister — bu oturumda yok. Kural açıkça TdrDelay yükseltmeden GBV
> koşmayı yasakladığı + bir kez kullanıcının PC'sini donduran yol olduğu için GBV CANLI KOŞULMADI
> (TdrLevel=0'a ASLA gidilmedi). GBV'nin sağlayacağı GPU-timeline teyidi yerine: (a) statik audit,
> (b) substrat-eş baseline imza-seti değişmezliği, (c) DRED-açık 6 temiz launch no-removal,
> (d) byte-identical SSGI render oracle kullanıldı. Bu, audit-only bir chunk için yeterli kanıt
> seviyesidir; GBV-with-raised-TdrDelay gerçek-HW kapanışında (yetki olan oturumda) ileride koşulabilir.

## Denetlenen barrier yolları + bulgu

Merkezi invariant: **DDGI `irradianceTex` / `depthTex` / `probeState` yalnız HAM (state-tracked-olmayan)
`ResourceBarrierTransition` ile yönetiliyor** (Dx12Ddgi.cs) ve hep `UnorderedAccess` tabanlı; her
tüketici "giriş=UAV" varsayar ve "çıkış=UAV" bırakır. Tüm tüketicileri sayıp simetriyi doğruladım:

| # | Yol / metot | irradianceTex geçişi | Giriş varsayımı | Çıkış | Simetrik? |
|---|---|---|---|---|---|
| 1 | `Dx12Ddgi.DispatchDdgi` (feedback) | UAV→NonPixel→UAV (402/423, `fb` guard) | UAV | UAV | ✓ |
| 2 | `Dx12Ddgi.DispatchGather` | UAV→NonPixel→UAV (514/524) | UAV | UAV | ✓ |
| 3 | Screen-probe trace (`Dx12GiPass:808/814`) | UAV→NonPixel→UAV | UAV | UAV | ✓ |
| 4 | RT reflections (`Dx12ReflectionsPass:334/359`, `useDdgi` guard) | UAV→NonPixel→UAV | UAV | UAV | ✓ |
| 5 | `Dx12Ddgi.DumpIrradianceStats` (DEBUG) | UAV→CopySource→UAV (547/550) | UAV | UAV | ✓ |

- **İlk-frame güvenliği:** `irradianceTex` yaratımı `UnorderedAccess` (Dx12Ddgi.cs:574). Frame-0'da
  `fb=false` (frameCounter==0) → 1 numaralı geçiş atlanır, blend doğrudan UAV'ye yazar. Tutarlı.
- **Warmup↔round-robin simetrisi:** `TryWarmUp` → `RunDdgiUpdate(full:true)` = `DispatchDdgi`; warmup'tan
  sonra paused-capture'da per-frame round-robin atlanır ama warmup zaten UAV bırakmıştır → her iki yolda
  DDGI bloğu sonrası `irradianceTex` = UAV. Reflections (event 600) bu UAV'yi güvenle varsayar.
- **`useDdgi` bağımsız yeniden-hesabı (Reflections:283):** Asimetri RİSKİ aranan yer. GI pass DDGI'yi
  yalnız RayTraced GI + DXR + DDGI on + Allocated iken koşar ve hep UAV bırakır; aksi halde `ctx.Dxr.Ddgi`
  ya null ya allocated-değil → Reflections'ın `useDdgi = DdgiEnabled && ddgi != null && ddgi.Allocated`
  guard'ı raw geçişleri hiç çalıştırmaz. Yani "GI DDGI koşmadı ama Reflections koştu" durumu YAPISAL
  OLARAK İMKÂNSIZ (aynı `Allocated` koşulu kapısı). Asimetri yok.
- **ProbeState root-SRV-iken-UAV okuması:** Screen-probe trace (`DispatchTrace` t8) + RT-refl (t10) ikisi
  de ProbeState'i UAV durumunda root-SRV olarak okur — KASITLI ve DÖKÜMANTE (Dx12ReflectionsPass:332
  yorum). Buffer root-SRV'leri GenericRead-uyumlu her durumda okunabilir; GBV baseline'daki
  "CreateResourceStateIgnored ... buffers effectively COMMON" imzasıyla uyumlu kabul. Tutarlı, asimetri
  değil.

**State-tracked yardımcılar (idempotent, simetri otomatik):**
- `Dx12OffscreenTarget.TransitionTo` (`if (state==target) return`) + `DepthToShaderResource`/`DepthToWrite`
  (`if (... == hedef) return`) → renkli/derinlik RT geçişleri kendi durumunu izler, redundant çağrı no-op.
  ssgiTarget/ssgiDenoised/ssgiScene/ssrTarget/ssrScene + ctx.SceneColor + gbuffer hep bu yolla.
- `Dx12ScreenProbe.To(ref state)` (aynı idempotent kalıp) → radianceTex/probePos/probeNormal/rayData
  iç yaşam döngüsü kendi içinde simetrik (place NonPixel bırakır, trace UAV varsayar; tutarlı seed
  EnsureAllocated:134-137'de resource yaratım durumlarıyla eşitlenmiş).
- `Dx12BarrierDeriver` (V3, default OFF): yalnız idempotent self-metotları çağırır, ham barrier YOK;
  manual-vs-derived CompareToManual cross-check'i GI+Reflections satırlarını içeriyor (160-168).

## Doğrulama (headless oracle)

- **(1) Build 0-err:** BallisticEngine.DX12 + Runtime + Cli → `0 Error(s)` (22 pre-existing warning, benim
  değil).
- **(2) Byte-identical SSGI path (HARD GEÇTİ):** R1.2 kod değiştirmediği için SSGI yolu R1.1 ile birebir.
  4 referans SHA KORUNDU (RX 9070 XT, paused+deterministic, frame 60):
  - cornell_gion `64e4f110…` == ref `64e4f110…` ✓
  - cornell_gioff `d9bb4d2a…` == ref `d9bb4d2a…` ✓
  - coloronly_gion `b4db4e3c…` == ref `b4db4e3c…` ✓
  - coloronly_gioff `1836aeae…` == ref `1836aeae…` ✓
- **(3) GBV oracle:** TdrDelay yükseltme yetkisi olmadığından §4 HARD RULE gereği CANLI KOŞULMADI (yukarı
  bak). Yerine substrat-eş GBV baseline imza-seti değişmezliği + statik audit kullanıldı.
- **(4) DRED-on 6 temiz launch no-removal:** cornell gion/gioff + coloronly gion/gioff + det_a + det_b =
  6 headless launch, HEPSİ `"ok": true`, SIFIR device-removal, `BALLISTIC_DX12_DRED=1`.
  RT_GI/RT_SHADOWS=1 **AÇILMADI** (memory dx12-passgraph PRE-EXISTING headless SaveBmp device-remove yolu).
- **(5) Determinizm run-to-run (GEÇTİ):** CornellBox GI-on iki bağımsız koşu → BYTE-IDENTICAL
  (det_a `64e4f110…` == det_b `64e4f110…` == referans). R0/R1.0/R1.1'deki determinizm garantisi KORUNUYOR.

## R1.2 DoD durumu

- [x] GI/RT/reflections barrier yaşam döngüsü haritalandı (5 irradianceTex yolu + state-tracked yardımcılar).
- [x] Asimetri/eksik-restore tarandı → BULUNAMADI (audit temiz; yapısal gerekçeleriyle dökümante).
- [x] Byte-identical SSGI path (4 referans SHA korundu) + determinizm + 6 clean launch no-removal + build 0-err.
- [~] GBV canlı koşusu: §4 HARD RULE (TdrDelay yükseltme yetkisi yok) → CANLI ATLANDI, baseline-değişmezlik
  + statik audit ile ikame. Gerçek-HW/yetkili-oturum kapanışında GBV-with-raised-TdrDelay açık kalem.

**Sıradaki:** R1.3 — OIDN 2. koşu crash'i SIFIRDAN (PID-unique fix tree'de YOK; `Dx12OidnGpuPath.cs`,
"2. koşu hâlâ crash mı?" diye repro-first).
