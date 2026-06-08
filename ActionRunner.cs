using System.Runtime.InteropServices;

namespace HotCorners;

internal static class ActionRunner
{
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_A = 0x41;
    private const ushort VK_D = 0x44;
    private const ushort VK_L = 0x4C;
    private const ushort VK_M = 0x4D;
    private const ushort VK_N = 0x4E;
    private const ushort VK_S = 0x53;
    private const ushort VK_W = 0x57;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MONITORPOWER = 0xF170;
    private const int HWND_BROADCAST = 0xFFFF;

    public static void Run(HotAction action)
    {
        switch (action)
        {
            case HotAction.None:
                break;
            case HotAction.TaskView:
                SendCombo(VK_LWIN, VK_TAB);
                break;
            case HotAction.ShowDesktop:
                SendCombo(VK_LWIN, VK_D);
                break;
            case HotAction.ActionCenter:
                SendCombo(VK_LWIN, VK_A);
                break;
            case HotAction.NotificationCenter:
                SendCombo(VK_LWIN, VK_N);
                break;
            case HotAction.StartMenu:
                SendSingle(VK_LWIN);
                break;
            case HotAction.Search:
                SendCombo(VK_LWIN, VK_S);
                break;
            case HotAction.Widgets:
                SendCombo(VK_LWIN, VK_W);
                break;
            case HotAction.LockScreen:
                SendCombo(VK_LWIN, VK_L);
                break;
            case HotAction.VirtualDesktopLeft:
                SendTriple(VK_LCONTROL, VK_LWIN, VK_LEFT, extendedThird: true);
                break;
            case HotAction.VirtualDesktopRight:
                SendTriple(VK_LCONTROL, VK_LWIN, VK_RIGHT, extendedThird: true);
                break;
            case HotAction.WindowSnapLeft:
                SendCombo(VK_LWIN, VK_LEFT, extendedSecond: true);
                break;
            case HotAction.WindowSnapRight:
                SendCombo(VK_LWIN, VK_RIGHT, extendedSecond: true);
                break;
            case HotAction.MinimizeAll:
                SendCombo(VK_LWIN, VK_M);
                break;
            case HotAction.SleepDisplays:
                SendMessage((IntPtr)HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2);
                break;
        }
    }

    private static void SendSingle(ushort vk)
    {
        var inputs = new INPUT[2];
        inputs[0] = KeyDown(vk);
        inputs[1] = KeyUp(vk);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendCombo(ushort mod, ushort key, bool extendedSecond = false)
    {
        var inputs = new INPUT[4];
        inputs[0] = KeyDown(mod);
        inputs[1] = KeyDown(key, extendedSecond);
        inputs[2] = KeyUp(key, extendedSecond);
        inputs[3] = KeyUp(mod);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendTriple(ushort mod1, ushort mod2, ushort key, bool extendedThird = false)
    {
        var inputs = new INPUT[6];
        inputs[0] = KeyDown(mod1);
        inputs[1] = KeyDown(mod2);
        inputs[2] = KeyDown(key, extendedThird);
        inputs[3] = KeyUp(key, extendedThird);
        inputs[4] = KeyUp(mod2);
        inputs[5] = KeyUp(mod1);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyDown(ushort vk, bool extended = false) => MakeKey(vk, false, extended);
    private static INPUT KeyUp(ushort vk, bool extended = false) => MakeKey(vk, true, extended);

    private static INPUT MakeKey(ushort vk, bool up, bool extended)
    {
        uint flags = 0;
        if (up) flags |= KEYEVENTF_KEYUP;
        if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
