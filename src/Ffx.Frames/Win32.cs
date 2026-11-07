// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;

namespace Ffx.Frames;

/// <summary>The few Win32 calls the mod needs: the game's base, a code patch, the monitor's refresh rate.</summary>
internal static unsafe class Win32 {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string name);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int VirtualProtect(void* address, nuint size, uint protect, uint* old);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int FlushInstructionCache(nint process, void* address, nuint size);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetMonitorInfoW(nint monitor, void* info);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int EnumDisplaySettingsW(char* device, int mode, void* devmode);

    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int ENUM_CURRENT_SETTINGS = -1;
    /// <summary><c>MONITORINFOEXW</c>: cbSize, rcMonitor, rcWork, dwFlags, szDevice[32].</summary>
    private const int MonitorInfoSize = 4 + 16 + 16 + 4 + 64;
    /// <summary><c>DEVMODEW</c>'s size, and where <c>dmSize</c> and <c>dmDisplayFrequency</c> are in it.</summary>
    private const int DevModeSize = 220, DevModeSizeAt = 68, DevModeFrequencyAt = 184;

    /// <summary>The address FFX.exe is loaded at; 0 outside the game.</summary>
    public static nint ModuleBase(string name) => GetModuleHandleW(name);

    /// <summary>Writes <paramref name="value"/> at <paramref name="at"/> in the game's code.</summary>
    public static bool PatchU32(nint at, uint value) {
        uint old;
        if (VirtualProtect((void*)at, 4, PAGE_EXECUTE_READWRITE, &old) == 0) return false;
        *(uint*)at = value;
        uint ignored;
        VirtualProtect((void*)at, 4, old, &ignored);
        FlushInstructionCache(GetCurrentProcess(), (void*)at, 4);
        return true;
    }

    /// <summary>The refresh rate of the monitor <paramref name="hwnd"/> is on, in Hz; 0 when it cannot be read.</summary>
    public static int RefreshRate(nint hwnd) {
        if (hwnd == 0) return 0;
        nint monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == 0) return 0;
        byte* info = stackalloc byte[MonitorInfoSize];
        new Span<byte>(info, MonitorInfoSize).Clear();
        *(int*)info = MonitorInfoSize;
        if (GetMonitorInfoW(monitor, info) == 0) return 0;
        byte* dev = stackalloc byte[DevModeSize];
        new Span<byte>(dev, DevModeSize).Clear();
        *(ushort*)(dev + DevModeSizeAt) = DevModeSize;
        if (EnumDisplaySettingsW((char*)(info + 40), ENUM_CURRENT_SETTINGS, dev) == 0) return 0;
        int hz = *(int*)(dev + DevModeFrequencyAt);
        // 0 and 1 mean "the hardware's default".
        return hz > 1 ? hz : 0;
    }
}
