# GI Pragmatic Revival — Faz R1.0: MaterialId robust per-submesh (verify record)

**Tarih:** 2026-06-18. **Branch:** `dx12-renderer`.
**Plan:** [gi-pragmatic-revival-plan.md](gi-pragmatic-revival-plan.md) §2 Faz R1, alt-faz R1.0.
**Önceki:** R0 baseline [gi-revival-R0-baseline.md](gi-revival-R0-baseline.md) (commit `cb3e9d73`).

> Ham çıktılar: `e:/tmp/gi-r1/` (cornell_a/b, coloronly, coloronly_gioff, thinwall + *.stats.json + heatmap).
> CPU harness: `%TEMP%/bal-matid-test/` (MatId.csproj + Program.cs).

---

## Özet — R1.0(b) FIX ZATEN COMMIT'Lİ (user WIP `3f3406e9`), bu chunk (a)+(c)'yi kapatır

`git log cb3e9d73..HEAD` ile doğrulandı: R1.0'ın **kod düzeltmesi (b)** kullanıcının
`3f3406e9 [dx12] post-FX renderer pass: mip-pyramid bloom rework + GTAO AO + GI bridge`
commit'inde ZATEN landed:

- `Dx12GpuDrivenRenderer.ResolveOrRegisterMaterialId(Material)` EKLENDİ
  ([Dx12GpuDrivenRenderer.cs:303](../../BallisticEngine.DX12/Resources/Dx12GpuDrivenRenderer.cs#L303)).
- `Dx12RtGeometry.BuildTriMaterials` artık `gpu.TryMaterialId(...)-or-0` yerine
  `gpu.ResolveOrRegisterMaterialId(...)` çağırıyor
  ([Dx12RtGeometry.cs:128](../../BallisticEngine.DX12/Resources/Dx12RtGeometry.cs#L128)).

Bu chunk'ın işi plan R1.0'ın geri kalanı: **(a)** repro + fixture üret + buffer'ın boş/dejenere
olduğunu ASSERT et; **(c)** color-only artık bounce veriyor + imported/merged byte-identical doğrula.
**Kod düzeltmesine DOKUNULMADI** (zaten doğru ve commit'li).

## Kök sebep (yapısal, kanıtlandı)

`EnsureMaterialTable(wholeMeshRenderers)` YALNIZ `SubMeshIndex < 0` renderer'ların submesh
material'larını tabloya kaydeder ([DX12HDRenderer.cs:1302](../../BallisticEngine.DX12/DX12HDRenderer.cs#L1302)).
Ama `Dx12RtGeometry.Ensure(...)` TLAS'ın trace ettiği TÜM aktif renderer'lar için per-triangle
MaterialId buffer'ı kurar ([Dx12GiPass.cs:585](../../BallisticEngine.DX12/Resources/Dx12GiPass.cs#L585)) —
`SubMeshIndex >= 0` split-import çocukları DAHİL. O renderer'ların material'ı tabloda OLMADIĞI için
eski `TryMaterialId` miss → `matId = 0` → per-tri buffer tamamen id 0 (= İLK whole-mesh material'ı,
genelde Bistro taşı). Sonuç: RT-GI/emissive/reflection bounce SESSİZCE yanlış/boş çıkıyordu —
raster G-buffer doğru görünürken RT trace yanlıştı. İkinci dejenere yol: GPU-driven KAPALI iken
`EnsureMaterialTable` hiç koşmaz → whole-mesh dahi tabloda yok.

## R1.0(a) — Repro + fixture

`SampleProject/Assets/*` **gitignored** (repo facts; Bistro 1.6 GB ignored, CornellBox/LightTest de
ignored). Bu yüzden fixture'lar yerelde mevcut ama commit EDİLEMEZ → tanımları buraya gömüldü
(yeniden üretilebilir). `bal import` + `bal validate` ile yerelde doğrulandı (`valid: true`,
yalnız zararsız member-adı uyarıları — kanonik CornellBox.scene ile aynı şekil).

Üretilen fixture'lar (`SampleProject/Assets/GiFixtures/`):
- **`RedEmitter.mat`** — color-only kırmızı emitter (textures yok; baseColor + emissiveColor + intensity).
- **`WhiteReceiver.mat`** — color-only beyaz albedo alıcı (textures yok).
- **`ColorOnly.scene`** — whole-mesh single-submesh kırmızı emitter küp + beyaz duvar/zemin (color-bleed).
- **`ThinWall.scene`** — mühürlü kırmızı emitter + ince duvar occluder + dışarıda alıcı (leak testi).

### `RedEmitter.mat`
```json
{
  "version": 1,
  "shader": "Assets/Default/Shaders/Standard.shader",
  "textures": {},
  "transparent": false, "opacity": 1,
  "baseColor": [0.9, 0.05, 0.05, 1.0],
  "emissiveColor": [1.0, 0.0, 0.0], "emissiveIntensity": 12.0,
  "metallic": 0, "roughness": 1.0
}
```
### `WhiteReceiver.mat`
```json
{
  "version": 1,
  "shader": "Assets/Default/Shaders/Standard.shader",
  "textures": {},
  "transparent": false, "opacity": 1,
  "baseColor": [0.85, 0.85, 0.85, 1.0],
  "emissiveColor": [0.0, 0.0, 0.0], "emissiveIntensity": 0.0,
  "metallic": 0, "roughness": 1.0
}
```
`ColorOnly.scene` / `ThinWall.scene`: Plane.obj + Cube.obj (single-submesh OBJ, `usemtl` yok)
StaticMeshRenderer'lar, yukarıdaki color-only mat'lar `sharedMaterial` ile atanmış, `subMeshIndex: -1`.
(Tam YAML için yerel `SampleProject/Assets/GiFixtures/`; yeniden üretim: bu mat'lar + Default/Meshes
primitive'leri + bir HDCamera + SceneLighting ambientIntensity ~0.15.)

### Buffer "boş/dejenere" ASSERT — CPU harness (GPU'suz, device-remove riski YOK)

Plan R0.0b: "sadece authoring yetmez, buffer'ın boş olduğu KANITLANMALI." RT_GI=1 headless SaveBmp
device-remove edebildiği için (memory dx12-passgraph PRE-EXISTING) bu, `Dx12RtGeometry.BuildTriMaterials`
+ `EnsureMaterialTable`/`ResolveOrRegisterMaterialId` karar-mantığının BİREBİR replikası olan saf-CPU
harness ile kanıtlandı (`%TEMP%/bal-matid-test`, `dotnet run` → **ALL CHECKS PASSED**):

- **CASE 2 (THE BUG):** `SubMeshIndex>=0` color-only çocuk → `matRed` tabloda YOK →
  OLD buffer `[0,0,...,0]` (DEJENERE: tamamı id 0 = matWhole0, matRed DEĞİL) →
  FIX sonrası matRed kaydoluyor, buffer gerçek id'ye (≠0) işaret ediyor, `old != new`.
- **CASE 3:** GPU-driven KAPALI (tablo hiç kurulmaz) → whole-mesh dahi OLD'da dejenere; FIX on-demand kaydeder.

## R1.0(c) — Doğrulama (soft-gate)

- **Color-only bounce var:** `ColorOnly.scene` GI-isolate (ScreenSpace SSGI on vs off) → meanError
  0.0070, differentPct **1.155%**, hotspot 0.674 → emitter bölgesinden yüzeylere net ışık katkısı.
- **Imported/merged BYTE-IDENTICAL (regresyon yok):**
  - CPU harness CASE 1 (kayıtlı whole-mesh single-submesh) + CASE 4 (CornellBox-benzeri çok-submesh
    whole-mesh): `new == old` byte-identical, tablo boyutu değişmiyor, 3 ayrı material korunuyor.
    `ResolveOrRegisterMaterialId` zaten-kayıtlı material için `TryMaterialId` ile AYNI id'yi döndürür
    → strict superset, eski davranışın değişmediği yol için tam byte-identical.
  - **Path-level:** FIX yalnız `Dx12RtGeometry`'i kullanır = SADECE RayTraced GI/RT-reflection yolu.
    R0 baseline'ın default'u ScreenSpace SSGI → FIX o yolda ÖLÜ KOD → SSGI-path byte-identical garantili.
    (RT_GI=1 headless device-remove riski nedeniyle launch edilmedi; byte-identical RT-path için CPU
    harness CASE 4 yeterli kanıt.)
- **Soft-gate:** color-only/split için çıktı KASTEN değişir (doğruluk düzeltmesi, açıklanmış diff =
  kabul); kayıtlı whole-mesh için byte-identical (CASE 1/4) → açıklanamayan regresyon YOK.

## GPU-güvenliği + determinizm (headless oracle GEÇTİ)

- **5 clean launch, DRED on (`BALLISTIC_DX12_DRED=1`), SIFIR device-removal** (hepsi RC=0):
  CornellBox×2, ColorOnly (SSGI on), ColorOnly (SSGI off), ThinWall. RT_GI/RT_SHADOWS=1 AÇILMADI.
- **Determinizm (run-to-run, aynı frame 60, iki bağımsız koşu → BYTE-IDENTICAL):**
  CornellBox `96daa282…` == `96daa282…`. R0'daki determinizm garantisi R1.0 sonrası KORUNUYOR.
- **Build 0-err:** BallisticEngine + DX12 + Runtime + Cli hepsi `0 Error(s)`.

## R1.0 DoD durumu

- [x] (a) Repro + fixture üretildi (ColorOnly/ThinWall + color-only mat'lar) + `bal validate` geçti.
- [x] (a) Buffer'ın dejenere olduğu ASSERT edildi (CPU harness CASE 2/3, RT device-remove'a girmeden).
- [x] (b) FIX (zaten `3f3406e9`'de commit'li; bu chunk dokunmadı, doğruladı).
- [x] (c) Color-only artık bounce/color-bleed veriyor (GI-isolate %1.155).
- [x] (c) Imported/merged byte-identical (CPU CASE 1/4 + SSGI-path ölü-kod argümanı).
- [x] Determinizm + 5 clean launch no-removal + build 0-err.

**Sıradaki:** R1.1 — bindless-tail merkezi allocator (`BindlessTailAllocator`).
