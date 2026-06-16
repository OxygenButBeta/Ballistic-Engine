# Native binaries (DX12 renderer dependencies)

Large native DLLs for the DX12 renderer's denoising + upscaling. The **DLLs are gitignored**
(like `Tools/Tracy/` and the Bistro content) — only the small C API **headers** are tracked
(they define the P/Invoke contract). Re-fetch the DLLs with the commands below if they're missing.

Pinned versions (2026-06-15):
- **Intel Open Image Denoise (OIDN) 2.5.0** — `native/oidn/` — ALL denoising (RT GI/reflections/
  shadows, and any other denoise need). The repo uses the C API in `oidn/include/OpenImageDenoise/oidn.h`.
- **AMD FidelityFX SDK 2.2.0** — `native/fsr/` — FSR upscaling (FSR4 on RDNA4 / RX 9070 XT, falls back
  to FSR3.1) via the FFX host API in `fsr/include/ffx_api/`. Also framegeneration + radiancecache DLLs.

## OIDN 2.5.0 (Windows x64)

```sh
cd native/oidn
curl -sL -o oidn.zip "https://github.com/RenderKit/oidn/releases/download/v2.5.0/oidn-2.5.0.x64.windows.zip"
unzip -q oidn.zip
mv oidn-2.5.0.x64.windows/{bin,include,lib} .
# drop non-AMD device backends (RX 9070 XT uses CPU + HIP/AMD only)
rm -f bin/OpenImageDenoise_device_cuda.dll bin/OpenImageDenoise_device_sycl.dll bin/sycl8.dll \
      bin/ur_loader.dll bin/ur_adapter_level_zero.dll bin/ur_win_proxy_loader.dll
rm -rf oidn-2.5.0.x64.windows oidn.zip
```
Runtime DLLs kept: `OpenImageDenoise.dll`, `OpenImageDenoise_core.dll` (holds the trained weights, ~50MB),
`OpenImageDenoise_device_cpu.dll` (guaranteed fallback), `OpenImageDenoise_device_hip.dll` (AMD GPU),
`tbb*.dll`. The `oidnDenoise.exe` CLI is a handy offline test door.

## FidelityFX SDK 2.2.0 (FSR upscaler, DX12 x64)

The prebuilt samples zip carries the runtime DLLs; the host API headers come from the source tree.

```sh
cd native/fsr
curl -sL -o fsr.zip "https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/download/v2.2.0/FidelityFX-Samples-v2.2.0-prebuilt.zip"
# extract the DX12 runtime DLLs to bin/ (any Release path inside works — they're identical copies)
mkdir -p bin
for d in amd_fidelityfx_upscaler_dx12.dll amd_fidelityfx_loader_dx12.dll \
         amd_fidelityfx_framegeneration_dx12.dll amd_fidelityfx_radiancecache_dx12.dll amd_ags_x64.dll; do
  p=$(unzip -l fsr.zip | grep -ioE "Samples/[A-Za-z0-9_/.-]*Release/$d" | head -1)
  unzip -o -j -q fsr.zip "$p" -d bin/
done
rm -f fsr.zip
# host API headers (tracked in git, re-fetch only if needed):
BASE="https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/v2.2.0/Kits/FidelityFX"
mkdir -p include/ffx_api && cd include/ffx_api
curl -sLO "$BASE/api/include/ffx_api.h"
curl -sLO "$BASE/api/include/ffx_api_types.h"
curl -sLO "$BASE/api/include/ffx_api_loader.h"
curl -sLO "$BASE/api/include/dx12/ffx_api_dx12.h"
curl -sLO "$BASE/upscalers/include/ffx_upscale.h"
curl -sLO "$BASE/framegeneration/include/dx12/ffx_api_framegeneration_dx12.h"
```

## Deployment

The renderer P/Invokes these by name; they must sit next to the exe at runtime (the build copies
`native/*/bin/*.dll` to the output dir — see the DX12 csproj copy step). OIDN also needs its `core`
and device DLLs beside the main `OpenImageDenoise.dll`.
