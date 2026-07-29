using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinDayFlow.App.Services;

internal sealed class TrayIconService : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8000 + 42;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmLeftButtonDoubleClick = 0x0203;
    private const uint WmRightButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint WmNull = 0x0000;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNonotify = 0x0080;
    private const uint TpmReturnCommand = 0x0100;
    private const uint OpenCommand = 1;
    private const uint ExitCommand = 2;
    private const nuint SubclassId = 1;

    private readonly nint _windowHandle;
    private readonly Action _open;
    private readonly Action _exit;
    private readonly SubclassProcedure _subclassProcedure;
    private readonly uint _taskbarCreatedMessage;
    private NotifyIconData _iconData;
    private nint _iconHandle;
    private bool _ownsIcon;
    private bool _disposed;

    public TrayIconService(
        nint windowHandle,
        string iconPath,
        Action open,
        Action exit)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A tray icon requires a window handle.", nameof(windowHandle));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(iconPath);
        _windowHandle = windowHandle;
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _subclassProcedure = WindowSubclassProcedure;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreatedMessage == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _iconHandle = LoadImage(
            0,
            Path.GetFullPath(iconPath),
            ImageIcon,
            32,
            32,
            LrLoadFromFile);
        _ownsIcon = _iconHandle != 0;
        if (_iconHandle == 0)
        {
            _iconHandle = LoadIcon(0, new nint(32512));
        }
        if (_iconHandle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _iconData = CreateIconData();
        if (!SetWindowSubclass(
                _windowHandle,
                _subclassProcedure,
                SubclassId,
                0))
        {
            ReleaseIcon();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!ShellNotifyIcon(NimAdd, ref _iconData))
        {
            RemoveWindowSubclass(
                _windowHandle,
                _subclassProcedure,
                SubclassId);
            ReleaseIcon();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = ShellNotifyIcon(NimDelete, ref _iconData);
        _ = RemoveWindowSubclass(
            _windowHandle,
            _subclassProcedure,
            SubclassId);
        ReleaseIcon();
    }

    private nint WindowSubclassProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        _ = wParam;
        _ = subclassId;
        _ = referenceData;
        try
        {
            if (!_disposed && message == _taskbarCreatedMessage)
            {
                _ = ShellNotifyIcon(NimAdd, ref _iconData);
                return 0;
            }

            if (!_disposed && message == CallbackMessage)
            {
                var notification = unchecked((uint)lParam.ToInt64());
                if (notification is WmLeftButtonUp or WmLeftButtonDoubleClick)
                {
                    _open();
                    return 0;
                }

                if (notification is WmRightButtonUp or WmContextMenu)
                {
                    ShowContextMenu();
                    return 0;
                }
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            _ = AppendMenu(menu, MfString, OpenCommand, "打开 WinDayFlow");
            _ = AppendMenu(menu, MfSeparator, 0, null);
            _ = AppendMenu(menu, MfString, ExitCommand, "退出");
            _ = GetCursorPos(out var cursor);
            var command = TrackPopupMenu(
                menu,
                TpmRightButton | TpmNonotify | TpmReturnCommand,
                cursor.X,
                cursor.Y,
                0,
                _windowHandle,
                0);
            _ = PostMessage(_windowHandle, WmNull, 0, 0);
            if (command == OpenCommand)
            {
                _open();
            }
            else if (command == ExitCommand)
            {
                _exit();
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private NotifyIconData CreateIconData()
    {
        return new NotifyIconData
        {
            Size = checked((uint)Marshal.SizeOf<NotifyIconData>()),
            WindowHandle = _windowHandle,
            Id = IconId,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = CallbackMessage,
            IconHandle = _iconHandle,
            Tip = "WinDayFlow - 后台录制与分析",
            Info = string.Empty,
            InfoTitle = string.Empty,
        };
    }

    private void ReleaseIcon()
    {
        if (_ownsIcon && _iconHandle != 0)
        {
            _ = DestroyIcon(_iconHandle);
        }

        _iconHandle = 0;
        _ownsIcon = false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Point
    {
        public readonly int X;
        public readonly int Y;
    }

    private delegate nint SubclassProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        SubclassProcedure procedure,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        SubclassProcedure procedure,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(
        nint instance,
        string name,
        uint type,
        int width,
        int height,
        uint loadFlags);

    [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    private static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(
        nint menu,
        uint flags,
        nuint item,
        string? text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint windowHandle,
        nint rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string value);
}
