using Mesh.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Mesh.App;

/// <summary>
/// The monitors this PC has, as the phone sees them in its cast controls, and the rectangles
/// the cast window is put into. WPF has no monitor list of its own, so this is the Win32 one.
/// All coordinates are physical pixels; <see cref="CastWindow"/> converts them to the
/// device-independent units WPF positions windows in.
/// </summary>
public static class CastMonitors
{
    /// <summary>Every monitor, primary first. The id is the device name Windows gives the
    /// monitor (<c>\\.\DISPLAY1</c>), which survives the window being moved around.</summary>
    public static IReadOnlyList<CastMonitor> Current()
    {
        var monitors = new List<CastMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr _, ref Rect _, IntPtr _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(handle, ref info))
            {
                var isPrimary = (info.Flags & MonitorinfoPrimary) != 0;
                monitors.Add(new CastMonitor(
                    info.DeviceName,
                    FriendlyName(info.DeviceName, isPrimary),
                    info.Monitor.Right - info.Monitor.Left,
                    info.Monitor.Bottom - info.Monitor.Top,
                    isPrimary));
            }
            return true;
        }, IntPtr.Zero);

        return monitors.OrderByDescending(m => m.IsPrimary).ToList();
    }

    /// <summary>The working rectangle of one monitor in physical pixels, or the primary one's
    /// when that monitor is gone.</summary>
    public static (int X, int Y, int Width, int Height) BoundsOf(string monitorId)
    {
        var bounds = default((int X, int Y, int Width, int Height));
        var primary = bounds;
        var found = false;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr _, ref Rect _, IntPtr _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(handle, ref info)) return true;
            var rect = (info.Monitor.Left, info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
            if ((info.Flags & MonitorinfoPrimary) != 0) primary = rect;
            if (info.DeviceName == monitorId)
            {
                bounds = rect;
                found = true;
            }
            return true;
        }, IntPtr.Zero);

        return found ? bounds : primary;
    }

    /// <summary>The rectangle covering every monitor — the "all monitors" placement.</summary>
    public static (int X, int Y, int Width, int Height) VirtualDesktop()
    {
        var left = GetSystemMetrics(SmXvirtualscreen);
        var top = GetSystemMetrics(SmYvirtualscreen);
        return (left, top, GetSystemMetrics(SmCxvirtualscreen), GetSystemMetrics(SmCyvirtualscreen));
    }

    /// <summary>A name a person can pick out of a list on the phone. Windows only gives the
    /// device name here, so it is dressed up with the monitor's number and its size.</summary>
    private static string FriendlyName(string deviceName, bool isPrimary)
    {
        var number = deviceName.Replace(@"\\.\DISPLAY", string.Empty);
        return isPrimary ? $"Monitor {number} (primary)" : $"Monitor {number}";
    }

    private const int MonitorinfoPrimary = 0x00000001;
    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
