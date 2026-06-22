# Ballistic Engine Mimari Analiz Raporu

Tarih: 2026-06-19
Kapsam: repository yapısı, proje dosyaları, runtime/editor/renderer/asset pipeline/scripting/physics/networking/UI katmanları, mevcut test ve build sinyalleri.
Not: Bu rapor kaynak kodu değiştirmeden yapılan statik okuma, proje dosyası incelemesi ve mevcut test/build komutlarından elde edilen sinyallere dayanır.

## 1. Kısa Yönetici Özeti

Ballistic Engine artık klasik anlamda bir Unity projesi değil; C#/.NET 9 üzerinde çalışan, Unity benzeri authoring modeli kurmaya çalışan custom bir game engine workspace'i. Depoda core engine, DX12/DXR renderer, editor, runtime player, CLI, MCP köprüsü, asset pipeline, scripting hot reload, physics, networking, UI ve örnek proje birlikte bulunuyor.

Genel mühendislik seviyesi hobi projesi ortalamasının üstünde. Özellikle DX12 geçişi, render graph, asset import/cache sistemi, script hot reload, reflection/property model, undo testleri, remote schema ve MCP boundary testleri ciddi düşünülmüş. Projede "bu sistem neden böyle" sorusuna cevap veren çok sayıda yorum ve plan dokümanı var.

Ana sürdürülebilirlik riski ise sınırların çoğunun derleyici tarafından değil, yorumlar, grep disiplini ve geliştirici hafızası tarafından korunması. Mimari niyet güçlü, fakat assembly/project sınırları bunu yeterince enforce etmiyor. Büyüdükçe yanlış dependency, global state sızıntısı ve büyük sınıflarda değişiklik çakışması riski artacak.

Kısa hüküm:

- Mimari vizyon iyi.
- Runtime/editor/asset/rendering kabiliyetleri güçlü.
- Test kültürü belirli kritik alanlarda iyi.
- Katman izolasyonu ve build tekrarlanabilirliği zayıf.
- Büyük dosyalar ve static/global state uzun vadeli bakım maliyetini artırıyor.
- DX12 migration sonrası doküman/kod kalıntıları temizlenmeli.

## 2. İncelenen Ana Yapı

Depodaki ana klasörler ve roller:

- `BallisticEngine.csproj`: root engine library. Engine, abstraction, asset pipeline, shared, toolkit, physics, networking, audio, UI gibi ana kaynakları glob ile topluyor.
- `BallisticEngine.DX12/`: DX12/DXR renderer, HLSL shaderlar, render graph, Lumen GI, DXR query, FSR, GPU-driven rendering.
- `BallisticEngine.Editor/`: ImGui tabanlı editor, dock/panel sistemi, inspector, hierarchy, asset browser, gizmo, remote pipe.
- `BallisticEngine.Runtime/`: standalone player host.
- `BallisticEngine.Cli/`: headless tooling, render/query/perf/schema/scene/asset komutları.
- `BallisticEngine.Mcp/`: editor remote command port'a bağlanan MCP server.
- `AssetPipeline/`: import pipeline, artifact cache, project manifest, build pipeline, game script compilation.
- `Engine/`: scene/entity/component modeli, serialization, rendering data/types, gameplay, networking facade, physics components, audio facade.
- `Abstraction/`: rendering/physics/networking/input abstraction tipleri.
- `Physics/`: Bepu backend.
- `Networking/`: LiteNetLib ve loopback transports.
- `UI/`: UI document/style/layout/render abstraction, Yoga portu.
- `BallisticEngine.Tests.Reflection/`: reflection/property model/undo/MCP/remote schema gibi contract testleri.
- `SampleProject/`: örnek Ballistic project.

## 3. Doğru Mimari Kararlar

### 3.1 DX12 Backend'in Ayrı Project Olması

`BallisticEngine.DX12.csproj`, Vortice/DX12 bağımlılıklarını core engine'den ayrı tutuyor. Bu doğru. Renderer backend'i ayrı project yapmak hem native/GPU bağımlılıklarını karantinaya alır hem de core engine'in daha taşınabilir kalmasını sağlar.

Güçlü taraflar:

- DX12/Vortice bağımlılığı açıkça backend projesinde.
- HLSL shaderlar embedded resource olarak backend içinde.
- FSR native runtime dosyaları condition ile kopyalanıyor; clean checkout bozulmadan build edebiliyor.

Risk:

- Core `BallisticEngine.csproj` hâlâ birçok import/runtime paketi birlikte referanslıyor. DX12 izole edilmiş ama diğer sınırlar aynı netlikte değil.

### 3.2 Render Graph ve Pass Disiplini

`Dx12RenderGraph`, event-ordered pass list'ten DAG/compile/cull/alias/barrier türetmeye doğru evriliyor. Bu çok iyi bir yön.

Güçlü taraflar:

- Pass order stabil tutulmaya çalışılmış.
- Phase 1 list modeli ile Phase 2 compiled graph arasında byte-identical geçiş hedeflenmiş.
- Culling default-off; migration güvenli yapılmış.
- Barrier derivation kapılı ve karşılaştırmalı ilerliyor.
- Render target alias planı için explicit pool var.

Risk:

- Yorumlarda çok fazla invariant var. Bu invariantların bazıları testte var, bazıları hâlâ "dokümanla korunuyor" gibi duruyor.
- DX12 tarafında büyük orchestration hâlâ `DX12HDRenderer.cs` içinde yoğun.

### 3.3 RenderFeature Abstraction

Engine tarafındaki `RenderFeature` API'si backend-agnostic düşünülmüş. Game code sadece engine assembly'ye referans vererek feature yazabiliyor; DX12 adapter bunu backend pass'e çeviriyor.

Doğru kararlar:

- Feature parametreleri plain reflected members.
- `Declare(IFeatureIOBuilder)` backend-neutral.
- `Record(IFeaturePassRecorder)` backend-neutral.
- Runtime order `RenderPassEvent` üzerinden.

Risk:

- `RenderPassEvent` ve `Dx12RenderPassEvent` lock-step tutulmak zorunda. Şu an yorum/invariant ile korunuyor. Bunu küçük bir testle enforce etmek iyi olur.

### 3.4 Asset Pipeline Olgunluğu

Asset pipeline beklenenden olgun:

- `.meta` GUID modeli var.
- Artifact DB var.
- Import parallel yapılabiliyor.
- Importer version/content hash ile dirty detection var.
- Shipped-player path refresh/import yapmıyor, artifact ve guid map üzerinden çalışıyor.
- Build pipeline content pack üretiyor.
- Unity package, Blender, Falcor, model/material/texture/audio/font import gibi çoklu kaynaklar destekleniyor.

Bu alan engine'in en güçlü parçalarından biri. Burada doğru "Unity-style project" hissi kurulmuş.

Risk:

- `AssetDatabase` static global.
- Core engine içinde doğrudan `AssetDatabase` kullanımları var.
- Import pipeline ile runtime asset resolving aynı assembly evreninde fazla kolay karışıyor.

### 3.5 Script Hot Reload

Game scripts tarafı ciddi düşünülmüş:

- `Assets/**/*.cs` ayrı `GameScripts.dll` olarak build ediliyor.
- Collectible `AssemblyLoadContext` kullanılıyor.
- DLL byte-loaded; dosya lock problemleri azaltılmış.
- Reload sırasında scene YAML snapshot ile round-trip ediliyor.
- Reflection cache invalidation için `ReloadCaches` mekanizması var.
- Input/network replication/scene replication registry gibi ALC pinleyen root'lar clear ediliyor.

Bu, küçük bir custom engine için güçlü bir sistem.

Risk:

- Hot reload güvenliği çok fazla static root'un doğru zamanda clear edilmesine bağlı.
- Yeni static cache ekleyen geliştirici `ReloadCaches.Register` kuralını unutursa ALC leak veya stale reflection oluşabilir.
- Bu kural testlerle kısmen korunuyor ama project boundary ile korunmuyor.

### 3.6 Reflection/Inspector/Undo Testleri

`BallisticEngine.Tests.Reflection` harness'i iyi sinyal veriyor. Şu alanlar testleniyor:

- TypeCache
- PropertyModel
- Reload invalidation
- Menu/window registry
- Ordered pass list
- Input action chain
- DrawerStack
- SerializeField
- Material override
- Component preview registry
- Asset inspector registry
- Entity refs
- Collections
- SerializeReference
- Polymorphic collections
- Nested serialization
- Undo coverage
- Remote schema
- MCP boundary
- Serializer drops

Bu testler projenin editor/reflection merkezli kırılgan kısımlarını koruyor. Özellikle undo ve MCP boundary testleri iyi mimari refleks.

## 4. Ana Sürdürülebilirlik Problemleri

### 4.1 Katman Sınırları Derleyiciyle Korunmuyor

En büyük mimari problem bu.

Dokümanda şöyle bir niyet var:

- `Shared`, `ToolKit`: BCL only.
- `Abstraction`: Shared, math.
- `Engine`: Abstraction, Shared.
- `DX12`: Abstraction, Engine, Vortice.
- `Physics`: Abstraction, Bepu.
- `AssetPipeline`: import/file format bağımlılıkları.

Gerçekte root engine project çok geniş:

- `BallisticEngine.csproj` SDK default glob ile kökteki `.cs` dosyalarını topluyor.
- Host projeleri ve bazı klasörler `Compile Remove` ile dışarı atılıyor.
- Core project içinde Assimp, Bepu, LiteNetLib, Magick, NVorbis, OpenTK, Stb, YamlDotNet, ZeroAllocJobScheduler paketleri aynı anda referanslı.

Bu şu riskleri doğurur:

- Herhangi bir engine dosyası yanlışlıkla Assimp veya Bepu kullanabilir.
- Asset pipeline runtime engine'e sızabilir.
- Testte yakalanmayan dependency cycle'lar oluşabilir.
- Mimari kuralı bilmeyen yeni geliştirici veya agent kolayca yanlış layer'a kod ekleyebilir.

Örnek gerçek coupling:

- `Engine/Serialization/SceneSerializer.cs` doğrudan `BallisticEngine.AssetPipeline` ve `AssetDatabase` kullanıyor.
- `Engine/Serialization/DataAssetSerializer.cs` aynı şekilde asset resolving yapıyor.
- `Engine/Rendering/Renderer.cs` submesh material ref için `AssetDatabase.LoadRef` kullanıyor.
- `Engine/Rendering/Terrain/TerrainDefaultMaterial.cs` default material/shader assetlerini `AssetDatabase` ile yüklüyor.

Bunlar pratik olarak anlaşılır, fakat dokümandaki strict layering ile çelişir.

Öneri:

Kısa vadede:

- Architecture test ekle:
  - `Engine/` içinde yasak namespace/package kullanımı.
  - `AssetPipeline/` dışında Assimp/Stb/Magick kullanımı.
  - `Physics/` dışında Bepu kullanımı.
  - `Networking/LiteNetLib/` dışında LiteNetLib kullanımı.
  - `BallisticEngine.DX12/` dışında Vortice kullanımı.
- Bu testleri reflection harness gibi basit console runner'a eklemek yeterli.

Orta vadede:

- Gerçek project ayrımı:
  - `BallisticEngine.Shared`
  - `BallisticEngine.Abstractions`
  - `BallisticEngine.Engine`
  - `BallisticEngine.AssetPipeline`
  - `BallisticEngine.Physics.Bepu`
  - `BallisticEngine.Networking.LiteNetLib`
  - `BallisticEngine.UI`
  - `BallisticEngine.DX12`

### 4.2 Root Glob Build Modeli Kırılgan

SDK-style project default glob kullanıldığı için untracked `.cs` dosyaları bile build'e giriyor. Bu analiz sırasında `BallisticEngine.DX12/Lumen/Dx12LumenCluster.cs` untracked görünmesine rağmen `BallisticEngine.DX12.csproj` tarafından derlenebilir durumda.

Riskler:

- Bir geliştiricinin yerel untracked dosyası build sonucunu değiştirir.
- CI temiz checkout ile lokal build farklı davranabilir.
- "Ben sadece dosya deniyorum" sanılan bir `.cs`, proje davranışını etkiler.
- Agent veya IDE tarafından oluşturulmuş temporary `.cs` dosyaları build'i bozabilir.

Öneri:

- Her project için daha kontrollü include/exclude stratejisi düşün.
- En azından `bin-altcheck`, scratch, docs harness, generated folders, local experiment klasörleri explicit dışarı alınmalı.
- CI'da `git clean` benzeri temiz checkout garantisi olmalı.
- Untracked `.cs` dosyası var mı kontrol eden dev-check eklenebilir.

### 4.3 Global Static State Fazla Yaygın

Engine Unity ergonomisini taklit ettiği için static facade'lar anlaşılır:

- `SceneManager`
- `AssetDatabase`
- `Physics`
- `Network`
- `Audio`
- `RuntimeSet<T>`
- `VolumeManager`
- `RenderFeatureManager`
- `Input`
- `Dx12Backend`

Bu kullanımı kolaylaştırıyor. Fakat sürdürülebilirlik açısından şu sorunları getiriyor:

- Test izolasyonu zor.
- Bir testten diğerine state sızabilir.
- Editor ile play mode aynı global kökleri paylaşır.
- Birden fazla scene/world/session aynı process'te zorlaşır.
- Hot reload, static cache clear sıralamasına bağımlı hale gelir.
- Thread safety net değil.

Özellikle `RuntimeSet<T>` global HashSet. Renderer bunun üzerinden scene renderable'larını okuyor. Scene switch sırasında `ClearAllRenderSets` ile temizlenmesi gerekiyor. Bu "çalışan ama dikkat isteyen" bir pattern.

Öneri:

- Static facade'lar kullanıcı API'si olarak kalabilir.
- Arkada `EngineContext` veya `WorldContext` oluşturulmalı.
- `SceneManager.Current`, `AssetDatabase.Current`, `Physics.World`, `Network.Manager` gibi kökler context üzerinden resolve edilmeli.
- Testlerde yeni context açıp kapatmak mümkün olmalı.
- Runtime/editor ayrımı context ile netleşmeli.

### 4.4 Büyük Dosyalar ve Sınıf Sorumlulukları

Satır sayısı tek başına kalite ölçmez, ama değişim hızı yüksek dosyalarda güçlü bir risk göstergesidir.

En büyük dosyalar:

- `BallisticEngine.Editor/Panels/InspectorPanel.cs`: yaklaşık 2693 satır.
- `BallisticEngine.Editor/EditorApp/EditorApplication.cs`: yaklaşık 2036 satır.
- `BallisticEngine.DX12/DX12HDRenderer.cs`: yaklaşık 1989 satır.
- `Engine/Networking/NetworkManager.cs`: yaklaşık 1570 satır.
- `BallisticEngine.Editor/Panels/AssetBrowserPanel.cs`: yaklaşık 1380 satır.
- `Engine/Serialization/SceneSerializer.cs`: yaklaşık 899 satır.

Bu dosyaların çoğunda refactor izleri var. Örneğin inspector içinde registry/preview/asset inspector ayrımı başlamış. Editor application içinde frame graph ve input router var. DX12 renderer içinde bazı pass'ler `Resources/` altına taşınmış. Yani yön doğru.

Risk:

- Yeni özellikler tekrar bu dosyalara eklenirse karmaşıklık geri büyür.
- Merge conflict riski artar.
- Lokal invariantları anlamadan değişiklik yapmak kolaylaşır.
- Sınıf içi state sayısı arttıkça lifecycle hatası çıkar.

Öneri:

- `DX12HDRenderer`:
  - frame orchestration
  - resource lifetime/resize
  - scene extraction
  - shadow orchestration
  - debug/screenshot/idmap
  ayrı sınıflara ayrılmalı.
- `NetworkManager`:
  - topology/transport
  - rpc dispatch
  - snapshot serialization
  - prediction/input
  - interest management
  - reconnect/orphan ownership
  modüllerine bölünmeli.
- `EditorApplication`:
  - editor shell/window lifecycle
  - frame graph passes
  - viewport windows
  - menu/window registry
  - input/focus/cursor policy
  parçalarına ayrılmalı.
- `InspectorPanel`:
  - entity inspector
  - component member renderer
  - collection/dictionary/polymorphic drawers
  - asset slot/picker
  - scene object picker
  zaten oluşan alt klasörlere daha fazla taşınmalı.

### 4.5 Doküman Drift'i

`README.md` hâlâ eski OpenTK/OpenGL ve "no active editor yet" dönemini anlatıyor. Güncel proje notları ise DX12/DXR, editor, CLI, MCP, agent surface, asset pipeline ve validation sistemini anlatıyor.

Bu ciddi bir onboarding problemi:

- Yeni gelen biri yanlış mimari modelle başlar.
- Agent veya geliştirici eski OpenGL varsayımlarını takip edebilir.
- Projenin gerçek üretim yüzeyi belirsizleşir.

Ayrıca kod yorumlarında da migration residue var:

- `RenderBackend.OpenGL` hâlâ duruyor.
- `HDRenderer.DebugFrame` GL texture id alanları taşıyor.
- Bazı editor thumbnail/material preview commentleri GL texture terimleri kullanıyor.
- `AssetBrowserPanel` `.shader/.glsl` için "GLSL" etiketi gösteriyor.
- `EmbeddedShaderSource` commentleri GLSL/DX12 ayrımından kalma.

Öneri:

- README güncellenmeli ve tek hakikat haline getirilmeli.
- `CLAUDE.md` içindeki güncel mimari özetinden kullanıcıya dönük sade README üretilmeli.
- Eski GL commentleri üç kategoriye ayrılmalı:
  - gerçekten hâlâ geçerli OpenTK window/input açıklaması
  - migration residue
  - silinmesi gereken dead abstraction
- `RenderBackend.OpenGL` ya kaldırılmalı ya da "legacy stub" diye net işaretlenmeli.

### 4.6 Input Abstraction Yarım Geçişte

Yeni `Abstraction/Input` sistemi doğru yönde:

- Engine-owned `Key`, `MouseCtrl`, `PadButton`, `PadAxis` enumları var.
- `InputAction` bu enumlara bind oluyor.
- `IInputSource` OpenTK-free.
- `EngineInputSource` tek mapping noktası gibi tasarlanmış.

Fakat eski input facade hâlâ OpenTK tipleri kullanıyor:

- `Abstraction/IInputProvider.cs` doğrudan `OpenTK.Windowing.GraphicsLibraryFramework.Keys` ve `MouseButton` kullanıyor.
- `Abstraction/API Bindings/Input.cs` OpenTK `Keys` ve `MouseButton` üzerinden public API sunuyor.
- Bazı engine gameplay/camera/sample componentleri hâlâ OpenTK Keys import ediyor.

Bu pratikte çalışır, ama DX12-only / backend-agnostic hedef ile çelişir.

Öneri:

- Public game input API tamamen engine-owned enumlara taşınmalı.
- Eski OpenTK-based `Input` facade deprecated yapılmalı.
- Editor raw OpenTK kullanabilir; game/runtime input kullanmamalı.
- `IInputProvider` signatures engine enumlarına çevrilmeli.

### 4.7 Nullable Durumu Karışık

Core, DX12 ve editor projelerinde `<Nullable>disable</Nullable>` var. Buna rağmen kod içinde `object?`, `string?`, `Transform?` gibi nullable annotation kullanımları mevcut ve build uyarısı üretiyor.

Risk:

- Annotation varmış gibi görünüyor ama compiler contract yok.
- Null-heavy sistemlerde gerçek hatalar gözden kaçabilir.
- Reflection/serialization/asset loading gibi doğal olarak nullable alanlarda niyet belirsizleşir.

Öneri:

Parça parça ilerle:

1. `Abstraction` nullable enable.
2. `AssetPipeline` nullable enable.
3. `BallisticEngine.Cli` zaten enable; warning temizliği.
4. `Engine.Serialization`, `Engine.PropertyModel`, `Engine.BObject` seçili dosyalar.
5. `Editor` ve `DX12` en sona.

Bu sırada warning-as-error hemen açılmamalı; önce bütçe ile azaltılmalı.

### 4.8 Build/Dev Experience Problemleri

Komut sonuçları:

- `dotnet run --project BallisticEngine.Tests.Reflection/BallisticEngine.Tests.Reflection.csproj --no-restore` geçti.
- `dotnet build BallisticEngine.slnx --no-restore` kaynak hatasıyla değil, çalışan `BallisticEngine.Mcp` process'i `BallisticEngine.Mcp.exe` / `.dll` kilitlediği için fail etti.

Bu developer experience sorunu:

- MCP çalışırken full solution build kırılıyor.
- Build çıktısı process tarafından kilitleniyor.
- `/p:UseAppHost=false` bile DLL lock yüzünden çözmedi.

Öneri:

- Dev check script'i:
  - MCP project hariç build seçeneği.
  - veya MCP output path per-session/temp.
  - veya MCP self-host build dışı bırakılabilir.
- `.slnx` içine test project eklenmeli veya ayrı `check` script'te test çalışmalı.
- Build öncesi çalışan MCP process'ini raporlayan net mesaj verilmeli.

### 4.9 Volume Bridge Manuel Tabloya Dönüşüyor

Volume framework güzel:

- `Volume`
- `VolumeProfile`
- `VolumeComponent`
- `VolumeParameter`
- `VolumeManager`

Ama `VolumePostProcessing.Apply` büyüyen merkezi mapping dosyası. Her yeni volume component veya field için PostFX'e elle map gerekiyor.

Bu bilinçli "tek seam" olarak kabul edilebilir. Ancak post-processing feature sayısı arttıkça:

- Mapping unutulabilir.
- Defaults drift edebilir.
- Component field adı ile `PostProcessSettings` field adı arasında manual coupling artar.
- Test yoksa no-volume/default-volume davranışı bozulabilir.

Öneri:

- Her volume component için mapping coverage test'i.
- `PostFX` defaultları ile `VolumeStack` defaultları aynı mı testi.
- Reflection/attribute tabanlı otomatik mapping düşünülebilir, ama performans ve açıklık için önce test daha iyi.

### 4.10 Renderer ve Scene Data Akışı Kısmen Global RuntimeSet'e Bağlı

Renderer, `RuntimeSet<IStaticMeshRenderer>`, `RuntimeSet<PointLight>`, `RuntimeSet<SpotLight>` gibi global setleri okuyor. Bu Unity-style edit/play için pratik.

Risk:

- Scene switch sonrası stale renderer kalırsa eski sahne draw edebilir.
- Bu zaten yaşanmış ve `SceneManager.ClearAllRenderSets` savunması eklenmiş.
- Yeni renderable tür ekleyen geliştirici `ClearAllRenderSets` listesine eklemeyi unutabilir.

Öneri:

- `RuntimeSetRegistry` gibi merkezi kayıt mekanizması.
- Her renderable type kendi runtime set clear action'ını register etmeli.
- Test: scene clear sonrası bütün runtime setler boş.
- Renderer scene view'ı mümkünse explicit `RenderSceneSnapshot` üzerinden alsın.

## 5. Alan Bazlı Değerlendirme

### 5.1 Core Engine / Scene / BObject

Doğru yönler:

- Unity benzeri `Entity`, `Behaviour`, `SceneBehaviour`, `Transform`.
- Edit/play split düşünülmüş.
- Snapshot restore ile play stop davranışı var.
- Scene object refs GUID instance id üzerinden lazy resolve oluyor.
- Lifecycle'da script exceptions guard'lanıyor.

Riskler:

- `SceneManager` static ve single current scene varsayımı güçlü.
- Multi-scene varmış gibi `activeScenes` set'i var, ama API `GetCurrentScene().Last()` merkezli.
- Global `RenderCamera`, `IsPlaying`, `IsPaused`, `SnapshotProvider`, `SceneLoader` aynı sınıfta.
- Runtime systems SceneManager'a doğrudan bağlı.

Öneri:

- `SceneManager` içindeki statik facade ile instance state ayrılmalı.
- Multi-scene hedef değilse API sadeleştirilmeli; hedefse scene context explicit yapılmalı.
- Lifecycle order testleri artırılmalı.

### 5.2 Serialization / Reflection

Doğru yönler:

- Unity-style public fields/properties + `[SerializeField]` non-public opt-in.
- `[SerializeReference]`, polymorphic collections, nested objects, entity refs düşünülmüş.
- Serializer drop warning var.
- Reflection tests güçlü.

Riskler:

- `SceneSerializer.cs` çok büyümüş ve birçok özel durum içeriyor.
- Asset resolving doğrudan `AssetDatabase` ile.
- Serialization formatı engine, editor, asset database ve reflection model arasında çok merkezi coupling noktası.

Öneri:

- Serializer'ı value codec registry olarak bölmek:
  - primitive/math codec
  - asset ref codec
  - scene ref codec
  - collection/dictionary codec
  - polymorphic codec
  - render feature codec
- Mevcut testler korunarak incremental extraction yapılmalı.

### 5.3 DX12 Renderer

Doğru yönler:

- DX12 backend ayrı.
- Render graph migration bilinçli.
- Pass'ler kaynak klasörlere taşınmaya başlamış.
- Lumen V2 planı net.
- GPU validation/golden set belgeleri var.
- DXR scene query gibi agent-operability odaklı güçlü tooling var.

Riskler:

- `DX12HDRenderer` hâlâ çok merkezi.
- DX12 pass state ve resource lifetime yorumlarla korunuyor.
- Stackalloc-in-loop uyarıları var:
  - `DX12HDRenderer.cs`
  - `Dx12TransparentsPass.cs`
  - `Dx12ClusteredLights.cs`
  - `Dx12GpuDrivenRenderer.cs`
- Environment variable "doors" çok fazla olursa product behavior belirsizleşir.
- Lumen V2 aktif geliştirme halinde; legacy/plan/implementation sınırı net tutulmalı.

Öneri:

- `DX12HDRenderer` sadece frame coordinator haline getirilmeli.
- Resource resize/lifetime manager ayrı.
- Scene extraction/cache builder ayrı.
- Shadow orchestration ayrı.
- Debug capture/idmap ayrı.
- Stackalloc warnings temizlenmeli.
- DX12 env flags dokümante edilmeli ve product/debug diye ayrılmalı.

### 5.4 Editor

Doğru yönler:

- Editor frame graph var.
- Input router var.
- Dock panel registry var.
- Component preview registry ve asset inspector registry ile eski instanceof zincirleri azaltılmış.
- Undo coverage testleri var.
- Remote command queue main-thread pump ile doğru düşünülmüş.

Riskler:

- `EditorApplication` hâlâ çok fazla rol taşıyor.
- `InspectorPanel` çok büyük.
- AssetBrowser doğrudan filesystem operations yapıyor; doğru ama büyük sorumluluk.
- UI rendering/thumbnail GL/DX12 comment drift'i var.

Öneri:

- Editor shell, viewport, input/focus, menu/window management ayrı sınıflara taşınmalı.
- Inspector'da collection/dictionary/polymorphic/asset slot picker kendi componentlerine ayrılmalı.
- Asset operations servis katmanında daha fazla toplanmalı; panel sadece UI olmalı.

### 5.5 Asset Pipeline

Doğru yönler:

- Import cache/artifact yaklaşımı doğru.
- Parallel import + sequential commit iyi.
- `.meta` GUID modeli iyi.
- Shipped player mode düşünülmüş.
- Content pack ve guid map iyi.

Riskler:

- Static `AssetDatabase` global.
- Pipeline thread safety volatile map swap ile çözülmüş, ama loaded asset cache global ve thread safety sınırlı.
- Editor ve runtime aynı API üzerinden çok şey yapıyor.

Öneri:

- `IAssetResolver` / `IAssetStore` interface ile engine runtime tarafı ayrılmalı.
- Editor import pipeline ve player content resolver ayrılmalı ama aynı facade'dan sunulabilir.
- Asset loading thread contract net yazılmalı.

### 5.6 Networking

Doğru yönler:

- Transport abstraction var.
- LiteNetLib quarantined.
- Prediction, interpolation, reconnect, interest, lag compensation gibi ileri konular düşünülmüş.
- Source generator ile network serialization yaklaşımı var.

Riskler:

- `NetworkManager` çok büyük ve çok fazla state sahibi.
- Static `Network` facade ergonomik ama test izolasyonunu zorlaştırıyor.
- Networking gibi hata yüzeyi yüksek bir alan için daha fazla protocol-level test gerekli olabilir.

Öneri:

- `NetworkManager` alt domainlere ayrılmalı.
- Wire codec/generator/protocol tests artırılmalı.
- Transport-independent simulation harness kurulmalı.

### 5.7 Physics

Doğru yönler:

- Bepu backend ayrı klasörde.
- Engine components `IPhysicsWorld` üzerinden konuşuyor.
- Fixed timestep ve play-mode lifecycle açık.
- Unity-style collider/rigidbody semantics düşünülmüş.

Riskler:

- `Physics` static facade global world'e bağlı.
- Engine layer Bepu bilmez ama core project Bepu package'ı görebiliyor.
- Collider/rigidbody lifecycle static scene state'e bağlı.

Öneri:

- Bepu package core project'ten çıkarılmalı; ayrı physics backend project'e taşınmalı.
- `Physics.World` context'e bağlanmalı.

### 5.8 UI

Doğru yönler:

- UI, text resolver üzerinden asset pipeline'a doğrudan bağlanmamaya çalışıyor.
- Yoga portu içeride.
- Renderer abstraction var.

Riskler:

- UI klasörü büyük ama domain olarak ayrılmış.
- Yoga portu çok satırlı ve vendor-like; edit edilmemesi gereken alanlar işaretlenmeli.
- Font loading bootstrap üzerinden asset pipeline'a bağlanıyor.

Öneri:

- UI vendor/Yoga kodu ayrı namespace/project veya açık `ThirdParty` alanına alınabilir.
- UI asset resolving interface daha netleştirilebilir.

## 6. Build ve Test Bulguları

Çalıştırılan komutlar:

```powershell
dotnet build BallisticEngine.slnx --no-restore
dotnet build BallisticEngine.slnx --no-restore /p:UseAppHost=false
dotnet run --project BallisticEngine.Tests.Reflection\BallisticEngine.Tests.Reflection.csproj --no-restore
```

Sonuç:

- Reflection test harness geçti.
- Full solution build, çalışan `BallisticEngine.Mcp (30040)` process'i output exe/dll dosyalarını kilitlediği için fail etti.
- Build loglarında kaynak compile error görünmedi; ana projeler output üretmeye kadar ilerledi.

Öne çıkan uyarı sınıfları:

- Nullable annotation context uyarıları.
- DX12 stackalloc-in-loop `CA2014`.
- Analyzer release tracking `RS2008`.
- Windows-only COM call platform analyzer uyarıları.
- Bazı unused/default field uyarıları.

Bu uyarılar tek başına kritik bug demek değil, ama kalite kapısı olarak birikmeleri sağlıklı değil.

## 7. Çalışma Ağacı Durumu

Analiz sırasında çalışma ağacı zaten dirty idi. Görülen örnekler:

- Modified:
  - `BallisticEngine.Editor/Panels/Inspector/Preview/ComponentPreviews.cs`
  - `Engine/Rendering/Volumes/VolumePostProcessing.cs`
  - `BallisticEngine.DX12/Resources/Dx12SceneAS.cs`
  - `BallisticEngine.DX12/Lumen/Dx12LumenScene.cs`
- Untracked:
  - `BallisticEngine.DX12/Lumen/Dx12LumenCluster.cs`
  - `Docs/Brand/`
  - `Docs/Docs.rar`
  - `Docs/index.html`
  - `Engine/Rendering/Volumes/Components/Reflections.cs`
  - `SampleProject/Scripts.csproj`
  - `native/oidn/lib/`
  - çeşitli sample assets ve bin-altcheck klasörleri.

Not: Untracked `.cs` dosyaları SDK glob nedeniyle build'e dahil olabilir. Bu raporun önemli build reproducibility bulgularından biridir.

## 8. Önceliklendirilmiş İyileştirme Planı

### P0: Güvenlik Ağı ve Doküman Gerçeği

1. README'yi güncelle:
   - DX12/DXR live backend.
   - Editor var.
   - CLI/MCP var.
   - OpenGL eski bilgi.
   - Build/test komutları gerçek duruma göre.

2. Architecture tests ekle:
   - forbidden namespace/package rules.
   - render pass enum parity.
   - runtime set clear coverage.
   - volume component mapping coverage.

3. Dev check script oluştur:
   - build core/editor/runtime/dx12/cli.
   - reflection tests run.
   - MCP lock varsa net uyarı.
   - untracked `.cs` varsa uyarı.

### P1: Build ve Project Sınırları

1. `BallisticEngine.Tests.Reflection` `.slnx` içine alınmalı veya resmi check script'e eklenmeli.
2. MCP running durumunda build'in kırılmaması için output strategy düşünülmeli.
3. Physics backend ayrı project'e taşınmalı.
4. LiteNetLib transport ayrı project'e taşınmalı.
5. AssetPipeline core engine'den ayrılmalı.

### P2: Büyük Dosya Parçalama

1. `NetworkManager` domainlere ayrılmalı.
2. `DX12HDRenderer` coordinator'a indirilmeli.
3. `EditorApplication` shell/viewport/input/menu olarak bölünmeli.
4. `InspectorPanel` drawer/picker/section components'e bölünmeli.

### P3: Static Context Refactor

1. `EngineContext` tasarımı.
2. Static facade'lar current context'e yönlendirilmeli.
3. Tests kendi context'ini açıp kapatabilmeli.
4. Hot reload clear list'i context-owned state'e taşınmalı.

### P4: Nullability ve Warning Budget

1. `Abstraction` nullable enable.
2. `AssetPipeline` nullable enable.
3. CLI warning cleanup.
4. Core serialization/property model cleanup.
5. DX12 analyzer warnings cleanup.

## 9. Yapılmaması Gerekenler

- Büyük refactor'a testsiz başlama.
- Static facade'ları tek hamlede silmeye çalışma.
- DX12 render graph ve Lumen geçişini aynı anda mimari project split ile karıştırma.
- AssetDatabase'i kaldırmadan önce asset ref serialization için replacement interface tanımlamadan ilerleme.
- README güncellemesini erteleme; yanlış doküman yanlış değişiklik üretir.

## 10. Genel Değerlendirme

Ballistic Engine'in en iyi yanı, karmaşık sistemlerde "neden" sorusuna cevap verme alışkanlığı. Plan dokümanları, yorumlar, validation baseline'ları ve reflection tests bu projede gerçek bir mühendislik hafızası olduğunu gösteriyor.

En zayıf yanı ise bu hafızanın çok fazla insan/agent disiplinine dayanması. Şu an bilen biri doğru yere doğru kodu koyabilir; ama repo büyüdükçe "doğru yer" compiler tarafından zorlanmadığı için mimari erozyon kaçınılmaz olur.

Bu yüzden bir sonraki kalite sıçraması yeni render özelliği veya yeni editor paneli değil; sınırları otomatik koruyan project/test/dev-check altyapısı olmalı. Ondan sonra mevcut güçlü sistemler daha rahat büyür.
