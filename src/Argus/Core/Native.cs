using System;
using System.Runtime.InteropServices;

namespace PresenceGuard
{
    internal static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool LockWorkStation();

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        /// <summary>Locks the workstation. Returns false if the call failed.</summary>
        public static bool Lock()
        {
            try { return LockWorkStation(); } catch { return false; }
        }

        /// <summary>Seconds since the last keyboard/mouse input. Returns a large value on failure.</summary>
        public static double IdleSeconds()
        {
            try
            {
                var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                if (!GetLastInputInfo(ref info)) return 9999;
                uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
                return idleMs / 1000.0;
            }
            catch { return 9999; }
        }

        /// <summary>True when running on battery (AC line explicitly offline).</summary>
        public static bool OnBattery()
        {
            try { return GetSystemPowerStatus(out var s) && s.ACLineStatus == 0; }
            catch { return false; }
        }
    }
}
