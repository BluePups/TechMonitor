using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32.TaskScheduler;

namespace TechMonitor.Services
{
    public class AppConfig
    {
        public bool ShowOverlay { get; set; } = true;
        public bool ShowCpu { get; set; } = true;
        public bool ShowGpu { get; set; } = true;
        public bool ShowRam { get; set; } = true;
        public bool ShowStatus { get; set; } = true;

        public bool LockOverlay { get; set; } = true;
        public double OverlayLeft { get; set; } = 20;
        public double OverlayTop { get; set; } = 20;

        public bool MinimizeToTrayOnClose { get; set; } = true;
        public bool AutoStartWithWindows { get; set; } = false;

        public string CalibratedCpuName { get; set; } = "";
        public float CalibratedCpuMaxPowerW { get; set; } = 0f;
        public float CalibratedCpuMaxClockMhz { get; set; } = 0f;
        public float CalibratedCpuMaxVolt { get; set; } = 0f;

        public string CalibratedGpuName { get; set; } = "";
        public float CalibratedGpuMaxPowerW { get; set; } = 0f;
    }

    public static class ConfigService
    {
        private static readonly string ConfigFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TechMonitor");
        private static readonly string ConfigFile = Path.Combine(ConfigFolder, "settings.json");
        private const string TaskName = "TechMonitorOverlayStartup";

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    string json = File.ReadAllText(ConfigFile);
                    return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
            }
            catch { }
            return new AppConfig();
        }

        public static void Save(AppConfig config)
        {
            try
            {
                Directory.CreateDirectory(ConfigFolder);
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigFile, json);
            }
            catch { }
        }

        public static void ApplyAutoStart(bool enable)
        {
            try
            {
                using var ts = new TaskService();
                if (!enable)
                {
                    ts.RootFolder.DeleteTask(TaskName, false);
                    return;
                }

                string currentExe = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(currentExe) || !currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return;

                // Nếu file đang nằm ở Downloads hoặc Desktop, tự chép vào C:\ProgramData\TechMonitor để tránh khách xóa nhầm
                string safeFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TechMonitor");
                string targetExe = Path.Combine(safeFolder, "TechMonitor.exe");

                if (!currentExe.Equals(targetExe, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(safeFolder);
                    File.Copy(currentExe, targetExe, true);
                }

                TaskDefinition td = ts.NewTask();
                td.RegistrationInfo.Description = "Theo doi nhiet do va hieu nang phan cung TechMonitor";
                td.Principal.RunLevel = TaskRunLevel.Highest; // Chạy quyền Admin ngầm không hiện UAC
                td.Triggers.Add(new LogonTrigger());
                td.Actions.Add(new ExecAction(targetExe, null, safeFolder));

                ts.RootFolder.RegisterTaskDefinition(TaskName, td);
            }
            catch { }
        }
    }
}