// SPDX-License-Identifier: MIT
// Win32 functions used by ChainShot.exe (window bounds, hotkeys, console attach).

using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ChainShot
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT rect, int size);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int which);
        [DllImport("kernel32.dll")] static extern int GetFileType(IntPtr handle);

        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        /// <summary>Per-monitor DPI awareness, so that captures use physical pixels (the manifest
        /// already asks for it; this covers hosts that ignore it).</summary>
        public static void SetDpiAwareness()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch (EntryPointNotFoundException) { }
            try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, string l);

        /// <summary>Grey hint text in an empty text box.</summary>
        public static void SetCueBanner(Control box, string text)
        {
            SendMessage(box.Handle, 0x1501 /* EM_SETCUEBANNER */, new IntPtr(1), text);
        }

        public const int WM_HOTKEY = 0x0312;
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;

        /// <summary>Visible bounds of a window (without the invisible resize border of Windows 10/11).</summary>
        public static System.Drawing.Rectangle GetWindowBounds(IntPtr hWnd)
        {
            RECT r;
            if (DwmGetWindowAttribute(hWnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(hWnd, out r);
            return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

        public static string GetClassName(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string GetTitle(IntPtr hWnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static int GetProcessId(IntPtr hWnd)
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            return (int)pid;
        }

        /// <summary>True when stdout is a real file, pipe or console (e.g. redirected by a script).</summary>
        public static bool HasStdOut()
        {
            IntPtr h = GetStdHandle(-11);
            return h != IntPtr.Zero && h != new IntPtr(-1) && GetFileType(h) != 0;
        }
    }

    /// <summary>Receives WM_HOTKEY for global hotkeys on the UI thread.</summary>
    sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        public event Action<int> Pressed;
        readonly System.Collections.Generic.List<int> ids = new System.Collections.Generic.List<int>();

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        /// <summary>Registers Ctrl+Alt+key; returns false when another program already owns it.</summary>
        public bool Register(int id, Keys key)
        {
            bool ok = Native.RegisterHotKey(Handle, id, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)key);
            if (ok) ids.Add(id);
            return ok;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && Pressed != null) Pressed(m.WParam.ToInt32());
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            foreach (int id in ids) Native.UnregisterHotKey(Handle, id);
            ids.Clear();
            DestroyHandle();
        }
    }
}
