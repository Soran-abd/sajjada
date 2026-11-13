using System.Runtime.InteropServices;
using System.Windows;

namespace Sajjada;

/// <summary>
/// The icons on the desktop, through Explorer's own desktop view. Only where an icon is
/// drawn changes, exactly as if it were dragged there by hand: files and folders are never
/// moved, renamed, opened or changed.
/// </summary>
sealed class DesktopIcons : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    /// <summary>A desktop icon: its full parsing name (a file path for files) and its top-left in view pixels.</summary>
    public record Icon(string Name, IntPtr Pidl, POINT Pos);

    // ---------- COM ----------

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int cookie);
        void RegisterPending(int threadId, ref object loc, ref object locRoot, int swClass, out int cookie);
        void Revoke(int cookie);
        void OnNavigate(int cookie, ref object loc);
        void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(ref object loc, ref object locRoot, int swClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object QueryService(ref Guid service, ref Guid riid);
    }

    // Only the slots up to QueryActiveShellView are used; the others just keep the order.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(int enter);
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        void QueryActiveShellView(out IShellView view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView
    {
        void GetWindow(out IntPtr hwnd);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFolderView
    {
        void GetCurrentViewMode(out uint mode);
        void SetCurrentViewMode(uint mode);
        void GetFolder(ref Guid riid, out IntPtr ppv);
        void Item(int index, out IntPtr pidl);
        void ItemCount(uint flags, out int count);
        void Items(uint flags, ref Guid riid, out IntPtr ppv);
        void GetSelectionMarkedItem(out int item);
        void GetFocusedItem(out int item);
        void GetItemPosition(IntPtr pidl, out POINT pt);
        void GetSpacing(ref POINT pt);
        void GetDefaultSpacing(out POINT pt);
        [PreserveSig] int GetAutoArrange();
        void SelectItem(int item, uint flags);
        void SelectAndPositionItems(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls,
                                    [MarshalAs(UnmanagedType.LPArray)] POINT[] points, uint flags);
    }

    static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    const int CSIDL_DESKTOP = 0, SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
    const uint SVGIO_ALLVIEW = 2, SVSI_POSITIONITEM = 0x80, SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr name);

    [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);

    // ---------- the desktop view ----------

    readonly IFolderView view;
    readonly IntPtr list;
    readonly List<IntPtr> owned = new();

    DesktopIcons(IFolderView view, IntPtr list) => (this.view, this.list) = (view, list);

    /// <summary>The desktop as Explorer shows it right now, or null if Explorer is not there (restarting, say).</summary>
    public static DesktopIcons? Open()
    {
        try
        {
            if (Type.GetTypeFromCLSID(CLSID_ShellWindows) is not { } type ||
                Activator.CreateInstance(type) is not IShellWindows windows) return null;
            object loc = CSIDL_DESKTOP, root = null!;
            var found = windows.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
            if (found is not IServiceProvider provider) return null;
            Guid sid = SID_STopLevelBrowser, iid = typeof(IShellBrowser).GUID;
            if (provider.QueryService(ref sid, ref iid) is not IShellBrowser browser) return null;
            browser.QueryActiveShellView(out var shellView);
            shellView.GetWindow(out var defView);
            var list = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            if (list == IntPtr.Zero) list = defView;
            return shellView is IFolderView folderView ? new DesktopIcons(folderView, list) : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException) { return null; }
    }

    /// <summary>Icons cannot be placed by hand while "Auto arrange icons" is on.</summary>
    public bool AutoArrange => view.GetAutoArrange() == 0;

    /// <summary>The size of one icon's cell, in view pixels.</summary>
    public POINT Spacing()
    {
        var p = new POINT();
        view.GetSpacing(ref p);
        if (p.X <= 0 || p.Y <= 0) view.GetDefaultSpacing(out p);
        return p;
    }

    public List<Icon> Items()
    {
        var icons = new List<Icon>();
        view.ItemCount(SVGIO_ALLVIEW, out var count);
        for (var i = 0; i < count; i++)
        {
            view.Item(i, out var pidl);
            if (pidl == IntPtr.Zero) continue;
            owned.Add(pidl);
            // The desktop is the root of the shell namespace, so its child IDs are absolute too.
            if (SHGetNameFromIDList(pidl, SIGDN_DESKTOPABSOLUTEPARSING, out var text) != 0) continue;
            var name = Marshal.PtrToStringUni(text) ?? "";
            CoTaskMemFree(text);
            view.GetItemPosition(pidl, out var pos);
            icons.Add(new Icon(name, pidl, pos));
        }
        return icons;
    }

    /// <summary>Moves each icon's picture to the given top-left. Nothing else about it changes.</summary>
    public void Place(IReadOnlyList<Icon> icons, IReadOnlyList<POINT> spots)
    {
        if (icons.Count == 0) return;
        view.SelectAndPositionItems((uint)icons.Count, icons.Select(i => i.Pidl).ToArray(), spots.ToArray(), SVSI_POSITIONITEM);
    }

    public Point ToScreen(POINT p)
    {
        ClientToScreen(list, ref p);
        return new Point(p.X, p.Y);
    }

    public POINT FromScreen(Point p)
    {
        var q = new POINT { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) };
        ScreenToClient(list, ref q);
        return q;
    }

    public void Dispose()
    {
        foreach (var pidl in owned) CoTaskMemFree(pidl);
        owned.Clear();
    }

    /// <summary>
    /// True for things that are icons on the desktop: directly in the user's or the shared Desktop
    /// folder. Decided from the path alone, so it is safe to ask in the middle of a drag.
    /// </summary>
    public static bool OnDesktop(string path)
    {
        var parent = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(path));
        if (parent is null) return false;
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            var dir = Environment.GetFolderPath(folder);
            if (dir.Length > 0 && string.Equals(System.IO.Path.TrimEndingDirectorySeparator(dir), parent, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
