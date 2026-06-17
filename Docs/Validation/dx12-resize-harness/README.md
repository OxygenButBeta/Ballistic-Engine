# DX12 swapchain-resize regression harness (EF3)

The smallest surface that exercises the DX12 swapchain-resize **device-removal** path *without* launching
the full editor. The editor's window resize used to TDR-crash the dev PC on a large size jump (e.g.
4K→1080p) because `ResizeBuffers` recycled back buffers that an in-flight frame still bound. The
[GPU-hang safety rule](../../../CLAUDE.md) forbids relaunch-looping the real editor to chase that, so this
harness is the verification path instead.

## What it does
Creates a hidden Win32 HWND, builds a real `Dx12Device` + `Dx12SwapChain`, then loops `Resize()` over a
stress sequence — `0×0`/minimize, shrink, grow, 4K, **4K→1080p** (the historical removal jump), same-size
early-out, off-by-one — with a real `BeginFrame/EndFrame/Present` frame *between* every resize so genuine
in-flight GPU work exists at `Resize` time. After each step it asserts `DeviceRemovedReason.Success`.

## Run
From the repo root:

```sh
dotnet run -c Release -p:Platform=x64 --project Docs/Validation/dx12-resize-harness
BALLISTIC_DX12_OVERLAP=1 dotnet run -c Release -p:Platform=x64 --project Docs/Validation/dx12-resize-harness
```

`Exit 0` / `PASS` = no device removal across the whole sequence. Run BOTH variants: the default path is
`FramesInFlight==1` (EndFrame drains); `BALLISTIC_DX12_OVERLAP=1` is `FramesInFlight==2`, which leaves a
frame signalled only on the pipelined `frameFence`, so it proves `Dx12Device.Flush` drains that fence too
(not just the legacy render + upload fences).

## Why it lives here, not in BallisticEngine.Tests.Reflection
`Tests.Reflection` is a pure-reflection, CI-headless suite with **no** native GPU dependency. This harness
needs a real D3D12 device + swapchain (Vortice native), so keeping it separate preserves that suite's
no-GPU property — same reasoning as the `%TEMP%\bal-*-test` scratch harnesses. It is excluded from the
engine library build (`<Compile Remove="Docs\**\*.cs">` in the root csproj) and is not in the `.slnx`.
