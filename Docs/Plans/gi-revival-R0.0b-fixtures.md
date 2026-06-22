# GI Pragmatic Revival — Faz R0.0b: 5 fixture scenes + `bal validate` (record + reproducible defs)

**Tarih:** 2026-06-18. **Branch:** `dx12-renderer`.
**Plan:** [gi-pragmatic-revival-plan.md](gi-pragmatic-revival-plan.md) §2 R0.0b satırı + §4 verification gates.
**Önceki:** R0.0a re-ground (commit `ef7f28c1`, plan .md only). R1.0 [gi-revival-R1.0-materialid.md](gi-revival-R1.0-materialid.md) (commit `e1ccbbf6`).

> **Neden bu doc var:** `SampleProject/Assets/*` **gitignored** (CLAUDE.md repo facts; `git check-ignore` rc=0).
> Fixture `.scene`/`.mat`/`.cs` dosyaları yerelde mevcut ama **commit edilemez** → tanımları (yeniden üretilebilir)
> buraya gömüldü. Bir sonraki temiz işçi `SampleProject/Assets/GiFixtures/` boşsa bu doc'tan aynısını üretir.

---

## Özet

R0.0b plan §2: **5 fixture sahne kur + her birine `bal validate` çalıştır.** R1.0 (`e1ccbbf6`) 2 tanesini
(`ColorOnly.scene`=scene-3, `ThinWall.scene`=scene-4) + `RedEmitter.mat`/`WhiteReceiver.mat`'ı zaten üretmişti.
Bu chunk eksik 3'ü (scene-1 outdoor, scene-2 multi-light interior, scene-5 moving-light) + `LightMover.cs`
script'ini ekledi, R1.0'ın iki sahnesinin member-adı uyarılarını temizledi, ve **R0.0b'nin iki kritik
assert'ini** kanıtladı.

Tüm 5 fixture `bal validate` → **EXIT=0, `valid: true`** (3 yeni sahne `issueCount: 0` warning-clean;
ColorOnly/ThinWall artık temizlenmiş — eski `sunIntensity`/HDCamera-FOV inert member uyarıları kaldırıldı).

### Fixture eşlemesi (plan §2 R0.0b)

| # | Plan rolü | Dosya | Whole-mesh renderer (SubMeshIndex<0) | Not |
|---|---|---|---|---|
| 1 | outdoor (cascaded shadow) | `GiFixtures/Outdoor.scene` | 5 | DirectionalLight grazing sun + ground + 3 prop |
| 2 | multi-light interior (≥8 punctual) | `GiFixtures/MultiLightInterior.scene` | 7 | enclosed room + 8 PointLight |
| 3 | whole-mesh single-submesh color-only emissive+albedo | `GiFixtures/ColorOnly.scene` | 3 | R1.0 fixture (temizlendi) |
| 4 | thin-wall (leak) | `GiFixtures/ThinWall.scene` | 3 | R1.0 fixture (temizlendi) |
| 5 | moving-light (two-rate latency) | `GiFixtures/MovingLight.scene` | 4 | PointLight + `LightMover` sweep script |

## R0.0b kritik ASSERT'ler (DoD)

### Assert 1 — Scene-3 MaterialId buffer GERÇEKTEN dejenere (EMPTY)

R1.0'ın saf-CPU harness'i (`%TEMP%/bal-matid-test`, `dotnet run -c Release` → **ALL CHECKS PASSED, EXIT=0**)
bu chunk'ta **PROVISIONAL POLICY ile yeniden doğrulandı**: harness'in replike ettiği karar-mantığı tree'deki
gerçek metodlarla birebir eşleşiyor (re-grep edildi):
- `Dx12GpuDrivenRenderer.EnsureMaterialTable` (cs:242) sadece whole-mesh listesini kaydeder.
- `RegisterMaterial` (cs:269-270) null/transparent/already-present/full skip.
- `ResolveOrRegisterMaterialId` (cs:303-308) -1 for null/transparent, else lookup-or-register.
- `TryMaterialId` (cs:316-319) lookup, null-miss → id 0.
- `Dx12RtGeometry.BuildTriMaterials` (cs:121-132) `ResolveOrRegisterMaterialId` + `matId<0→0` + tri-range fill.

Harness CASE 2 (Scene-3 color-only split-child bug): **OLD buffer `[0,0,...,0]` DEJENERE** (tamamı matId 0 =
matWhole0, matRed DEĞİL) → R1.0 öncesi color-only bounce sessizce boştu. FIX sonrası gerçek matRed id'ye işaret
ediyor (`old != new`). **Assert GEÇTİ.** (RT_GI=1 GPU yoluna GİRİLMEDEN — device-remove riski yok, plan §4.)

### Assert 2 — Scene-1/2 ≥1 whole-mesh renderer (SubMeshIndex<0) present

`grep "SubMeshIndex: -1"`: Outdoor=5, MultiLightInterior=7 (her ikisi ≥1). **Assert GEÇTİ.**
(Bu olmadan whole-mesh GI bug'ı R2'ye kadar görünmezdi.)

## `bal validate` çıktısı (oracle)

```
Outdoor:            EXIT=0, issueCount=0, valid=true
MultiLightInterior: EXIT=0, issueCount=0, valid=true
ColorOnly:          EXIT=0, issueCount=0, valid=true
ThinWall:           EXIT=0, issueCount=0, valid=true
MovingLight:        EXIT=0, issueCount=0, valid=true
```

> `LightMover` component'inin resolve olması için `Library/ScriptAssemblies/GameScripts.dll` rebuild edildi
> (`dotnet build SampleProject/Scripts.csproj -p:BallisticEngineDir=<Runtime bin>`); `Assets\**\*.cs` glob'u
> `LightMover.cs`'i alır. Game script bare `Vector3` için `using System.Numerics;` gerekir (engine global-using;
> game asm değil).

---

## Yeniden üretim — fixture tanımları (gitignore yüzünden buraya gömülü)

Konum: `SampleProject/Assets/GiFixtures/`. Bağımlılıklar: `Assets/Default/Meshes/{Plane,Cube,Sphere}.obj`
(single-submesh OBJ, `usemtl` yok), `Assets/Default/Shaders/Standard.shader`.

### `RedEmitter.mat` (R1.0, color-only kırmızı emitter)
```json
{
  "version": 1, "shader": "Assets/Default/Shaders/Standard.shader", "textures": {},
  "transparent": false, "opacity": 1,
  "baseColor": [0.9, 0.05, 0.05, 1.0],
  "emissiveColor": [1.0, 0.0, 0.0], "emissiveIntensity": 12.0,
  "metallic": 0, "roughness": 1.0
}
```

### `WhiteReceiver.mat` (R1.0, color-only beyaz albedo alıcı)
```json
{
  "version": 1, "shader": "Assets/Default/Shaders/Standard.shader", "textures": {},
  "transparent": false, "opacity": 1,
  "baseColor": [0.85, 0.85, 0.85, 1.0],
  "emissiveColor": [0.0, 0.0, 0.0], "emissiveIntensity": 0.0,
  "metallic": 0, "roughness": 1.0
}
```

### `LightMover.cs` (scene-5 moving-light driver)
```csharp
using System.Numerics;
using BallisticEngine;
namespace Game;
// Triangle-wave transform sweep (deterministic, self-clocked) so a parented light physically moves
// in play mode for the two-rate latency test. Lifecycle overrides MUST be `protected` (game-asm rule).
public sealed class LightMover : Behaviour {
    public float Amplitude { get; set; } = 2.5f;   // world-units to each side
    public float Period    { get; set; } = 4f;     // seconds per full back-and-forth
    public int   Axis      { get; set; } = 0;       // 0=X 1=Y 2=Z
    Vector3 origin; float clock; bool captured;
    protected override void OnBegin() { origin = transform.Position; clock = 0f; captured = true; }
    protected override void Tick(in float delta) {
        if (!captured) { origin = transform.Position; captured = true; }
        clock += delta;
        float period = System.MathF.Max(Period, 0.0001f);
        float phase = (clock / period) % 1f;
        float tri = phase < 0.5f ? (phase * 4f - 1f) : (3f - phase * 4f); // -1..1 const-speed
        float d = tri * Amplitude;
        Vector3 p = origin;
        switch (Axis) { case 1: p.Y += d; break; case 2: p.Z += d; break; default: p.X += d; break; }
        transform.Position = p;
    }
}
```

### Scene shape notları (tam YAML yerelde; bunlar yeniden üretim için yeterli iskelet)

Tüm sahnelerde: `SceneLighting` (scene component, `AmbientIntensity` düşük) + `HDCamera` (members boş — HDCamera'da
serializable member yok, FOV/near/far inert) + `StaticMeshRenderer`'lar `SubMeshIndex: -1`, `SharedMesh`/
`SharedMaterial` canonical (büyük-harf) member adlarıyla.

- **Outdoor.scene** — `SceneLighting{AmbientIntensity:0.1}`; Camera @(0,3,9) hafif aşağı bakar; `DirectionalLight`
  (Sun) grazing açı `rotation {x:-0.3827,y:0.3827,z:0,w:0.8409}`, `Illuminance:90000 ShadowDistance:60`; Ground
  Plane scale 20; BoxA/BoxB(Cube, biri RedEmitter)/Ball(Sphere) gölge döker. **5 whole-mesh.**
- **MultiLightInterior.scene** — `SceneLighting{AmbientIntensity:0.05}`, sun YOK; kapalı oda = Floor+Ceiling+
  BackWall+LeftWall+RightWall (Plane'ler, duvar rotasyonları 90° eksen-quaternion) + CenterBall(Sphere); **8
  PointLight** (PL_1..PL_8) farklı renk/temp, `Lumens 1500-2500 Range 8-10`. **7 whole-mesh, 8 punctual.**
- **MovingLight.scene** — `SceneLighting{AmbientIntensity:0.05}`; Floor + OnScreenWall(z=-3, kameraya bakar) +
  OffScreenWall(z=+9, kamera arkası); `MovingLight` entity = `PointLight{Lumens:6000 Range:14}` + `LightMover
  {Amplitude:2.5 Period:4 Axis:0}` → X ekseninde süpürür. **4 whole-mesh.** NOT: LightMover sadece PLAY modda
  Tick'te hareket eder; paused/edit capture başlangıç konumunu görür.
- **ColorOnly.scene** / **ThinWall.scene** — R1.0 fixture'ları (yukarıda mat'lar). Bu chunk member-adı
  temizliği yaptı: `sunIntensity`(SceneLighting'de yok) kaldırıldı, `ambientIntensity`→`AmbientIntensity`,
  HDCamera inert `members:{}`. Geometri/material/transform DEĞİŞMEDİ (sadece warning temizliği).

## R0.0b DoD durumu

- [x] 5 fixture mevcut (3 yeni + 2 R1.0) + her biri `bal validate` EXIT=0 / `valid: true`.
- [x] Assert 1: Scene-3 MaterialId buffer dejenere (CPU harness CASE 2, RT GPU yoluna girmeden).
- [x] Assert 2: Scene-1/2 ≥1 whole-mesh renderer (SubMeshIndex<0) present (Outdoor=5, MultiLightInterior=7).
- [x] Build 0-err (Cli + GameScripts.dll).
- [x] Fixture tanımları committable doc'a gömüldü (gitignore work-around).

**Sıradaki:** R0.1 — bridge'i flip et (`VolumePostProcessing.cs:66-76` → live volume değerleri;
`PostProcessSettings` defaults GI/SSR-on; precedence volume-authoritative/env-debug; window "dev-only,
R1.0-incomplete"). PROVISIONAL POLICY: R0.1 öncesi `VolumePostProcessing.cs:66-76`'yı yeniden grep et.
