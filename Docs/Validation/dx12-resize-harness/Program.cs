using System;
using System.Runtime.InteropServices;
using BallisticEngine.DX12;
using Vortice.Direct3D12;

// EF3 swapchain-resize regression harness (editor-fixes-plan.md). The smallest surface that exercises the
// DX12 swapchain-resize device-removal path WITHOUT launching the full editor — the GPU-hang safety rule
// forbids relaunch-looping the real editor on a resize that historically TDR-crashed the dev PC.
//
// It creates a hidden Win32 HWND, builds a Dx12Device + Dx12SwapChain, then drives Resize() over a stress
// sequence (0×0/minimize, shrink, grow, 4K, 4K→1080p, same-size early-out, off-by-one) with a REAL
// BeginFrame/EndFrame/Present frame between resizes so genuine in-flight GPU work exists at Resize time —
// the exact case that removed the device before Dx12SwapChain.Resize's drained sequence + Dx12Device.Flush
// were hardened to wait the pipelined frameFence (P0b) as well as the render + upload fences.
//
// Run (repo root):
//   dotnet run -c Release -p:Platform=x64 --project Docs/Validation/dx12-resize-harness
//   BALLISTIC_DX12_OVERLAP=1 dotnet run -c Release -p:Platform=x64 --project Docs/Validation/dx12-resize-harness
// Exit 0 = no device removal across the whole sequence (default AND overlap). Exit 1 = a removal was seen.

static class Program {
    // ---- minimal Win32 hidden window (just a valid HWND for CreateSwapChainForHwnd) -----------------
    const int CW_USEDEFAULT = unchecked((int)0x80000000);
    const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEX {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public IntPtr lpszMenuName; public IntPtr lpszClassName; public IntPtr hIconSm;
    }

    delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandleW(string name);

    static WndProc _wndProcDelegate;   // keep the delegate alive for the window's lifetime

    static IntPtr CreateHiddenWindow() {
        _wndProcDelegate = (h, m, w, l) => DefWindowProcW(h, m, w, l);
        IntPtr hInst = GetModuleHandleW(null);
        var wc = new WNDCLASSEX {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInst,
            lpszClassName = Marshal.StringToHGlobalUni("BalResizeHarnessWnd"),
        };
        RegisterClassExW(ref wc);
        // Not shown (no WS_VISIBLE) — only a valid HWND is needed for the flip-model swapchain.
        IntPtr hwnd = CreateWindowExW(0, "BalResizeHarnessWnd", "ResizeHarness", WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT, 1280, 720, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new Exception($"CreateWindowExW failed (err {Marshal.GetLastWin32Error()})");
        return hwnd;
    }

    // ------------------------------------------------------------------------------------------------
    static int failures;

    static void Assert(bool cond, string msg) {
        if (cond) Console.WriteLine($"  ok   {msg}");
        else { Console.WriteLine($"  FAIL {msg}"); failures++; }
    }

    static int Main() {
        bool overlap = Environment.GetEnvironmentVariable("BALLISTIC_DX12_OVERLAP") == "1";
        Console.WriteLine($"[EF3 resize-harness] overlap={overlap} (FramesInFlight {(overlap ? ">1" : "1")})");

        IntPtr hwnd = CreateHiddenWindow();
        var dev = new Dx12Device(enableDebugLayer: false);   // headless; GBV off (fast + TDR-safe)
        Dx12Backend.Initialize(dev);
        Console.WriteLine($"[EF3 resize-harness] device on '{dev.AdapterDescription}'");

        var sc = new Dx12SwapChain(dev, hwnd, 1280, 720);

        // One real present frame: a pipelined frame opens (so P0b leaves a frame signalled on frameFence),
        // the UI list clears + executes (EndFrame → dev.Flush), then present. This puts genuine GPU work in
        // flight so the next Resize MUST drain it — the case that historically removed the device.
        void Frame() {
            dev.BeginFrame();
            sc.BeginFrame(0.05f, 0.05f, 0.06f);
            sc.EndFrame();
            dev.EndFrame();
            sc.Present(vsync: false);
        }

        (int w, int h)[] seq = {
            (1280, 720),   // baseline == ctor size → same-size early-out exercised
            (0, 0),        // minimize → clamped to 1×1
            (1, 1),        // explicit tiny
            (640, 360),    // shrink
            (2560, 1440),  // grow
            (3840, 2160),  // 4K
            (1920, 1080),  // 4K → 1080p (the historical device-removal jump)
            (1920, 1080),  // same-size early-out
            (800, 600),    // odd shrink
            (1281, 721),   // off-by-one (non-aligned)
            (1280, 720),   // back to baseline
        };

        for (int rep = 0; rep < 3; rep++) {       // repeat to catch ring/state drift
            foreach (var (w, h) in seq) {
                Frame();                           // in-flight GPU work before the resize
                sc.Resize(w, h);                   // THE path under test
                Frame();                           // a frame on the freshly-resized swapchain
                var reason = dev.Device.DeviceRemovedReason;
                Assert(reason.Success, $"rep{rep} resize {w}x{h} -> reason={reason} sc={sc.Width}x{sc.Height}");
                if (!reason.Success) {
                    Console.WriteLine($"  DRED={dev.DrainDredReport()}");
                    goto done;                     // stop on first removal — do not keep hammering the GPU
                }
            }
        }

    done:
        sc.Dispose();                              // teardown drains via Flush too — must not remove either
        var teardownReason = dev.Device.DeviceRemovedReason;
        Assert(teardownReason.Success, $"post-dispose reason={teardownReason}");
        dev.Dispose();
        DestroyWindow(hwnd);

        Console.WriteLine($"[EF3 resize-harness] {(failures == 0 ? "PASS" : $"FAIL ({failures})")}");
        return failures == 0 ? 0 : 1;
    }
}
