using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TechMonitor.Services
{
    public class HardwareLimitService
    {
        // --- 1. ĐỌC TRẦN GPU RỜI NGAY KHI MỞ APP ---
        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
        private static extern int NvmlInit();

        [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
        private static extern int NvmlShutdown();

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimit")]
        private static extern int NvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limitMilliWatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
        private static extern int NvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint limitMilliWatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMaxClockInfo")]
        private static extern int NvmlDeviceGetMaxClockInfo(IntPtr device, int clockType, out uint clockMhz);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperatureThreshold")]
        private static extern int NvmlDeviceGetTemperatureThreshold(IntPtr device, int thresholdType, out uint tempCelsius);

        public static (float MaxPowerW, uint MaxClockMhz, uint MaxTempC) GetDiscreteGpuLimits(string gpuName)
        {
            // Ưu tiên đọc qua NVML nếu là card NVIDIA
            if (gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (NvmlInit() == 0)
                    {
                        if (NvmlDeviceGetHandleByIndex(0, out IntPtr device) == 0)
                        {
                            float powerW = 0f;
                            if (NvmlDeviceGetPowerManagementLimit(device, out uint mw) == 0 && mw > 0)
                                powerW = mw / 1000f;
                            else if (NvmlDeviceGetEnforcedPowerLimit(device, out uint enforcedMw) == 0 && enforcedMw > 0)
                                powerW = enforcedMw / 1000f;
                            uint clockMhz = NvmlDeviceGetMaxClockInfo(device, 0, out uint cMhz) == 0 ? cMhz : 0;
                            uint maxTemp = NvmlDeviceGetTemperatureThreshold(device, 1, out uint tLimit) == 0 ? tLimit : 0;
                            NvmlShutdown();
                            return (powerW, clockMhz, maxTemp);
                        }
                        NvmlShutdown();
                    }
                }
                catch { }
            }

            // Nếu là card rời AMD Radeon RX: thử đọc SoftPowerPlayTable trong Registry
            if (gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var baseKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                    if (baseKey != null)
                    {
                        foreach (var subName in baseKey.GetSubKeyNames())
                        {
                            if (!int.TryParse(subName, out _)) continue;
                            using var subKey = baseKey.OpenSubKey(subName);
                            string desc = subKey?.GetValue("DriverDesc")?.ToString() ?? "";
                            if (desc.Equals(gpuName, StringComparison.OrdinalIgnoreCase))
                            {
                                // Một số driver AMD lưu PP_PhmSoftPowerPlayTable dạng byte[]
                                if (subKey?.GetValue("PP_PhmSoftPowerPlayTable") is byte[] table && table.Length > 40)
                                {
                                    // Giá trị TGP thường nằm ở offset cố định tùy thế hệ RDNA, nếu không chắc chắn trả về 0 để dùng Max Peak
                                    return (0, 0, 0);
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            return (0, 0, 0);
        }

        // --- 2. BÀI STRESS TEST CPU NHẸ (45 GIÂY) CÓ TỰ NGẮT AN TOÀN ---
        private CancellationTokenSource? _stressCts;
        public bool IsStressRunning { get; private set; }

        public async Task<(float PeakPowerW, float PeakClockMhz, bool StoppedByThermal)> RunCpuCeilingTestAsync(
            int durationSeconds,
            Func<SensorSnapshot> getLatestSnapshot,
            Action<int, float, float> onProgress,
            CancellationToken externalToken = default)
        {
            if (IsStressRunning) return (0, 0, false);

            IsStressRunning = true;
            _stressCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            var token = _stressCts.Token;

            float peakPower = 0f;
            float peakClock = 0f;
            bool stoppedByThermal = false;

            // Khởi chạy các luồng tính toán nặng trên toàn bộ số luồng CPU
            int threadCount = Environment.ProcessorCount;
            var workers = new Task[threadCount];
            for (int i = 0; i < threadCount; i++)
            {
                workers[i] = Task.Run(() =>
                {
                    double[] data = new double[1024];
                    for (int k = 0; k < data.Length; k++) data[k] = k + 1.12345;
                    double acc = 1.0001;

                    while (!token.IsCancellationRequested)
                    {
                        for (int j = 0; j < data.Length; j++)
                        {
                            // Ép tải nặng đơn vị tính toán dấu phẩy động (FPU) và đường truyền bộ nhớ đệm
                            acc = Math.FusedMultiplyAdd(data[j], 1.0000001, acc);
                            data[j] = Math.Sin(acc) + Math.Cos(data[j]);
                        }
                    }
                }, token);
            }

            try
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < durationSeconds && !token.IsCancellationRequested)
                {
                    await Task.Delay(1000, token);
                    var snap = getLatestSnapshot();

                    if (snap.CpuPower > peakPower) peakPower = snap.CpuPower;
                    if (snap.CpuClock > peakClock) peakClock = snap.CpuClock;

                    int remaining = Math.Max(0, durationSeconds - (int)sw.Elapsed.TotalSeconds);
                    onProgress?.Invoke(remaining, snap.CpuPower, snap.CpuTemp);

                    // Cầu chì bảo vệ: Tự ngắt ngay nếu nhiệt độ chạm 95°C hoặc kích hoạt Throttling
                    if ((!snap.IsSensorBlocked && snap.CpuTemp >= 95f) || snap.IsCpuThrottling)
                    {
                        stoppedByThermal = true;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _stressCts.Cancel();
                try { await Task.WhenAll(workers); } catch { }
                _stressCts.Dispose();
                _stressCts = null;
                IsStressRunning = false;
            }

            return (peakPower, peakClock, stoppedByThermal);
        }

        public void StopStressTest()
        {
            _stressCts?.Cancel();
        }
    }
}