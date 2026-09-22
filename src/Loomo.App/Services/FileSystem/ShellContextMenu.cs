using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace sk0ya.Loomo.App.Services;

/// <summary>Windows Explorer の右クリックメニュー（Shell の <c>IContextMenu</c>）をそのまま出す。
/// 中身は Shell がその場で組み立てるので、共有・送る・プロパティ・7-Zip／Git などの Shell 拡張まで
/// Explorer と同じものが並ぶ——Loomo 側で1つずつ写すより確実で、入れた拡張も漏れない。
/// ただし「名前の変更」と「削除」は Explorer のビュー前提（その場編集／履歴に積まれない）なので、
/// 呼び出し側の同じ操作へ振り替える。</summary>
internal static class ShellContextMenu
{
    /// <summary>カーソル位置にメニューを出す。<paramref name="paths"/> の先頭は右クリックした項目。
    /// 選んだ動詞が <paramref name="handleVerb"/> で処理済みになれば Shell には渡さない——そのとき渡すのは
    /// メニューが実際に対象にしたパス（親が揃わず絞った後）で、見せたメニューと違う項目を触らないため。
    /// 対象を Shell が解決できなければ false。</summary>
    public static bool Show(
        Window owner, IReadOnlyList<string> paths, Func<string, IReadOnlyList<string>, bool>? handleVerb = null)
    {
        GetCursorPos(out var point);
        return Run(owner, paths, (menu, hwnd, contextMenu, targets) =>
        {
            var hook = new HwndSourceHook((IntPtr _, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
                => ForwardMenuMessage(contextMenu, msg, wParam, lParam, ref handled));
            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(hook);
            int command;
            try
            {
                command = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, point.X, point.Y, hwnd, IntPtr.Zero);
            }
            finally
            {
                source?.RemoveHook(hook);
            }
            if (command <= 0)
                return;

            var verb = GetVerb(contextMenu, (uint)(command - FirstCommandId));
            if (verb is not null && handleVerb?.Invoke(verb, targets) == true)
                return;
            Invoke(contextMenu, hwnd, new IntPtr(command - FirstCommandId), IntPtr.Zero, point);
        });
    }

    /// <summary>メニューを出さずに動詞（<c>properties</c> など）を実行する。</summary>
    public static bool InvokeVerb(Window owner, IReadOnlyList<string> paths, string verb)
        => Run(owner, paths, (_, hwnd, contextMenu, _) =>
        {
            var ansi = Marshal.StringToHGlobalAnsi(verb);
            var unicode = Marshal.StringToHGlobalUni(verb);
            try
            {
                GetCursorPos(out var point);
                Invoke(contextMenu, hwnd, ansi, unicode, point);
            }
            finally
            {
                Marshal.FreeHGlobal(ansi);
                Marshal.FreeHGlobal(unicode);
            }
        });

    private static bool Run(
        Window owner, IReadOnlyList<string> paths, Action<IntPtr, IntPtr, IContextMenu, IReadOnlyList<string>> use)
    {
        var targets = SameParentTargets(paths);
        if (targets.Count == 0)
            return false;

        var hwnd = new WindowInteropHelper(owner).Handle;
        var pidls = new List<IntPtr>();
        IShellFolder? parent = null;
        IContextMenu? contextMenu = null;
        var menu = IntPtr.Zero;
        try
        {
            var children = new IntPtr[targets.Count];
            for (var i = 0; i < targets.Count; i++)
            {
                if (SHParseDisplayName(targets[i], IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                    return false;
                pidls.Add(pidl);
                if (SHBindToParent(pidl, typeof(IShellFolder).GUID, out var folder, out children[i]) != 0)
                    return false;
                // 親はどれも同じフォルダー（SameParentTargets で揃えてある）なので最初のものを使う。
                if (parent is null) parent = folder;
                else Marshal.ReleaseComObject(folder);
            }

            var iid = typeof(IContextMenu).GUID;
            if (parent!.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iid, IntPtr.Zero, out var unknown) != 0
                || unknown == IntPtr.Zero)
                return false;
            contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(unknown);
            Marshal.Release(unknown);

            menu = CreatePopupMenu();
            var flags = CmfNormal | CmfCanRename;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                flags |= CmfExtendedVerbs;
            if (contextMenu.QueryContextMenu(menu, 0, FirstCommandId, LastCommandId, flags) < 0)
                return false;

            use(menu, hwnd, contextMenu, targets);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (contextMenu is not null) Marshal.ReleaseComObject(contextMenu);
            if (parent is not null) Marshal.ReleaseComObject(parent);
            foreach (var pidl in pidls) Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>Shell のメニューは同じフォルダーの項目しかまとめて扱えない。親が揃わなければ
    /// 先頭だけにする——呼び出し側は右クリックした項目を先頭に置くこと。</summary>
    internal static IReadOnlyList<string> SameParentTargets(IReadOnlyList<string> paths)
    {
        var valid = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (valid.Count <= 1)
            return valid;
        var parent = ParentOf(valid[0]);
        return valid.All(path => string.Equals(ParentOf(path), parent, StringComparison.OrdinalIgnoreCase))
            ? valid
            : [valid[0]];
    }

    private static string? ParentOf(string path)
    {
        try { return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)); }
        catch (ArgumentException) { return null; }
    }

    private static string? GetVerb(IContextMenu contextMenu, uint offset)
    {
        var buffer = Marshal.AllocHGlobal(MaxVerbChars * 2);
        try
        {
            return contextMenu.GetCommandString(new UIntPtr(offset), GcsVerbW, IntPtr.Zero, buffer, MaxVerbChars) == 0
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void Invoke(IContextMenu contextMenu, IntPtr hwnd, IntPtr verb, IntPtr verbW, NativePoint point)
    {
        var info = new CmInvokeCommandInfoEx
        {
            cbSize = Marshal.SizeOf<CmInvokeCommandInfoEx>(),
            fMask = CmicMaskUnicode | CmicMaskPtInvoke,
            hwnd = hwnd,
            lpVerb = verb,
            lpVerbW = verbW == IntPtr.Zero ? verb : verbW,
            nShow = SwShowNormal,
            ptInvoke = point,
        };
        contextMenu.InvokeCommand(ref info);
    }

    // 「送る」「新規作成」などの子メニューは開いた瞬間に Shell が中身を詰め、アイコンも Shell が描く。
    // その合図（WM_INITMENUPOPUP／WM_DRAWITEM…）を渡さないと空の子メニューになる。
    private static IntPtr ForwardMenuMessage(IContextMenu contextMenu, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is not (WmInitMenuPopup or WmDrawItem or WmMeasureItem or WmMenuChar))
            return IntPtr.Zero;
        try
        {
            if (contextMenu is IContextMenu3 menu3)
            {
                if (menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == 0)
                {
                    handled = true;
                    return result;
                }
            }
            else if (contextMenu is IContextMenu2 menu2 && msg != WmMenuChar)
            {
                if (menu2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
            }
        }
        catch (COMException)
        {
        }
        return IntPtr.Zero;
    }

    private const uint FirstCommandId = 1;
    private const uint LastCommandId = 0x7FFF;
    private const uint CmfNormal = 0x0;
    private const uint CmfCanRename = 0x10;
    private const uint CmfExtendedVerbs = 0x100;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint GcsVerbW = 0x4;
    private const int MaxVerbChars = 260;
    private const uint CmicMaskUnicode = 0x00004000;
    private const uint CmicMaskPtInvoke = 0x20000000;
    private const int SwShowNormal = 1;
    private const int WmDrawItem = 0x002B;
    private const int WmMeasureItem = 0x002C;
    private const int WmInitMenuPopup = 0x0117;
    private const int WmMenuChar = 0x0120;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CmInvokeCommandInfoEx
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public NativePoint ptInvoke;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name,
            IntPtr pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIdList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name,
            uint flags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CmInvokeCommandInfoEx info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cchMax);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CmInvokeCommandInfoEx info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CmInvokeCommandInfoEx info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellFolder folder, out IntPtr lastPidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpmParams);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}
