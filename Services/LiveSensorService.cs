using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

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
    }

    public class LiveSensorService : IDisposable
    {
        private readonly Computer _computer;
        private IHardware? _cpuHardware;
        private IHardware? _discreteGpuHardware;
        private IHardware? _ramHardware;
        private DateTime _lastBatteryGpuPoll = DateTime.MinValue;
        private SensorSnapshot _lastSnapshot = new();

        public LiveSensorService()
        {
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
                var throttleFlag = GetSensorValue(_cpuHardware, SensorType.Factor, false, "Thermal Throttling");
                snap.IsCpuThrottling = (throttleFlag >= 1f && snap.CpuTemp >= 85f) ||
                                       (snap.CpuLoad > 60f && snap.CpuTemp >= 92f && snap.CpuClock > 0 && snap.CpuClock < 2200f);
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
            EvaluateOverallHealth(snap);

            _lastSnapshot = snap;
            return snap;
        }

        private void EvaluateOverallHealth(SensorSnapshot snap)
        {
            // Nếu cả nhiệt độ lẫn công suất CPU đều bằng 0 -> Driver đã bị Windows Security / Core Isolation chặn
            if (snap.CpuTemp <= 0f && snap.CpuPower <= 0f)
            {
                snap.IsSensorBlocked = true;
                snap.StatusLevel = "Critical";
                snap.HealthStatus = "Bị chặn cảm biến (Kiểm tra Windows Security / Core Isolation)";
                return;
            }

            if (snap.IsCpuThrottling || snap.IsGpuThrottling)
            {
                snap.StatusLevel = "Critical";
                snap.HealthStatus = "Quá nhiệt - Đang giảm xung!";
            }
            else if (snap.CpuTemp >= 90f || snap.GpuTemp >= 84f)
            {
                snap.StatusLevel = "Critical";
                snap.HealthStatus = "Nhiệt rất cao - Cần kiểm tra tản nhiệt";
            }
            else if (snap.CpuTemp >= 78f || snap.GpuTemp >= 75f)
            {
                snap.StatusLevel = "Warning";
                snap.HealthStatus = "Tải cao";
            }
            else
            {
                snap.StatusLevel = "Normal";
                snap.HealthStatus = "Mát mẻ / Ổn định";
            }
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

        public void Dispose()
        {
            _computer.Close(); // Bắt buộc đóng để giải phóng driver đọc phần cứng
        }
    }
}