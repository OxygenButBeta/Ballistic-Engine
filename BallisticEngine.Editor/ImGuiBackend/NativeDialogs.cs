using System.Runtime.InteropServices;

namespace BallisticEngine.Editor;

// Native Windows file/folder dialogs via the modern IFileOpenDialog COM API (no WinForms / NuGet
// dependency). Used by panels that need an OS browse dialog — e.g. the Build window's output folder.
// Windows-only; returns null on any failure or cancel (the caller keeps the typed-in value).
//
// COM vtable layout is UNFORGIVING: IFileOpenDialog inherits IFileDialog inherits IModalWindow, and
// every inherited method must be redeclared, in order, with the EXACT marshaled signature — a wrong
// slot or argument size corrupts the call stack and crashes the process. The declarations below match
// the Windows SDK (shobjidl_core.h) precisely; unused methods keep correct signatures, not empty stubs.
internal static class NativeDialogs {
    // Opens a native "select folder" dialog. initialDir seeds the starting location if it exists.
    // Returns the chosen absolute path, or null if the user cancelled / the dialog couldn't open.
    //
    // The common-item dialog requires a Single-Threaded-Apartment thread, but the editor's main loop
    // runs on the OpenTK/GLFW thread (MTA) — calling Show there crashes. So we marshal the whole
    // dialog onto a dedicated STA thread and block until it returns. The editor briefly stops painting
    // while the modal dialog is up, which is the expected behaviour for a native folder picker.
    public static string PickFolder(string title = "Select Folder", string initialDir = null) {
        if (!OperatingSystem.IsWindows())
            return null;

        string result = null;
        var staThread = new Thread(() => result = PickFolderSta(title, initialDir));
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();
        staThread.Join();
        return result;
    }

    // Opens a native "open file" dialog filtered to the given extensions. `filterName` labels the
    // filter (e.g. "Unity Package"); `extensions` are bare extensions WITH the dot (".unitypackage").
    // Returns the chosen absolute file path, or null on cancel / failure.
    public static string PickFile(string title, string filterName, string[] extensions, string initialDir = null) {
        if (!OperatingSystem.IsWindows())
            return null;

        string result = null;
        var staThread = new Thread(() => result = PickFileSta(title, filterName, extensions, initialDir));
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();
        staThread.Join();
        return result;
    }

    static string PickFileSta(string title, string filterName, string[] extensions, string initialDir) {
        IFileOpenDialog dialog = null;
        try {
            dialog = (IFileOpenDialog)new FileOpenDialogRcw();

            if (dialog.GetOptions(out uint options) != 0)
                return null;
            dialog.SetOptions(options | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_FILEMUSTEXIST);

            if (!string.IsNullOrEmpty(title))
                dialog.SetTitle(title);

            if (extensions is { Length: > 0 }) {
                var spec = string.Join(';', extensions.Select(e => "*" + e));
                ComdlgFilterSpec[] filters = [
                    new() { Name = filterName ?? "Supported", Spec = spec },
                    new() { Name = "All Files", Spec = "*.*" },
                ];
                dialog.SetFileTypes((uint)filters.Length, filters);
            }

            Guid shellItemId = typeof(IShellItem).GUID;
            if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir) &&
                SHCreateItemFromParsingName(initialDir, IntPtr.Zero, in shellItemId,
                    out IShellItem seed) == 0 && seed is not null) {
                dialog.SetFolder(seed);
                Marshal.ReleaseComObject(seed);
            }

            if (dialog.Show(GetActiveWindow()) != 0)
                return null;

            if (dialog.GetResult(out IShellItem item) != 0 || item is null)
                return null;
            item.GetDisplayName(SIGDN_FILESYSPATH, out string path);
            Marshal.ReleaseComObject(item);
            return path;
        }
        catch {
            return null;
        }
        finally {
            if (dialog is not null)
                Marshal.ReleaseComObject(dialog);
        }
    }

    static string PickFolderSta(string title, string initialDir) {
        IFileOpenDialog dialog = null;
        try {
            dialog = (IFileOpenDialog)new FileOpenDialogRcw();

            uint hr = dialog.GetOptions(out uint options);
            if (hr != 0)
                return null;
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);

            if (!string.IsNullOrEmpty(title))
                dialog.SetTitle(title);

            Guid shellItemId = typeof(IShellItem).GUID;
            if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir) &&
                SHCreateItemFromParsingName(initialDir, IntPtr.Zero, in shellItemId,
                    out IShellItem seed) == 0 && seed is not null) {
                dialog.SetFolder(seed);
                Marshal.ReleaseComObject(seed);
            }

            // S_OK = 0; anything else (incl. user cancel, ERROR_CANCELLED 0x800704C7) means "no selection".
            if (dialog.Show(GetActiveWindow()) != 0)
                return null;

            if (dialog.GetResult(out IShellItem item) != 0 || item is null)
                return null;
            item.GetDisplayName(SIGDN_FILESYSPATH, out string path);
            Marshal.ReleaseComObject(item);
            return path;
        }
        catch {
            return null;
        }
        finally {
            if (dialog is not null)
                Marshal.ReleaseComObject(dialog);
        }
    }

    // ---- COM interop ---------------------------------------------------------

    const uint FOS_PICKFOLDERS = 0x20;
    const uint FOS_FORCEFILESYSTEM = 0x40;
    const uint FOS_PATHMUSTEXIST = 0x800;
    const uint FOS_FILEMUSTEXIST = 0x1000;
    const uint SIGDN_FILESYSPATH = 0x80058000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(
        string path, IntPtr bindCtx, in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("user32.dll")]
    static extern IntPtr GetActiveWindow();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ComdlgFilterSpec {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    class FileOpenDialogRcw { }

    // IModalWindow (b4db1657-70d7-485e-8e3e-6fcb5a5c1802) → IFileDialog (42f85136-db7e-439c-85f1-e4075d135fc8)
    // → IFileOpenDialog (d57c7288-d4ad-4768-be02-9d969532d960). Single interface, full inherited chain.
    // All methods PreserveSig so HRESULTs come back as ints (no exception on a non-fatal failure).
    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOpenDialog {
        // --- IModalWindow ---
        [PreserveSig] int Show(IntPtr parent);

        // --- IFileDialog ---
        [PreserveSig] uint SetFileTypes(uint cFileTypes,
            [MarshalAs(UnmanagedType.LPArray)] ComdlgFilterSpec[] rgFilterSpec);
        [PreserveSig] uint SetFileTypeIndex(uint iFileType);
        [PreserveSig] uint GetFileTypeIndex(out uint piFileType);
        [PreserveSig] uint Advise(IntPtr pfde, out uint pdwCookie);
        [PreserveSig] uint Unadvise(uint dwCookie);
        [PreserveSig] uint SetOptions(uint fos);
        [PreserveSig] uint GetOptions(out uint pfos);
        [PreserveSig] uint SetDefaultFolder(IShellItem psi);
        [PreserveSig] uint SetFolder(IShellItem psi);
        [PreserveSig] uint GetFolder(out IShellItem ppsi);
        [PreserveSig] uint GetCurrentSelection(out IShellItem ppsi);
        [PreserveSig] uint SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] uint GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        [PreserveSig] uint SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        [PreserveSig] uint SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        [PreserveSig] uint SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        [PreserveSig] uint GetResult(out IShellItem ppsi);
        [PreserveSig] uint AddPlace(IShellItem psi, int fdap);
        [PreserveSig] uint SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        [PreserveSig] uint Close(int hr);
        [PreserveSig] uint SetClientGuid(in Guid guid);
        [PreserveSig] uint ClearClientData();
        [PreserveSig] uint SetFilter(IntPtr pFilter);

        // --- IFileOpenDialog ---
        [PreserveSig] uint GetResults(out IntPtr ppenum);
        [PreserveSig] uint GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem {
        [PreserveSig] uint BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppv);
        [PreserveSig] uint GetParent(out IShellItem ppsi);
        [PreserveSig] uint GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        [PreserveSig] uint GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] uint Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
