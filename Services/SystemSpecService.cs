using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace TechMonitor.Services
{
    public class DriveSpec
    {
        public string Name { get; set; } = "";
        public double TotalGb { get; set; }
        public double FreeGb { get; set; }
        public double UsedPercent => TotalGb > 0 ? ((TotalGb - FreeGb) / TotalGb) * 100.0 : 0;
        public string Summary => $"{Name} — Trống {FreeGb:F1} GB / {TotalGb:F0} GB ({UsedPercent:F0}% đã dùng)";
    }

    public class SystemSpecReport
    {
        public string CpuName { get; set; } = "Đang nhận diện...";
        public string CpuCoresInfo { get; set; } = "";
        public string DiscreteGpuName { get; set; } = "Không có card rời (Dùng đồ họa tích hợp)";
        public string DiscreteGpuDetail { get; set; } = "";
        public string RamSummary { get; set; } = "";
        public List<string> RamSticks { get; set; } = new();
        public string BatteryHealthSummary { get; set; } = "Máy bàn (PC) hoặc không gắn pin";
        public string UptimeSummary { get; set; } = "";
        public List<DriveSpec> Drives { get; set; } = new();
    }

    public class SystemSpecService
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI = 0x00000002;
        private const uint SHERB_NOSOUND = 0x00000004;

        public SystemSpecReport GetSystemSpecs()
        {
            var report = new SystemSpecReport();
            ReadCpuInfo(report);
            ReadGpuInfo(report);
            ReadRamInfo(report);
            ReadBatteryHealth(report);
            ReadUptime(report);
            ReadDrivesInfo(report);
            return report;
        }

        private void ReadCpuInfo(SystemSpecReport report)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
                foreach (ManagementObject obj in searcher.Get())
                {
                    report.CpuName = obj["Name"]?.ToString()?.Trim() ?? "Unknown CPU";
                    var cores = obj["NumberOfCores"]?.ToString() ?? "?";
                    var threads = obj["NumberOfLogicalProcessors"]?.ToString() ?? "?";
                    report.CpuCoresInfo = $"{cores} Nhân / {threads} Luồng";
                    break;
                }
            }
            catch { }
        }

        private void ReadGpuInfo(SystemSpecReport report)
        {
            try
            {
                string[] iGpuKeywords = { "Intel(R) UHD", "Intel(R) Iris", "Intel(R) HD", "Radeon(TM) Graphics", "Radeon Vega", "Microsoft Basic" };
                using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion, DriverDate FROM Win32_VideoController");

                foreach (ManagementObject obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString()?.Trim() ?? "";
                    if (string.IsNullOrEmpty(name)) continue;

                    bool isIntegrated = iGpuKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
                    if (!isIntegrated)
                    {
                        report.DiscreteGpuName = name;

                        // Tính dung lượng VRAM (AdapterRAM lưu dạng uint32 byte)
                        double vramGb = 0;
                        if (obj["AdapterRAM"] != null && ulong.TryParse(obj["AdapterRAM"].ToString(), out ulong ramBytes))
                        {
                            vramGb = Math.Round(ramBytes / (1024.0 * 1024.0 * 1024.0), 1);
                        }

                        string driverVer = obj["DriverVersion"]?.ToString() ?? "";
                        string driverDateRaw = obj["DriverDate"]?.ToString() ?? "";
                        string formattedDate = "";
                        if (driverDateRaw.Length >= 8)
                        {
                            formattedDate = $"{driverDateRaw.Substring(6, 2)}/{driverDateRaw.Substring(4, 2)}/{driverDateRaw.Substring(0, 4)}";
                        }

                        var details = new List<string>();
                        if (vramGb > 0) details.Add($"VRAM: {vramGb:F0} GB");
                        if (!string.IsNullOrEmpty(driverVer)) details.Add($"Driver: {driverVer}");
                        if (!string.IsNullOrEmpty(formattedDate)) details.Add($"Ngày: {formattedDate}");

                        report.DiscreteGpuDetail = string.Join("  |  ", details);
                        break;
                    }
                }
            }
            catch { }
        }

        private void ReadRamInfo(SystemSpecReport report)
        {
            try
            {
                double totalGb = 0;
                int stickCount = 0;
                using var searcher = new ManagementObjectSearcher("SELECT Capacity, Speed, Manufacturer, PartNumber FROM Win32_PhysicalMemory");

                foreach (ManagementObject obj in searcher.Get())
                {
                    if (ulong.TryParse(obj["Capacity"]?.ToString(), out ulong capBytes))
                    {
                        double gb = capBytes / (1024.0 * 1024.0 * 1024.0);
                        totalGb += gb;
                        stickCount++;
                        string speed = obj["Speed"]?.ToString() ?? "?";
                        string maker = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                        report.RamSticks.Add($"Khe {stickCount}: {gb:F0} GB ({speed} MHz) {maker}".Trim());
                    }
                }

                string channelMode = stickCount >= 2 ? "Dual-Channel (Tối ưu)" : "Single-Channel (1 thanh)";
                report.RamSummary = $"{totalGb:F0} GB ({stickCount} thanh — {channelMode})";
            }
            catch { }
        }

        private void ReadBatteryHealth(SystemSpecReport report)
        {
            try
            {
                uint designCap = 0;
                uint fullChargedCap = 0;

                using (var s1 = new ManagementObjectSearcher(@"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData"))
                {
                    foreach (ManagementObject obj in s1.Get())
                    {
                        designCap = Convert.ToUInt32(obj["DesignedCapacity"]);
                        break;
                    }
                }

                using (var s2 = new ManagementObjectSearcher(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
                {
                    foreach (ManagementObject obj in s2.Get())
                    {
                        fullChargedCap = Convert.ToUInt32(obj["FullChargedCapacity"]);
                        break;
                    }
                }

                if (designCap > 0 && fullChargedCap > 0)
                {
                    double healthPct = Math.Min(100.0, (fullChargedCap * 100.0) / designCap);
                    double wearPct = Math.Max(0.0, 100.0 - healthPct);
                    string eval = wearPct <= 15 ? "Rất tốt" : (wearPct <= 35 ? "Bình thường" : "Nên kiểm  tra");

                    report.BatteryHealthSummary = $"Còn {healthPct:F0}% (Chai {wearPct:F0}% — {eval})  |  {fullChargedCap:N0} / {designCap:N0} mWh";
                }
            }
            catch
            {
                // Giữ mặc định nếu là máy bàn PC không có cảm biến pin WMI
            }
        }

        private void ReadUptime(SystemSpecReport report)
        {
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            string note = up.TotalDays >= 3 ? " (Nên Restart để làm mới RAM)" : "";
            report.UptimeSummary = $"{up.Days} ngày {up.Hours} giờ {up.Minutes} phút{note}";
        }

        private void ReadDrivesInfo(SystemSpecReport report)
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                report.Drives.Add(new DriveSpec
                {
                    Name = $"Ổ đĩa {d.Name.TrimEnd('\\')} ({(string.IsNullOrEmpty(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel)})",
                    TotalGb = d.TotalSize / (1024.0 * 1024.0 * 1024.0),
                    FreeGb = d.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0)
                });
            }
        }

        public double CleanTempFiles()
        {
            long freedBytes = 0;
            string[] targetFolders =
            {
                Path.GetTempPath(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download")
            };

            foreach (string folder in targetFolders)
            {
                if (!Directory.Exists(folder)) continue;
                freedBytes += DeleteDirectoryContents(new DirectoryInfo(folder));
            }

            try
            {
                SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            }
            catch { }

            return freedBytes / (1024.0 * 1024.0);
        }

        private long DeleteDirectoryContents(DirectoryInfo dir)
        {
            long freed = 0;
            try
            {
                foreach (var file in dir.GetFiles())
                {
                    try
                    {
                        long size = file.Length;
                        file.Delete();
                        freed += size;
                    }
                    catch { }
                }
                foreach (var subDir in dir.GetDirectories())
                {
                    try
                    {
                        freed += DeleteDirectoryContents(subDir);
                        subDir.Delete(false);
                    }
                    catch { }
                }
            }
            catch { }
            return freed;
        }
    }
}