using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Sitepane;

/// <summary>
/// Per-site Windows app identity (AppUserModelID): each site gets its own taskbar button, and
/// pinning it relaunches that site with its icon instead of bare Sitepane.exe. Plus Start menu
/// shortcuts carrying the same identity.
/// </summary>
internal static class WindowsShell
{
    private static readonly Guid AppUserModelKeys = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint PID_RelaunchCommand = 2;
    private const uint PID_RelaunchIconResource = 3;
    private const uint PID_RelaunchDisplayNameResource = 4;
    private const uint PID_ID = 5;
    private const ushort VT_LPWSTR = 31;

    private static string RelaunchCommand(Uri url) => $"\"{Environment.ProcessPath}\" \"{url.AbsoluteUri}\"";

    /// <summary>
    /// Sets taskbar grouping and, once the icon exists, the command, name and icon used by a pin.
    /// Until then, the taskbar uses the live window icon.
    /// </summary>
    public static void SetWindowIdentity(IntPtr hwnd, string appId, (Uri Url, string Name, string IconFile)? relaunch)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, ref iid, out var store));
        try
        {
            SetString(store, PID_ID, appId);
            if (relaunch is { } r)
            {
                SetString(store, PID_RelaunchCommand, RelaunchCommand(r.Url));
                SetString(store, PID_RelaunchDisplayNameResource, r.Name);
                SetString(store, PID_RelaunchIconResource, r.IconFile + ",0");
            }
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>Creates or replaces "Start menu\Programs\{name}.lnk"; returns its path.</summary>
    public static string CreateStartMenuShortcut(string? appId, Uri url, string name, string iconPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(Environment.ProcessPath!);
            link.SetArguments($"\"{url.AbsoluteUri}\"");
            link.SetWorkingDirectory(AppContext.BaseDirectory);
            link.SetDescription(url.AbsoluteUri);
            link.SetIconLocation(iconPath, 0);
            if (appId is not null)
            {
                var store = (IPropertyStore)link;
                SetString(store, PID_ID, appId);
                Marshal.ThrowExceptionForHR(store.Commit());
            }

            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), SafeFileName(name) + ".lnk");
            ((IPersistFile)link).Save(file, true);
            return file;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>"Planner — tasks for today" → "Planner"; falls back to the host.</summary>
    public static string AppName(string? pageName, Uri url)
    {
        var name = pageName?.Split([" — ", " – ", " | ", " - ", " · ", ": "], 2, StringSplitOptions.None)[0].Trim();
        return string.IsNullOrWhiteSpace(name) ? url.Host : name;
    }

    public const string PageNameScript = """
        (() => {
          const meta = (selector) => {
            const value = document.querySelector(selector)?.content?.trim();
            return value || null;
          };
          return meta('meta[name="application-name"]')
            || meta('meta[name="apple-mobile-web-app-title"]')
            || meta('meta[property="og:site_name"]')
            || document.title;
        })()
        """;

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length > 0 ? safe : "Sitepane app";
    }

    private static void SetString(IPropertyStore store, uint pid, string value)
    {
        var key = new PROPERTYKEY { fmtid = AppUserModelKeys, pid = pid };
        var pv = new PROPVARIANT { vt = VT_LPWSTR, pointer = Marshal.StringToCoTaskMemUni(value) };
        try
        {
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref pv));
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pv);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
        [FieldOffset(8)] private long padding;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    /// <summary>Vtable order matters; getters are never called, so their parameters are opaque.</summary>
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(IntPtr file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(IntPtr name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments(IntPtr args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation(IntPtr path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
