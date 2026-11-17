// using System;
// using System.Windows;
// using System.Windows.Controls;
// using System.Windows.Threading;
// using TechMonitor.Services;

// namespace TechMonitor
// {
//     public partial class MainWindow : Window
//     {
//         private readonly LiveSensorService _sensorService;
//         private readonly DispatcherTimer _timer;
//         private readonly TextBlock _debugText;

//         public MainWindow()
//         {
//             InitializeComponent();
//             _debugText = new TextBlock { FontSize = 16, Margin = new Thickness(20) };
//             this.Content = _debugText;

//             _sensorService = new LiveSensorService();

//             _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
//             _timer.Tick += (s, e) => UpdateDisplay();
//             _timer.Start();

//             UpdateDisplay();
//         }

//         private void UpdateDisplay()
//         {
//             var data = _sensorService.ReadSensors();
//             string gpuLine = !data.HasDiscreteGpu
//                 ? "GPU Rời: Không tìm thấy"
//                 : data.IsGpuSleeping
//                     ? $"GPU ({data.GpuName}): Đang ngủ (Tiết kiệm pin)"
//                     : $"GPU ({data.GpuName}): {data.GpuTemp:F0}°C | {data.GpuLoad:F0}% | {data.GpuPower:F1}W | {data.GpuClock:F0}MHz";

//             _debugText.Text =
//                 $"CPU ({data.CpuName}): {data.CpuTemp:F0}°C | {data.CpuLoad:F0}% | {data.CpuPower:F1}W | {data.CpuClock:F0}MHz\n" +
//                 $"{gpuLine}\n" +
//                 $"RAM: {data.RamUsedGb:F1} / {data.RamTotalGb:F1} GB ({data.RamLoadPercent:F0}%)\n" +
//                 $"Trạng thái: {data.HealthStatus}";
//         }

//         protected override void OnClosed(EventArgs e)
//         {
//             _timer.Stop();
//             _sensorService.Dispose();
//             base.OnClosed(e);
//         }
//     }
// }