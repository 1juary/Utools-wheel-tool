using System;
using System.Runtime.InteropServices;

sealed class WheelScrollHook : IDisposable
{
    const int LowLevelMouse = 14;
    const int MouseWheel = 0x020A;
    const int LeftButtonUp = 0x0202;
    readonly Func<int, bool> scroll;
    readonly Action leftReleased;
    readonly HookCallback callback;
    readonly IntPtr hook;

    delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int id, HookCallback callback, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string module);

    public WheelScrollHook(Func<int, bool> scroll, Action leftReleased = null)
    {
        this.scroll = scroll;
        this.leftReleased = leftReleased;
        callback = Handle;
        hook = SetWindowsHookEx(LowLevelMouse, callback, GetModuleHandle(null), 0);
    }

    IntPtr Handle(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt32() == MouseWheel)
        {
            int delta = unchecked((short)(Marshal.ReadInt32(data, 8) >> 16));
            if (scroll(delta)) return new IntPtr(1);
        }
        else if (code >= 0 && message.ToInt32() == LeftButtonUp && leftReleased != null)
            leftReleased();
        return CallNextHookEx(hook, code, message, data);
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        GC.KeepAlive(callback);
    }
}
