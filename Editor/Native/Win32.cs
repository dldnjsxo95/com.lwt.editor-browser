using System;
using System.Runtime.InteropServices;

namespace EditorBrowser.Native
{
    /// <summary>
    /// Windows user32 P/Invoke 정의. 본 패키지의 ExternalBrowserHost 전용.
    /// 다른 플랫폼에서 본 타입을 참조하더라도 호출 직전에만 검사하면 된다(현재 Editor-only).
    /// </summary>
    internal static class Win32
    {
        // ----- GetWindowLong / SetWindowLong 인덱스 -----
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int GWLP_HWNDPARENT = -8;

        // ----- 윈도우 스타일 (WS_*) -----
        public const uint WS_OVERLAPPED   = 0x00000000;
        public const uint WS_POPUP        = 0x80000000;
        public const uint WS_CHILD        = 0x40000000;
        public const uint WS_VISIBLE      = 0x10000000;
        public const uint WS_CLIPSIBLINGS = 0x04000000;
        public const uint WS_CLIPCHILDREN = 0x02000000;
        public const uint WS_CAPTION      = 0x00C00000;
        public const uint WS_BORDER       = 0x00800000;
        public const uint WS_DLGFRAME     = 0x00400000;
        public const uint WS_SYSMENU      = 0x00080000;
        public const uint WS_THICKFRAME   = 0x00040000;
        public const uint WS_MINIMIZEBOX  = 0x00020000;
        public const uint WS_MAXIMIZEBOX  = 0x00010000;

        // ----- 확장 스타일 (WS_EX_*) -----
        public const uint WS_EX_TOOLWINDOW = 0x00000080;
        public const uint WS_EX_APPWINDOW  = 0x00040000;
        public const uint WS_EX_NOACTIVATE = 0x08000000;

        // ----- SetWindowPos 플래그 -----
        public const uint SWP_NOSIZE         = 0x0001;
        public const uint SWP_NOMOVE         = 0x0002;
        public const uint SWP_NOZORDER       = 0x0004;
        public const uint SWP_NOACTIVATE     = 0x0010;
        public const uint SWP_FRAMECHANGED   = 0x0020;
        public const uint SWP_SHOWWINDOW     = 0x0040;
        public const uint SWP_HIDEWINDOW     = 0x0080;
        public const uint SWP_NOOWNERZORDER  = 0x0200;
        public const uint SWP_ASYNCWINDOWPOS = 0x4000;

        // ----- ShowWindow 명령 -----
        public const int SW_HIDE           = 0;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_SHOW           = 5;

        // ----- 구조체 -----
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        // ----- 함수 -----
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        // 32/64 bit 분기: GetWindowLong은 32비트 슬롯, GetWindowLongPtr이 64비트 안전.
        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
                : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // EnumWindows로 PID 매칭 — Process.MainWindowHandle 의존 회피용.
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        // ----- Z-order 상수 -----
        public static readonly IntPtr HWND_TOP       = new IntPtr(0);
        public static readonly IntPtr HWND_BOTTOM    = new IntPtr(1);
        public static readonly IntPtr HWND_TOPMOST   = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        // ----- RedrawWindow 플래그 -----
        public const uint RDW_INVALIDATE  = 0x0001;
        public const uint RDW_ERASE       = 0x0004;
        public const uint RDW_FRAME       = 0x0400;
        public const uint RDW_ALLCHILDREN = 0x0080;
        public const uint RDW_UPDATENOW   = 0x0100;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
    }
}
