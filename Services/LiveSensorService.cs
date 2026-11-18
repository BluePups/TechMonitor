using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace TechMonitor.Services
{
    // Đối tượng chứa toàn bộ thông số mỗi lần quét để đẩy lên UI
    public class SensorSnapshot
    {
        public string CpuName { get; set; } = "CPU";
        public float CpuTemp { get; set; }
        public float CpuLoad { get; set; }
        public float CpuPower { get; set; }
        public float CpuClock { get; set; }

        public string GpuName { get; set; } = "GPU Rời";
        public bool HasDiscreteGpu { get; set; }
        public bool IsGpuSleeping { get; set; } // True khi rút sạc và card rời đang ngủ
        public bool IsSensorBlocked { get; set; }
        public float GpuTemp { get; set; }
        public float GpuLoad { get; set; }
        public float GpuPower { get; set; }
        public float GpuClock { get; set; }

        public float RamUsedGb { get; set; }
        public float RamTotalGb { get; set; }
        public float RamLoadPercent { get; set; }

        public bool IsCpuThrottling { get; set; }
        public bool IsGpuThrottling { get; set; }
        public string HealthStatus { get; set; } = "Ổn định";
        public string StatusLevel { get; set; } = "Normal"; // Normal | Warning | Critical

        public float MaxCpuTemp { get; set; }
        public float MaxCpuPower { get; set; }
        public float MaxGpuTemp { get; set; }
        public float MaxGpuPower { get; set; }

        public List<HardwareAlertEvent> NewAlerts { get; set; } = new();
    }

    public class HardwareAlertEvent
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Category { get; set; } = "CPU"; // "CPU" | "GPU" | "RAM"
        public string Message { get; set; } = "";
        public string TimeText => Timestamp.ToString("HH:mm:ss");
        public string BadgeColor => Category switch
        {
            "CPU" => "#DC2626", // Đỏ
            "GPU" => "#D97706", // Cam đậm
            "RAM" => "#7C3AED", // Tím
            _ => "#334155"
        };
    }

    public class LiveSensorService : IDisposable
    {
        private readonly Computer _computer;
        private IHardware? _cpuHardware;
        private IHardware? _discreteGpuHardware;
        private IHardware? _ramHardware;
        private DateTime _lastBatteryGpuPoll = DateTime.MinValue;
        private SensorSnapshot _lastSnapshot = new();

        private float _maxCpuTemp = 0f;
        private float _maxCpuPower = 0f;
        private float _maxGpuTemp = 0f;
        private float _maxGpuPower = 0f;

        private float _baseClockMhz = 2500f;
        private float _observedMaxClockMhz = 0f;
        private int _highTempStreakTicks = 0;
        private float _tjMaxTemp = 100f;

        private PerformanceCounter? _cpuLoadFallbackCounter;
        private PerformanceCounter? _cpuPerfFallbackCounter;

        private int _gpuBottleneckTicks = 0;
        private int _ramBottleneckTicks = 0;
        private DateTime _lastCpuAlertTime = DateTime.MinValue;
        private DateTime _lastGpuAlertTime = DateTime.MinValue;
        private DateTime _lastRamAlertTime = DateTime.MinValue;

        public void ResetPeaks()
        {
            _maxCpuTemp = 0f;
            _maxCpuPower = 0f;
            _maxGpuTemp = 0f;
            _maxGpuPower = 0f;
        }

        public LiveSensorService()
        {
            EnsurePawnIoInstalled(); // Tự cài PawnIO nếu máy chưa có
            InitFallbackCounters();  // Chuẩn bị sẵn bộ đếm dự phòng của Windows
            
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true
            };

            _computer.Open();
            DetectHardware();
        }

        // Nhận diện phần cứng 1 lần lúc khởi động
        private void DetectHardware()
        {
            _cpuHardware = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            _ramHardware = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
            _discreteGpuHardware = FindDiscreteGpu();

            // Đọc xung gốc (Base Clock) và ước lượng TjMax theo dòng CPU
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, MaxClockSpeed FROM Win32_Processor");
                foreach (ManagementObject obj in searcher.Get())
                {
                    string cpuName = obj["Name"]?.ToString() ?? "";
                    if (obj["MaxClockSpeed"] != null && float.TryParse(obj["MaxClockSpeed"].ToString(), out float baseMhz) && baseMhz > 0)
                        _baseClockMhz = baseMhz;

                    // Ryzen 7000/8000 có trần TjMax thiết kế 95°C; các dòng khác thường 100°C - 105°C
                    if (cpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) &&
                    (cpuName.Contains("7") || cpuName.Contains("8")))
                        _tjMaxTemp = 95f;
                    break;
                }
            }
            catch { }
        }

        // Lọc riêng GPU rời (dGPU), loại bỏ card onboard của Intel và AMD
        private IHardware? FindDiscreteGpu()
        {
            var gpus = _computer.Hardware.Where(h =>
                h.HardwareType == HardwareType.GpuNvidia ||
                h.HardwareType == HardwareType.GpuAmd ||
                h.HardwareType == HardwareType.GpuIntel).ToList();

            // 1. Ưu tiên tuyệt đối card NVIDIA
            var nvidia = gpus.FirstOrDefault(g => g.HardwareType == HardwareType.GpuNvidia);
            if (nvidia != null) return nvidia;

            // 2. Lọc card rời AMD Radeon RX / Intel Arc dựa trên tên & VRAM (> 1024MB)
            string[] igpuKeywords = { "Radeon(TM) Graphics", "Radeon Graphics", "UHD Graphics", "Iris" };
            foreach (var gpu in gpus)
            {
                if (igpuKeywords.Any(k => gpu.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    continue;

                gpu.Update();
                var dedicatedVram = gpu.Sensors.FirstOrDefault(s =>
                    s.SensorType == SensorType.SmallData &&
                    s.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase) &&
                    s.Value.GetValueOrDefault() > 1024);

                if (dedicatedVram != null) return gpu;
            }

            return null; // Máy không có card rời
        }

        // Gọi hàm này mỗi 1.5 - 2 giây từ Timer
        public SensorSnapshot ReadSensors()
        {
            var snap = new SensorSnapshot();
            bool isOnAcPower = IsPluggedIn();

            // --- 1. ĐỌC THÔNG SỐ CPU ---
            if (_cpuHardware != null)
            {
                _cpuHardware.Update();
                snap.CpuName = _cpuHardware.Name;

                // Nhiệt độ: Intel dùng "CPU Package", AMD Ryzen dùng "Core (Tctl/Tdie)"
                snap.CpuTemp = GetSensorValue(_cpuHardware, SensorType.Temperature, true,
                    "CPU Package", "Core (Tctl/Tdie)", "Tctl/Tdie", "Package");

                // % Tải tổng
                snap.CpuLoad = GetSensorValue(_cpuHardware, SensorType.Load, true, "CPU Total");

                // Công suất điện (Watt)
                snap.CpuPower = GetSensorValue(_cpuHardware, SensorType.Power, true,
                    "CPU Package", "Package", "Cores");

                // Xung nhịp cao nhất trong các nhân (MHz)
                var coreClocks = _cpuHardware.Sensors
                    .Where(s => s.SensorType == SensorType.Clock && s.Name.Contains("Core #", StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Value.GetValueOrDefault());
                snap.CpuClock = coreClocks.Any() ? coreClocks.Max() : 0;

                // Kiểm tra cờ phần cứng báo Thermal Throttling
                snap.IsCpuThrottling = DetectRealCpuThrottling(_cpuHardware, snap);

                // Nếu cả nhiệt độ lẫn công suất CPU đều bằng 0 -> Driver bị chặn, kích hoạt Fallback
                if (snap.CpuTemp <= 0f && snap.CpuPower <= 0f)
                {
                    snap.IsSensorBlocked = true;

                    if (snap.CpuLoad <= 0f && _cpuLoadFallbackCounter != null)
                    {
                        try { snap.CpuLoad = _cpuLoadFallbackCounter.NextValue(); } catch { }
                    }

                    if (snap.CpuClock <= 0f && _cpuPerfFallbackCounter != null)
                    {
                        try
                        {
                            // % Processor Performance nhân với xung gốc sẽ ra xung nhịp thực tế (MHz)
                            float perfPercent = _cpuPerfFallbackCounter.NextValue();
                            if (perfPercent > 0) snap.CpuClock = _baseClockMhz * (perfPercent / 100f);
                        }
                        catch { }
                    }
                }
            }

            // --- 2. ĐỌC THÔNG SỐ GPU RỜI ---
            if (_discreteGpuHardware != null)
            {
                snap.HasDiscreteGpu = true;
                snap.GpuName = _discreteGpuHardware.Name;

                // Nếu rút sạc (dùng pin), chỉ quét card rời mỗi 8 giây để không đánh thức dGPU gây hao pin
                bool shouldPollGpu = isOnAcPower || (DateTime.Now - _lastBatteryGpuPoll).TotalSeconds >= 8;

                if (shouldPollGpu)
                {
                    _discreteGpuHardware.Update();
                    _lastBatteryGpuPoll = DateTime.Now;

                    float temp = GetSensorValue(_discreteGpuHardware, SensorType.Temperature, true, "GPU Core", "Core");
                    if (temp <= 0 && !isOnAcPower)
                    {
                        // Card rời đang ngắt điện hoàn toàn để tiết kiệm pin
                        snap.IsGpuSleeping = true;
                    }
                    else
                    {
                        snap.GpuTemp = temp;
                        snap.GpuLoad = GetSensorValue(_discreteGpuHardware, SensorType.Load, true, "GPU Core", "D3D 3D");
                        snap.GpuPower = GetSensorValue(_discreteGpuHardware, SensorType.Power, true, "GPU Package", "GPU Power", "Total");
                        snap.GpuClock = GetSensorValue(_discreteGpuHardware, SensorType.Clock, true, "GPU Core", "Core");

                        var gpuThermalLimit = GetSensorValue(_discreteGpuHardware, SensorType.Factor, false, "Thermal Limit");
                        snap.IsGpuThrottling = (gpuThermalLimit >= 1f && snap.GpuTemp >= 80f) || (snap.GpuLoad > 70f && snap.GpuTemp >= 85f);
                    }
                }
                else
                {
                    // Giữ lại trạng thái gần nhất khi đang giãn nhịp quét lúc chạy pin
                    snap.IsGpuSleeping = _lastSnapshot.IsGpuSleeping;
                    snap.GpuTemp = _lastSnapshot.GpuTemp;
                    snap.GpuLoad = _lastSnapshot.GpuLoad;
                    snap.GpuPower = _lastSnapshot.GpuPower;
                    snap.GpuClock = _lastSnapshot.GpuClock;
                }
            }

            // --- 3. ĐỌC THÔNG SỐ RAM ---
            if (_ramHardware != null)
            {
                _ramHardware.Update();
                float used = GetSensorValue(_ramHardware, SensorType.Data, true, "Memory Used");
                float available = GetSensorValue(_ramHardware, SensorType.Data, true, "Memory Available");
                snap.RamUsedGb = used;
                snap.RamTotalGb = used + available;
                snap.RamLoadPercent = GetSensorValue(_ramHardware, SensorType.Load, true, "Memory");
            }

            // --- 4. ĐÁNH GIÁ TRẠNG THÁI TỔNG THỂ CHO KHÁCH ---
            if (snap.CpuTemp > _maxCpuTemp) _maxCpuTemp = snap.CpuTemp;
            if (snap.CpuPower > _maxCpuPower) _maxCpuPower = snap.CpuPower;
            if (snap.HasDiscreteGpu && !snap.IsGpuSleeping)
            {
                if (snap.GpuTemp > _maxGpuTemp) _maxGpuTemp = snap.GpuTemp;
                if (snap.GpuPower > _maxGpuPower) _maxGpuPower = snap.GpuPower;
            }

            snap.MaxCpuTemp = _maxCpuTemp;
            snap.MaxCpuPower = _maxCpuPower;
            snap.MaxGpuTemp = _maxGpuTemp;
            snap.MaxGpuPower = _maxGpuPower;

            EvaluateOverallHealth(snap);

            CheckAndRecordAlerts(snap);

            _lastSnapshot = snap;
            return snap;
        }

        private void EvaluateOverallHealth(SensorSnapshot snap)
        {
            if (snap.IsCpuThrottling || snap.IsGpuThrottling)
            {
                snap.StatusLevel = "Critical";
                snap.HealthStatus = "Quá nhiệt - Đang giảm xung!";
            }
            else if ((!snap.IsSensorBlocked && snap.CpuTemp >= 90f) || snap.GpuTemp >= 84f)
            {
                snap.StatusLevel = "Critical";
                snap.HealthStatus = "Nhiệt rất cao - Cần kiểm tra tản nhiệt";
            }
            else if ((!snap.IsSensorBlocked && snap.CpuTemp >= 78f) || snap.GpuTemp >= 75f)
            {
                snap.StatusLevel = "Warning";
                snap.HealthStatus = "Tải cao";
            }
            else if (snap.IsSensorBlocked)
            {
                snap.StatusLevel = "Warning";
                snap.HealthStatus = "Chế độ cơ bản (Chưa đọc được nhiệt độ CPU)";
            }
            else
            {
                snap.StatusLevel = "Normal";
                snap.HealthStatus = "Mát mẻ / Ổn định";
            }
        }

        private bool DetectRealCpuThrottling(IHardware cpuHardware, SensorSnapshot snap)
        {
            // 1. Đọc cờ báo phần cứng trực tiếp (nếu thư viện đọc được từ thanh ghi MSR)
            var throttleFlag = GetSensorValue(cpuHardware, SensorType.Factor, false, "Thermal Throttling");
            if (throttleFlag >= 1f && snap.CpuTemp >= 85f)
                return true;

            // 2. Cập nhật TjMax chính xác trên chip Intel qua cảm biến "Distance to TjMax"
            float distToTjMax = GetSensorValue(cpuHardware, SensorType.Temperature, false, "Distance to TjMax");
            if (distToTjMax > 0 && snap.CpuTemp > 0)
                _tjMaxTemp = snap.CpuTemp + distToTjMax;

            // 3. Tự học xung nhịp Boost đỉnh thực tế của máy
            if (snap.CpuClock > _observedMaxClockMhz)
                _observedMaxClockMhz = snap.CpuClock;

            // 4. Đếm số nhịp liên tiếp nhiệt độ chạm vùng nguy hiểm (cách TjMax <= 6°C)
            float dangerThreshold = _tjMaxTemp - 6f;
            if (snap.CpuTemp >= dangerThreshold)
                _highTempStreakTicks++;
            else
                _highTempStreakTicks = 0; // Tụt nhiệt ngay -> Chỉ là bung xung tức thời, xóa bộ đếm

            // 5. Kết luận Throttling khi thỏa ĐỒNG THỜI 3 điều kiện:
            // - Nhiệt cao kéo dài >= 4 nhịp (~6-8 giây liên tiếp)
            // - CPU vẫn đang tải nặng >= 50%
            // - Xung nhịp bị ép tụt dưới 80% xung đỉnh đã học (hoặc rơi sát xung gốc Base Clock)
            bool isSustainedHot = _highTempStreakTicks >= 4;
            bool isStillUnderLoad = snap.CpuLoad >= 50f;
            bool isClockDropped = snap.CpuClock > 0 &&
                                ((_observedMaxClockMhz > 0 && snap.CpuClock < _observedMaxClockMhz * 0.80f) ||
                                (snap.CpuClock <= _baseClockMhz * 1.05f));

            return isSustainedHot && isStillUnderLoad && isClockDropped;
        }

        // Hàm hỗ trợ tìm cảm biến theo danh sách từ khóa ưu tiên
        private static float GetSensorValue(IHardware hardware, SensorType type, bool allowFallback, params string[] preferredNames)
        {
            foreach (var name in preferredNames)
            {
                var exact = hardware.Sensors.FirstOrDefault(s =>
                    s.SensorType == type && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (exact?.Value != null) return exact.Value.Value;
            }

            foreach (var name in preferredNames)
            {
                var partial = hardware.Sensors.FirstOrDefault(s =>
                    s.SensorType == type && s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (partial?.Value != null) return partial.Value.Value;
            }

            if (allowFallback)
                {
                    var fallback = hardware.Sensors.FirstOrDefault(s => s.SensorType == type && s.Value != null);
                    return fallback?.Value ?? 0f;
                }

            return 0f;
        }

        // Kiểm tra laptop đang cắm sạc hay dùng pin qua Win32 API (không cần WinForms)
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus; // 0 = Pin, 1 = Cắm sạc
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        private static bool IsPluggedIn()
        {
            if (GetSystemPowerStatus(out var status))
                return status.ACLineStatus == 1;
            return true;
        }

        private static void EnsurePawnIoInstalled()
        {
            try
            {
                // Kiểm tra dịch vụ PawnIO đã có trong Windows chưa
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
                if (key != null) return;

                string tempInstaller = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("TechMonitor.PawnIO_setup.exe");

                if (stream == null) return;

                using (var fs = File.Create(tempInstaller))
                    stream.CopyTo(fs);

                var psi = new ProcessStartInfo
                {
                    FileName = tempInstaller,
                    Arguments = "-install -silent",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(psi);
                proc?.WaitForExit(10000);
                try { File.Delete(tempInstaller); } catch { }
            }
            catch { }
        }

        private void InitFallbackCounters()
        {
            try
            {
                _cpuLoadFallbackCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuPerfFallbackCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");

                // Bắt buộc gọi NextValue() 1 lần đầu để làm nóng bộ đếm (lần đầu Windows luôn trả về 0)
                _cpuLoadFallbackCounter.NextValue();
                _cpuPerfFallbackCounter.NextValue();
            }
            catch { }
        }

        public void Dispose()
        {
            _cpuLoadFallbackCounter?.Dispose();
            _cpuPerfFallbackCounter?.Dispose();
            _computer.Close();
        }

        private void CheckAndRecordAlerts(SensorSnapshot snap)
        {
            var now = DateTime.Now;

            // 1. Cảnh báo CPU (Quá nhiệt giảm xung)
            if (snap.IsCpuThrottling && (now - _lastCpuAlertTime).TotalMinutes >= 2)
            {
                _lastCpuAlertTime = now;
                snap.NewAlerts.Add(new HardwareAlertEvent
                {
                    Timestamp = now,
                    Category = "CPU",
                    Message = $"Quá nhiệt giảm xung: {snap.CpuTemp:F0}°C — Xung tụt còn {snap.CpuClock:F0} MHz (Tải {snap.CpuLoad:F0}%)"
                });
            }

            // 2. Cảnh báo GPU (Quá nhiệt HOẶC nghẽn tải 98%+ liên tục trong khi CPU còn rảnh)
            if (snap.HasDiscreteGpu && !snap.IsGpuSleeping)
            {
                if (snap.GpuLoad >= 98f && snap.CpuLoad < 65f) _gpuBottleneckTicks++;
                else _gpuBottleneckTicks = 0;

                if ((snap.IsGpuThrottling || _gpuBottleneckTicks >= 5) && (now - _lastGpuAlertTime).TotalMinutes >= 2)
                {
                    _lastGpuAlertTime = now;
                    string reason = snap.IsGpuThrottling
                        ? $"GPU quá nhiệt giảm xung: {snap.GpuTemp:F0}°C (Tải {snap.GpuLoad:F0}%)"
                        : $"Nghẽn cổ chai GPU: Tải kịch khung {snap.GpuLoad:F0}% ({snap.GpuTemp:F0}°C) trong khi CPU chỉ {snap.CpuLoad:F0}%";

                    snap.NewAlerts.Add(new HardwareAlertEvent
                    {
                        Timestamp = now,
                        Category = "GPU",
                        Message = reason
                    });
                }
            }

            // 3. Cảnh báo RAM (Tràn RAM >= 90% liên tục -> Windows đẩy sang ổ cứng gây drop 1% Low FPS)
            if (snap.RamLoadPercent >= 90f) _ramBottleneckTicks++;
            else _ramBottleneckTicks = 0;

            if (_ramBottleneckTicks >= 4 && (now - _lastRamAlertTime).TotalMinutes >= 2)
            {
                _lastRamAlertTime = now;
                snap.NewAlerts.Add(new HardwareAlertEvent
                {
                    Timestamp = now,
                    Category = "RAM",
                    Message = $"Tràn bộ nhớ RAM: Đã dùng {snap.RamUsedGb:F1}/{snap.RamTotalGb:F1} GB ({snap.RamLoadPercent:F0}%) — Dễ gây khựng hình"
                });
            }
        }
    }
}