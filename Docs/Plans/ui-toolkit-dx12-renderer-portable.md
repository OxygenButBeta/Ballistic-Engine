# UI Toolkit — DX12 IUIRenderer backend (port-edilebilir)

> **Bağlam:** Bu worktree (`worktree-ui-toolkit`) `dx12-perf-radical` branch'inden ayrıldı; o
> branch'te renderer değişiklikleri sürüyor. Burada UI render backend'ini **şimdi** yazıyoruz ama
> renderer işi bitince ana branch'e **çakışmasız port** edilebilecek şekilde izole tutuyoruz.
> Görsel UI Builder editörü buna bağlı (canvas'ı bu backend bir RT'ye çizecek), o yüzden öne aldık.

## Hedef

`UI/Rendering/IUIRenderer.cs`'in 8 primitive'ini (Begin/End, DrawRect, DrawGradient, DrawText,
DrawImage, PushClip/PopClip) DX12'de implement et ve oyun-içi overlay olarak ekrana bas. UI ağacı
(`VisualElement` + Yoga layout + USS) ZATEN tamam ve doğru — eksik tek parça çizim.

## Port-edilebilirlik ilkeleri (merge sürtünmesini sıfıra yakın tut)

1. **Tek yeni dosya, izole sınıf.** Tüm backend `BallisticEngine.DX12/UI/Dx12UIRenderer.cs` içinde.
   Mevcut DX12 dosyalarını DEĞİŞTİRME — sadece iki entegrasyon noktasına BİRER çağrı ekle (aşağıda).
2. **Mevcut helper'lara yaslan, kopyalama:** `Dx12Device`, `Dx12Buffer`, `Dx12DescriptorHeap`,
   `Dx12Texture2D`, `Dx12OffscreenTarget`, `Dx12ShaderCompiler`. Bunlar renderer rework'ünde stabil
   kalması beklenen altyapı; UI pass kendi PSO/root-sig'ini kurar, paylaşılan global state'e dokunmaz.
3. **Kendi shader'ı embedded.** UI shader'ı `BallisticEngine.DX12/` altında embedded `.hlsl`
   (gotcha: incremental build re-embed etmez → obj temizle; bkz memory `dx12-shader-edit-build-gotcha`).
4. **Entegrasyon noktaları minimum ve yorumlu** ki port'ta `git` çakışması tek satır olsun:
   - **Player:** `Dx12WindowedRuntime.OnRenderFrame` — `PresentTexture`'dan ÖNCE
     `Dx12UIRenderer.RenderOverlays(ldr)`. (Alternatif: `DX12HDRenderer.Render` sonunda, composite
     sonrası, `ldr` RenderTarget state'indeyken — daha temiz, tek yer.)
   - **Editör:** UI Builder canvas'ı kendi RT'sine çizdireceği için ayrı; player overlay'inden bağımsız.
5. **Frame graph'a dokunma.** UI ayrı bir "after composite" adımı; pass-graph plan'ındaki event-sorted
   listeye sokmaya gerek yok (port sırasında o liste değişmiş olabilir). `ldr` RT'sine doğrudan çiz.

## Çizim yeri (kesin)

`DX12HDRenderer.Render()` sonunda `DrawComposite` çağrıldıktan SONRA `ldr` (R8G8B8A8_UNORM,
`DisplayResource`) RenderTarget state'inde duruyor (player path, `PresentToScreen=true`). UI overlay'i
TAM burada `ldr`'e çizilmeli:

- Player: composite sonrası, `ldr` RenderTarget → UI çiz → present blit. (`!PresentToScreen` dalındaki
  `ldr.ColorToShaderResource()` editör içindir; UI ondan ÖNCE çizilmeli ya da editör kendi RT'sini kullanır.)
- Hedef RT formatı: `Dx12OffscreenTarget.ColorFormat` (UI PSO bunu RTV formatı yapmalı).

## Primitive → GPU eşlemesi

Tek bir **batched 2D pass** — tüm UI quad'larını bir vertex/index buffer'a topla, tek (veya az) draw:

- **DrawRect (rounded + border):** quad + SDF rounded-box. Pixel shader'da
  `sdRoundedBox(p, halfSize, radius)` ile fill + border (iki SDF band). Per-corner radius (Vector4)
  zaten walker tarafından clamp'leniyor. Anti-alias: `smoothstep(fwidth)`.
- **DrawGradient:** aynı rounded quad, fill rengi yerine linear/radial gradient. Gradient stop'ları
  küçük bir CBV/structured buffer'da; başlangıçta 2-stop fast-path, sonra N-stop.
- **DrawText:** SDF font atlas. `UIFonts.FontAtlas` CPU tarafında HAZIR — eksik olan GPU upload +
  glyph quad üretimi. Atlas texture'ı `Dx12Texture2D`'ye yükle (UIFonts.Version değişince re-upload).
  Her glyph bir quad; SDF alpha = `smoothstep(0.5-w, 0.5+w, sample)`. Shadow/glow = ikinci pass offset.
- **DrawImage:** `Image.Texture` opaque handle → `Dx12Texture2D` cast; tint çarpımı; ScaleMode için
  UV hesabı (StretchToFill/ScaleToFit/ScaleAndCrop).
- **PushClip/PopClip:** rounded clip gerektiği için scissor YETMEZ (scissor dikdörtgen). İki seçenek:
  - v1: dikdörtgen scissor (`RSSetScissorRects`) — overflow:hidden'ın %90'ı bu; rounded clip yok.
  - v2: clip rect'i her quad'a per-vertex/instance geçir, pixel shader'da SDF clip uygula (rounded).
  → **v1 ile başla** (scissor), v2'yi rounded-clip ihtiyacı çıkınca ekle.

## Vertex formatı (öneri)

```
struct UIVertex { float2 pos; float2 uv; float4 color; float4 rectParams; float4 radius; uint mode; }
```
`mode` = 0 solid, 1 gradient, 2 text, 3 image. `rectParams` = (rectCenter.xy, halfSize.xy) SDF için.
Tek PSO + mode-branch ya da mode başına ayrı PSO; başlangıçta tek PSO + dynamic branch yeterli.

## Koordinat / ölçek

Tüm primitive'ler PANEL/LOGICAL piksel (top-left origin, +Y down). Backend `Begin(canvasSize, scale)`
ile ortho projeksiyon kurar: `[0..canvasW]×[0..canvasH] → NDC`, scale uniform olarak baked.
`UIRenderWalker.Draw(doc, renderer)` zaten `doc.ResolvedScale`'i uyguluyor.

## Sürüм dilimleri (her biri çalışır biter)

| # | Dilim | Sonuç |
|---|-------|-------|
| R1 | PSO + root-sig + ortho + **DrawRect (solid, no radius)** + tek draw batch | Ekranda renkli kutular |
| R2 | Rounded + border (SDF) + clip scissor (v1) | Gerçek panel/buton görünümü |
| R3 | **DrawText** (SDF atlas upload + glyph quad) | Yazı görünür |
| R4 | DrawGradient + DrawImage | Tam primitive seti |
| R5 | Player hook: `RenderOverlays(ldr)` present öncesi + UIDocument.Active dolaş | Oyun-içi UI canlı |
| R6 | (Editör) UI Builder canvas RT'si bu backend'le çizilir | Görsel editör canvas'ı |

R1–R5 = oyun-içi UI ekrana gelir. R6 = görsel editöre köprü.

## Port checklist (renderer merge sonrası ana branch'e taşırken)

- [ ] `Dx12UIRenderer.cs` kullandığı helper imzaları hâlâ var mı? (Device/Buffer/DescriptorHeap/Texture2D/OffscreenTarget)
- [ ] `ldr`/`DisplayResource` ismi ve composite-sonrası state aynı mı? (rework değiştirmiş olabilir)
- [ ] Entegrasyon çağrısı pass-graph'ın yeni event sırasına mı taşınmalı? (composite-after invariant'ı koru)
- [ ] Embedded shader re-embed edildi mi? (obj temizle)
- [ ] Editör overlay path'i (`!PresentToScreen`) hâlâ aynı mantıkta mı?
```
