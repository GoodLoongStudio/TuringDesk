using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace TuringDesk.Desktop.Services;

public sealed record ShellSystemStatus(
    bool NetworkAvailable,
    bool HasBattery,
    int? BatteryPercent,
    bool Charging,
    string NetworkLabel,
    string BatteryLabel);

public static class SystemStatusService
{
    public static ShellSystemStatus Read()
    {
        var network = NetworkInterface.GetIsNetworkAvailable();
        var hasPower = GetSystemPowerStatus(out var power);
        var hasBattery = hasPower && power.BatteryFlag != 128 && power.BatteryLifePercent != 255;
        var percent = hasBattery ? Math.Clamp((int)power.BatteryLifePercent, 0, 100) : null;
        var charging = hasBattery && (power.BatteryFlag & 8) != 0;

        return new ShellSystemStatus(
            network,
            hasBattery,
            percent,
            charging,
            network ? "网络已连接" : "网络不可用",
            hasBattery
                ? $"电池 {percent}%{(charging ? " · 充电中" : string.Empty)}"
                : "桌面电源");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);
}
