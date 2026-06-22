# Editör Açılış Hızı — Derin Analiz Raporu

**Tarih:** 2026-06-21
**Yöntem:** Repo'nun izole bir kopyası (`E:/tmp/bal-startup-analysis`, junction'lı SampleProject)
build edildi, `EngineBootstrap` + `EditorApplication` + `DirectXRenderAsset` ctor'larına stopwatch
instrumentation eklendi, editör WARM (cache sıcak) ve COLD (ShaderCache/PsoCache/ScriptAssemblies
silinmiş) olarak çalıştırıldı. Aşağıdaki ms değerleri RX 9070 XT + .NET 9 üzerinde gerçek
ölçümlerdir (run2/run3 birbirini doğruladı). **Ana repoya hiçbir instrumentation sızmadı.**

> NOT: İlk izole-kopya run'ı (2955 ms) yanıltıcıydı — kopyanın farklı path'i `Scripts.csproj`'u
> yeniden yazdırıp gereksiz bir `dotnet build` tetikledi (GameScripts 1418 ms). 2. run'dan itibaren
> gerçek warm steady-state ölçüldü. Bu da başlı başına bir bulgu (B3).

---

## 1. Ölçüm sonuçları

### WARM açılış (cache sıcak — günlük senaryo): **ilk RenderFrame ≈ 920–1100 ms**

| Adım | Warm ms | Pay | Thread | Notlar |
|---|---:|---:|---|---|
| window-ctor (OpenTK GameWindow) | 220–340 | ~28% | main | GLFW init + monitor/DPI enum (OS-bound) |
| **Renderer.Initialize** (12 pass ctor, PSO) | 176–191 | ~18% | main | warm cache'le bile pahalı — PSO driver-compile |
| **ImGuiController** (font atlas SDF bake) | 127–153 | ~15% | main | Calibri+Bold+icon, her açılış yeniden bake |
| new Dx12Device (D3D12 device+heaps+fence) | 98–114 | ~10% | main | GPU init |
| RegisterUIFonts (UI SDF atlas) | 45–49 | ~5% | main | |
| GameScripts (up-to-date fast path) | 35–39 | ~4% | main | sadece mtime scan |
| Audio (OpenAL device aç) | 31–33 | ~3% | main | bağımsız |
| Window/User registry rebuild | 22–24 | ~2% | main | GameEditorScripts compile-check |
| BuildComponentRegistry (+TypeCache+Input) | 9–10 | ~1% | main | reflection (hızlı) |
| Physics (Bepu), AssetDatabase.Init, layout, misc | <15 | ~1% | main | |

### COLD açılış (cache temiz — ilk açılış / shader veya script edit sonrası): **ilk RenderFrame ≈ 4290 ms**

| Adım | Cold ms | vs Warm | Sebep |
|---|---:|---|---|
| **Renderer.Initialize** | **2505** | warm 180 (14×) | ~150 HLSL shader'ın DXC compile'ı + PSO driver-compile, **sıralı, tek thread** |
| **GameScripts.CompileAndLoad** | **989** | warm 35 (28×) | `dotnet build` subprocess |
| new Dx12Device | 114 | ~aynı | |
| ImGuiController font | 148 | ~aynı | |
| diğer | ~aynı | | warm ile aynı |

---

## 2. Bulgular (önem sırasına göre)

### B1 — COLD: Shader/PSO derlemesi sıralı ve tek thread (2505 ms) ★ en büyük cold kazanç
`DX12HDRenderer.Initialize` 12+ render pass'ini **eager** kuruyor (`new Dx12DeferredLightingPass`,
`Dx12GtaoPass`, `Dx12SkyPass`, `Dx12DdgiPass`, `Dx12ReflectionsPass`, `Dx12TaaPass`, `Dx12FsrPass`,
`Dx12MotionBlurPass`, `Dx12DepthOfFieldPass`, `Dx12CompositePass` …, `DX12HDRenderer.cs:760-825`).
Her ctor kendi HLSL'ini `Dx12ShaderCompiler.Compile` (`Dx12ShaderCompiler.cs:31-62`) ile **sırayla**
derliyor. DXC thread-safe; bu compile'lar bir worker havuzunda paralelleştirilebilir.

### B2 — Çoğu pass ilk frame için gereksiz, yine de eager kuruluyor (~500–700 ms cold)
DDGI, RT Reflections, FSR/DLSS upscaler, MotionBlur, DoF — hiçbiri varsayılan ilk frame'de aktif
değil (volume kapalı / opt-in), ama PSO'ları açılışta kuruluyor. RT Sun Shadows zaten lazy
(`EnsureRtShadows`, `DX12HDRenderer.cs:2375` BeginRender'dan koşullu) — **aynı pattern diğer
opt-in pass'lere uygulanırsa** o pass'in maliyeti ilk-frame'den 2. frame'e (veya ilk-kullanıma)
kayar. Görsel byte-identical kalır (pass zaten çalışmıyordu).

### B3 — Reflection registry'leri çok ucuz ama GameScripts up-to-date kontrolü path'e duyarlı
Warm'da BuildComponentRegistry (TypeCache+InputRegistry dahil) sadece ~10 ms — burada sorun yok.
Asıl tuzak: `GameScripts.EnsureProjectFile` (`GameScripts.cs:139-160`) `Scripts.csproj` içine engine
binaries'in MUTLAK path'ini (`<BallisticEngineDir>`) yazıyor; engine farklı bir yoldan çalışırsa
(worktree, kopya, taşınmış repo) csproj rewrite olur → mtime değişir → `IsUpToDate` false → gereksiz
`dotnet build` (≈1 s). Worktree/CI tabanlı geliştirmede her yeni yolda bir kez bu bedel ödenir.

### B4 — WARM: tüm açılış tek main thread'de seri, çok şey paralelleştirilebilir (~920 ms)
window-ctor + Dx12Device + 3 ayrı font/SDF bake + Audio + GameScripts + reflection hepsi ardışık.
**Bağımsız ve GPU gerektirmeyen** işler (Audio OpenAL 33 ms, Physics Bepu, AssetDatabase.Initialize,
GameScripts mtime scan) window/device kurulurken bir worker thread'de koşabilir.

### B5 — ImGui font atlas her açılış yeniden bake (127–153 ms, warm'da bile)
`ImGuiController.LoadFont` (`ImGuiController.cs:95-169`) Calibri+Bold+lucide ikonları + 3-4 semantik
boyut için SDF atlasını **her açılış sıfırdan** rasterize ediyor; diske cache yok. SDF baking CPU,
GPU texture upload main-thread. Atlas blob'u diske cache'lenip (font dosyaları + boyut + DPI hash'i
ile geçersizlenerek) deserialize edilebilir.

### B6 — window-ctor 220–340 ms (OS-bound, paralelleştirilemez ama örtülebilir)
OpenTK GameWindow ctor'u GLFW init + monitor/DPI enumerasyonu yapıyor (`Dx12BallisticEngineWindow.cs`,
çoğu maliyet OpenTK/GLFW içinde). Tek thread şart. Ama döndükten **sonraki** bağımsız işler bu süre
boyunca zaten başlatılabilir (B4).

---

## 3. Öncelikli hızlandırma önerileri

| Öncelik | Değişiklik | Tahmini kazanç | Risk |
|---|---|---|---|
| **P0** | **Shader/PSO derlemesini paralelleştir** — pass ctor'larındaki `Dx12ShaderCompiler.Compile` çağrılarını bir worker havuzunda topla (DXC thread-safe). | COLD 2505→~700 ms | Orta: PSO create main-thread olabilir; DXC→DXIL paralel, PSO create seri kalır. Byte-identical (sadece sıra). |
| **P0** | **Opt-in pass'leri lazy yap** (DDGI, RT Reflections, FSR/DLSS, MotionBlur, DoF) — `EnsureRtShadows` pattern'iyle ilk-kullanıma ertele. | COLD −500…700 ms, WARM −80…120 ms | Düşük: pass zaten çalışmıyordu; ilk aktifleştirmede tek seferlik küçük hitch. |
| **P1** | **Bağımsız init'leri worker thread'e al** — Audio, Physics, AssetDatabase.Initialize, GameScripts mtime-scan window/device kurulurken paralel koşsun; scene load öncesi join. | WARM −80…120 ms | Düşük-orta: thread-safety + sıralama (ComponentRegistry scene'den önce bitmeli). |
| **P1** | **B3: `Scripts.csproj` path-bağımsız yap** — `<BallisticEngineDir>`'i csproj'a mutlak yazmak yerine build'e MSBuild property olarak geçir (zaten Condition fallback var); böylece kopya/worktree gereksiz rebuild yapmaz. | path değişiminde COLD −1 s | Düşük. |
| **P2** | **ImGui font atlasını diske cache'le** — SDF blob'u `.fontcache` olarak yaz, font+boyut+DPI hash'iyle geçersizle. | WARM −100…140 ms | Düşük-orta: cache invalidation + DPI scale değişimi. |
| **P2** | **RegisterUIFonts + ImGui font'u birleştir/paralelle** — iki ayrı SDF bake (45+150 ms) tek atlas/worker'a inebilir. | WARM −40 ms | Orta. |

**Birleşik tahmini hedef:**
- COLD: 4290 → **~2000–2500 ms** (P0 shader-paralel + lazy pass'ler)
- WARM: ~1000 → **~650–750 ms** (P1 worker init + P2 font cache)

---

## 4. Ne YAPMADIM / doğrulama notları
- Gerçek bir optimizasyon **uygulamadım** — bu sadece ölçüm + analiz raporu (istenildiği gibi).
- Tüm instrumentation izole kopyada (`E:/tmp/bal-startup-analysis`) kaldı; ana repo temiz.
- ms değerleri tek makine/tek GPU; mutlak değil göreli oranlar (pay yüzdeleri) taşınabilir.
- P0 "shader paralel" tahmini muhafazakâr: PSO driver-compile kısmı seri kalabilir, o yüzden
  2505→700 bir tavan tahmini; gerçek kazanç DXC payına bağlı (ayrıca ölçülmeli).
- Mesh streaming işi (bu chat'in ilk yarısı) açılış raporundan bağımsızdır; o sahne YÜKLEME
  hitch'ini çözüyordu, bu rapor editör BOOT süresini ölçüyor.
```
```
