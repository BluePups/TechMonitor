using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace TechMonitor.Services
{
    public class DriveSpecItem
    {
        public string Name { get; set; } = "";          // VD: C:\
        public string VolumeLabel { get; set; } = "";   // VD: Windows
        public double TotalGb { get; set; }
        public double FreeGb { get; set; }
        public double UsedGb => Math.Max(0, TotalGb - FreeGb);
        public double UsedPercent => TotalGb > 0 ? (UsedGb / TotalGb) * 100 : 0;
        public string Summary => $"{Name} ({VolumeLabel}) - Trống {FreeGb:F1} / {TotalGb:F0} GB ({UsedPercent:F0}%)";
    }

    public class SystemSpecs
    {
        public string CpuName { get; set; } = "Đang đọc...";
        public string CpuCoresInfo { get; set; } = "";
        public string DiscreteGpuName { get; set; } = "Không có card rời";
        public string RamSummary { get; set; } = "";
        public List<string> RamSticks { get; set; } = new();
        public List<DriveSpecItem> Drives { get; set; } = new();
    }

    public class SystemSpecService
    {
        // Đọc thông số tĩnh 1 lần lúc mở app hoặc khi bấm nút "Làm mới"
        public SystemSpecs GetSystemSpecs()
        {
            var specs = new SystemSpecs();

            // 1. Đọc thông tin CPU qua WMI
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
                foreach (ManagementObject obj in searcher.Get())
                {
                    specs.CpuName = obj["Name"]?.ToString()?.Trim() ?? "CPU";
                    var cores = obj["NumberOfCores"]?.ToString() ?? "?";
                    var threads = obj["NumberOfLogicalProcessors"]?.ToString() ?? "?";
                    specs.CpuCoresInfo = $"{cores} Nhân / {threads} Luồng";
                    break;
                }
            }
            catch { }

            // 2. Đọc tên GPU rời (bỏ qua card onboard Intel/AMD)
            try
            {
                string[] igpuKeywords = { "Radeon(TM) Graphics", "Radeon Graphics", "UHD Graphics", "Iris", "Microsoft Basic" };
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                var gpuNames = new List<string>();
                foreach (ManagementObject obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(name)) gpuNames.Add(name);
                }

                var dGpu = gpuNames.FirstOrDefault(n => !igpuKeywords.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase)));
                specs.DiscreteGpuName = dGpu ?? gpuNames.FirstOrDefault() ?? "Không xác định";
            }
            catch { }

            // 3. Đọc từng thanh RAM, Bus và chế độ Single / Dual Channel
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Capacity, Speed, Manufacturer, DeviceLocator FROM Win32_PhysicalMemory");
                double totalRamGb = 0;
                int stickCount = 0;

                foreach (ManagementObject obj in searcher.Get())
                {
                    stickCount++;
                    double capGb = Convert.ToDouble(obj["Capacity"] ?? 0) / (1024 * 1024 * 1024);
                    totalRamGb += capGb;
                    string speed = obj["Speed"]?.ToString() ?? "?";
                    string maker = obj["Manufacturer"]?.ToString()?.Trim() ?? "RAM";
                    string slot = obj["DeviceLocator"]?.ToString()?.Trim() ?? $"Khe {stickCount}";

                    specs.RamSticks.Add($"{slot}: {capGb:F0} GB ({speed} MHz - {maker})");
                }

                string channelMode = stickCount >= 2 ? "Dual Channel" : "Single Channel";
                specs.RamSummary = $"{totalRamGb:F0} GB ({stickCount} thanh - {channelMode})";
            }
            catch { }

            // 4. Đọc dung lượng các ổ đĩa cố định
            try
            {
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                {
                    specs.Drives.Add(new DriveSpecItem
                    {
                        Name = drive.Name,
                        VolumeLabel = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel,
                        TotalGb = drive.TotalSize / (1024.0 * 1024 * 1024),
                        FreeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024)
                    });
                }
            }
            catch { }

            return specs;
        }

        // Dọn rác an toàn: %TEMP%, Windows\Temp, Windows Update Cache và Thùng rác
        public double CleanTempFiles()
        {
            long freedBytes = 0;
            string[] tempFolders =
            {
                Path.GetTempPath(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download")
            };

            foreach (var folder in tempFolders)
            {
                if (!Directory.Exists(folder)) continue;
                freedBytes += DeleteDirectoryContents(folder);
            }

            // Làm sạch Thùng rác (không hiện hộp thoại xác nhận, không phát âm thanh)
            try
            {
                SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            }
            catch { }

            return freedBytes / (1024.0 * 1024.0); // Trả về dung lượng giải phóng tính theo MB
        }

        private static long DeleteDirectoryContents(string folderPath)
        {
            long bytesDeleted = 0;
            var dirInfo = new DirectoryInfo(folderPath);

            foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    long size = file.Length;
                    file.Delete();
                    bytesDeleted += size;
                }
                catch { } // Bỏ qua các file đang bị Windows/app khác khóa
            }

            foreach (var subDir in dirInfo.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    bytesDeleted += DeleteDirectoryContents(subDir.FullName);
                    subDir.Delete(false);
                }
                catch { }
            }

            return bytesDeleted;
        }

        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI = 0x00000002;
        private const uint SHERB_NOSOUND = 0x00000004;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
    }
}