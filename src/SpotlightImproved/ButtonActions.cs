using System.Runtime.InteropServices;
using Spotlight.Core;

namespace SpotlightImproved;

/// <summary>
/// What the diverted buttons do. The device has already suppressed their default action; this decides what to
/// do instead. Called on HID threads - SendInput is thread-agnostic.
/// </summary>
sealed class ButtonActions(Func<AppSettings> settings)
{
    const ushort VK_LEFT = 0x25;
    const ushort VK_RIGHT = 0x27;

    public void OnPressed(ushort cid)
    {
        var s = settings();
        switch (cid)
        {
            // The device reports a long press once, when it recognizes it - one slide per press, like a short press.
            case SpotlightIds.NextLongPress when s.LongPressNextBack == LongPressAction.ChangeSlide:
                Input.TapKey(VK_RIGHT);
                break;
            case SpotlightIds.BackLongPress when s.LongPressNextBack == LongPressAction.ChangeSlide:
                Input.TapKey(VK_LEFT);
                break;
            case SpotlightIds.LeftButton when s.PointerTap == TapAction.LeftClick:
                Input.LeftClick();
                break;
        }
    }
}

static class Input
{
    const uint INPUT_MOUSE = 0;
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;

    /// <summary>Presses and releases a key. Arrow keys are extended keys, like on a real keyboard.</summary>
    public static void TapKey(ushort virtualKey)
    {
        var scan = (ushort)MapVirtualKey(virtualKey, 0);
        Send(
            Key(virtualKey, scan, KEYEVENTF_EXTENDEDKEY),
            Key(virtualKey, scan, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP));
    }

    public static void LeftClick() => Send(Mouse(MOUSEEVENTF_LEFTDOWN), Mouse(MOUSEEVENTF_LEFTUP));

    static INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    static INPUT Mouse(uint flags) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } },
    };

    static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint code, uint mapType);
}
