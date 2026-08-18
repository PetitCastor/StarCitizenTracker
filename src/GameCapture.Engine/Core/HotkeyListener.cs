using System.Runtime.InteropServices;

namespace GameCapture.Engine;

/// <summary>
/// Global hotkey via a low-level keyboard hook (WH_KEYBOARD_LL) on a dedicated
/// message-pump thread. RegisterHotKey is not used because games that read the keyboard
/// through raw input never let WM_HOTKEY fire while focused; a
/// low-level hook sees keys at the system input chain before the game does.
/// The callback runs on the listener thread and must return fast (hand work to a channel).
/// </summary>
public sealed class HotkeyListener : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private readonly Thread _thread;
    private readonly uint _modifiers;
    private readonly uint _virtualKey;
    private readonly Action _onPressed;

    // Kept as a field so the GC never collects the delegate the hook is calling into.
    private readonly LowLevelKeyboardProc _hookProc;

    private uint _threadId;
    private bool _comboDown; // suppress key auto-repeat while held

    public HotkeyListener(uint modifiers, uint virtualKey, Action onPressed)
    {
        _modifiers = modifiers;
        _virtualKey = virtualKey;
        _onPressed = onPressed;
        _hookProc = HookCallback;

        using var ready = new ManualResetEventSlim(false);
        Exception? startupError = null;

        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();

            var hook = SetWindowsHookExW(WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);
            if (hook == IntPtr.Zero)
            {
                startupError = new InvalidOperationException(
                    $"SetWindowsHookEx(WH_KEYBOARD_LL) failed (Win32 error {Marshal.GetLastWin32Error()}).");
                ready.Set();
                return;
            }

            ready.Set();

            try
            {
                // Low-level hooks require a message pump on the installing thread.
                while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0)
                {
                }
            }
            finally
            {
                UnhookWindowsHookEx(hook);
            }
        })
        {
            IsBackground = true,
            Name = "HotkeyListener",
        };

        _thread.Start();
        ready.Wait();

        if (startupError is not null)
            throw startupError;
    }

    private IntPtr HookCallback(int nCode, UIntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (uint)wParam;
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            if (info.VkCode == _virtualKey)
            {
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    if (!_comboDown && ModifiersMatch())
                    {
                        _comboDown = true;
                        _onPressed();
                    }
                }
                else if (msg is WM_KEYUP or WM_SYSKEYUP)
                {
                    _comboDown = false;
                }
            }
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private bool ModifiersMatch()
    {
        static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        var ctrl = IsDown(VK_CONTROL);
        var shift = IsDown(VK_SHIFT);
        var alt = IsDown(VK_MENU);
        var win = IsDown(VK_LWIN) || IsDown(VK_RWIN);

        // Exact match: required modifiers down, others up — so Ctrl+Shift+F12 does not
        // also fire on Ctrl+Alt+Shift+F12.
        return ctrl == ((_modifiers & MOD_CONTROL) != 0)
            && shift == ((_modifiers & MOD_SHIFT) != 0)
            && alt == ((_modifiers & MOD_ALT) != 0)
            && win == ((_modifiers & MOD_WIN) != 0);
    }

    public void Dispose()
    {
        PostThreadMessageW(_threadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    // ---- Hotkey string parsing ----------------------------------------------

    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;

    /// <summary>Parses combos like "Ctrl+Shift+F12" or "Alt+M" into modifier flags + virtual key.</summary>
    public static (uint Modifiers, uint VirtualKey) ParseHotkey(string hotkey)
    {
        uint modifiers = 0;
        uint vk = 0;

        foreach (var rawToken in hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = rawToken.ToUpperInvariant();
            switch (token)
            {
                case "CTRL" or "CONTROL": modifiers |= MOD_CONTROL; continue;
                case "SHIFT": modifiers |= MOD_SHIFT; continue;
                case "ALT": modifiers |= MOD_ALT; continue;
                case "WIN" or "WINDOWS": modifiers |= MOD_WIN; continue;
            }

            if (vk != 0)
                throw new FormatException($"Hotkey '{hotkey}' has more than one non-modifier key.");

            if (token.Length >= 2 && token[0] == 'F' && int.TryParse(token[1..], out var fn) && fn is >= 1 and <= 24)
                vk = 0x70u + (uint)fn - 1;                       // VK_F1..VK_F24
            else if (token.Length == 1 && token[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
                vk = token[0];                                    // VK codes match ASCII for A-Z, 0-9
            else
                throw new FormatException($"Unsupported key '{rawToken}' in hotkey '{hotkey}'. Use modifiers + A-Z, 0-9, or F1-F24.");
        }

        if (vk == 0)
            throw new FormatException($"Hotkey '{hotkey}' has no non-modifier key.");

        return (modifiers, vk);
    }
}
