using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

internal static class NativeMethods
{
    private static readonly nint DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    internal const int GWL_EXSTYLE = -20;
    internal const long WS_EX_TOOLWINDOW = 0x00000080L;
    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    internal const int DWMWA_CLOAKED = 14;
    internal const int SW_SHOW = 5;
    internal const int SW_RESTORE = 9;
    internal static readonly nint HWND_TOPMOST = new(-1);
    internal static readonly nint HWND_NOTOPMOST = new(-2);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    internal const ushort VK_BACK = 0x08;
    internal const ushort VK_TAB = 0x09;
    internal const ushort VK_RETURN = 0x0D;
    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_ESCAPE = 0x1B;
    internal const ushort VK_PRIOR = 0x21;
    internal const ushort VK_NEXT = 0x22;
    internal const ushort VK_END = 0x23;
    internal const ushort VK_HOME = 0x24;
    internal const ushort VK_LEFT = 0x25;
    internal const ushort VK_UP = 0x26;
    internal const ushort VK_RIGHT = 0x27;
    internal const ushort VK_DOWN = 0x28;
    internal const ushort VK_DELETE = 0x2E;

    internal const uint INPUT_MOUSE = 0;
    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_UNICODE = 0x0004;
    internal const uint MOUSEEVENTF_MOVE = 0x0001;
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    internal const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    internal const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    internal const uint MOUSEEVENTF_WHEEL = 0x0800;
    internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

    internal delegate bool EnumWindowsProc(nint windowHandle, nint lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    internal static extern bool BringWindowToTop(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern nint GetShellWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(nint windowHandle, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    internal static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(nint windowHandle, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint windowHandle, ref int processId);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(nint windowHandle, out RECT rect);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint windowHandle, int attribute, out RECT value, int valueSize);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint windowHandle, int attribute, out int value, int valueSize);

    [DllImport("user32.dll")]
    internal static extern bool PrintWindow(nint windowHandle, nint hdcBlt, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool MoveWindow(nint windowHandle, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(nint dpiContext);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, INPUT[] inputs, int size);

    internal static void TryEnableDpiAwareness()
    {
        try
        {
            SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
        }
        catch
        {
        }
    }

    internal static INPUT CreateMouseInput(uint flags, int dx = 0, int dy = 0, uint mouseData = 0)
    {
        return new INPUT
        {
            Type = INPUT_MOUSE,
            Union = new InputUnion
            {
                MouseInput = new MOUSEINPUT
                {
                    Dx = dx,
                    Dy = dy,
                    MouseData = mouseData,
                    DwFlags = flags,
                },
            },
        };
    }

    internal static INPUT CreateVirtualKeyInput(ushort virtualKey, bool keyUp)
    {
        return new INPUT
        {
            Type = INPUT_KEYBOARD,
            Union = new InputUnion
            {
                KeyboardInput = new KEYBDINPUT
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KEYEVENTF_KEYUP : 0,
                },
            },
        };
    }

    internal static INPUT CreateUnicodeKeyInput(char character, bool keyUp)
    {
        return new INPUT
        {
            Type = INPUT_KEYBOARD,
            Union = new InputUnion
            {
                KeyboardInput = new KEYBDINPUT
                {
                    ScanCode = character,
                    Flags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                },
            },
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT MouseInput;

        [FieldOffset(0)]
        public KEYBDINPUT KeyboardInput;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public nint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;

        public Rectangle ToRectangle() => new(Left, Top, Width, Height);
    }
}
