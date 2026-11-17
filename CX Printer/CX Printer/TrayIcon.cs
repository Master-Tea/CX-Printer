using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace CX_Printer;

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage=0x8001; // WM_APP + 1, also used by the desktop smoke check.
    private const uint ContextMenu=0x007b,LeftDoubleClick=0x0203,RightButtonUp=0x0205,KeyboardSelect=0x0401;
    private readonly Window _window;
    private readonly Action<string> _reportError;
    private readonly nint _handle;
    private readonly System.Drawing.Icon _icon;
    private readonly uint _taskbarCreated;
    private readonly SubclassProc _callback;
    private NotifyIconData _data;
    private bool _added,_subclassed,_hidden,_disposed;

    public TrayIcon(Window window,string iconPath,Action<string> reportError)
    {
        _window=window; _reportError=reportError;
        _handle=WinRT.Interop.WindowNative.GetWindowHandle(window);
        _callback=WindowProc;
        // Keep the ICO alive for the full Shell_NotifyIcon lifetime.
        _icon=new System.Drawing.Icon(iconPath,32,32);
        _taskbarCreated=RegisterWindowMessageW("TaskbarCreated");
        _data=new()
        {
            Size=(uint)Marshal.SizeOf<NotifyIconData>(),Window=_handle,Id=1,Flags=0x0087,
            Callback=CallbackMessage,Icon=_icon.Handle,Tip="CX Printer · 双击显示主窗口",Info="",InfoTitle=""
        };
        try
        {
            if (!SetWindowSubclass(_handle,_callback,1,0))
                throw new Win32Exception(Marshal.GetLastWin32Error(),"无法连接系统托盘消息。");
            _subclassed=true;
            if (!EnsureAdded()) throw new Win32Exception(Marshal.GetLastWin32Error(),"无法创建系统托盘图标。");
            _window.AppWindow.Changed+=WindowChanged;
            TraceState($"ready hwnd={_handle:X}, size={_data.Size}, icon={_data.Icon:X}, registered={_added}");
        }
        catch (Exception ex) { TraceState(ex.ToString()); Dispose(); throw; }
    }
    private static void TraceState(string message)
    {
        if (Environment.GetEnvironmentVariable("CXPRINTER_DEBUG_SERVICE")!="1") return;
        try
        {
            var directory=Path.Combine(AppContext.BaseDirectory,"DebugUI"); Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory,"tray.log"),$"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public void Minimize()
    {
        if (!_disposed && _window.AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
    }
    private bool EnsureAdded()
    {
        if (_disposed) return false;
        if (_added) return true;
        _added=Shell_NotifyIconW(0,ref _data); // NIM_ADD.
        TraceState($"NIM_ADD registered={_added}, error={Marshal.GetLastWin32Error()}, hwnd={_handle:X}");
        if (_added)
        {
            _data.Version=4;
            Shell_NotifyIconW(4,ref _data); // NIM_SETVERSION: keyboard/context-menu support.
        }
        return _added;
    }
    private void WindowChanged(AppWindow sender,AppWindowChangedEventArgs args)
    {
        if (!_hidden && sender.Presenter is OverlappedPresenter { State:OverlappedPresenterState.Minimized })
            _window.DispatcherQueue.TryEnqueue(HideMinimizedWindow);
    }
    private void HideMinimizedWindow()
    {
        if (_disposed || _hidden || _window.AppWindow.Presenter is not OverlappedPresenter { State:OverlappedPresenterState.Minimized }) return;
        // Never hide the last way back to the window if Explorer cannot create the icon.
        if (!EnsureAdded()) { _reportError("系统托盘暂不可用，窗口保留在任务栏。"); return; }
        _hidden=true;
        _window.AppWindow.Hide();
    }
    private void Restore()
    {
        if (_disposed) return;
        ShowWindow(_handle,9); // SW_RESTORE also preserves the previous maximized state.
        _window.AppWindow.Show();
        _window.Activate();
        SetForegroundWindow(_handle);
        _hidden=false;
    }
    private nint WindowProc(nint window,uint message,nuint wParam,nint lParam,nuint id,nuint data)
    {
        // Keep managed UI work out of the unmanaged callback and its exception boundary.
        if (!_disposed && message==CallbackMessage)
        {
            var notification=(uint)((long)lParam&0xffff);
            if (notification is LeftDoubleClick or KeyboardSelect) _window.DispatcherQueue.TryEnqueue(Restore);
            else if (notification is ContextMenu or RightButtonUp) _window.DispatcherQueue.TryEnqueue(ShowMenu);
            return 0;
        }
        if (!_disposed && _taskbarCreated!=0 && message==_taskbarCreated)
            _window.DispatcherQueue.TryEnqueue(()=>
            {
                _added=false;
                if (!EnsureAdded()) { if (_hidden) Restore(); _reportError("资源管理器重启后未能恢复托盘图标，可以重新最小化重试。"); }
            });
        return DefSubclassProc(window,message,wParam,lParam);
    }
    private void ShowMenu()
    {
        if (_disposed) return;
        var menu=CreatePopupMenu();
        if (menu==0) { _reportError("无法打开系统托盘菜单，请双击图标恢复窗口。"); return; }
        try
        {
            AppendMenuW(menu,0,1,"显示主窗口");
            AppendMenuW(menu,0x0800,0,null);
            AppendMenuW(menu,0,2,"退出 CX Printer");
            GetCursorPos(out var point);
            SetForegroundWindow(_handle);
            var command=TrackPopupMenuEx(menu,0x0102,point.X,point.Y,_handle,0); // TPM_RETURNCMD | TPM_RIGHTBUTTON.
            PostMessageW(_handle,0,0,0); // Let Explorer dismiss the menu when focus changes.
            if (command==1) Restore();
            else if (command==2) _window.Close();
        }
        finally { DestroyMenu(menu); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed=true;
        _window.AppWindow.Changed-=WindowChanged;
        if (_added) { Shell_NotifyIconW(2,ref _data); _added=false; }
        if (_subclassed) { RemoveWindowSubclass(_handle,_callback,1); _subclassed=false; }
        _icon.Dispose();
    }

    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id,Flags,Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X,Y; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint window,uint message,nuint wParam,nint lParam,nuint id,nuint data);
    [DllImport("comctl32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window,SubclassProc callback,nuint id,nuint data);
    [DllImport("comctl32.dll")] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window,SubclassProc callback,nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window,uint message,nuint wParam,nint lParam);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message,ref NotifyIconData data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string message);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint menu,uint flags,nuint id,string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu,uint flags,int x,int y,nint window,nint parameters);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessageW(nint window,uint message,nuint wParam,nint lParam);
}
