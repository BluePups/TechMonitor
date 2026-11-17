using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TechMonitor.Services;

namespace TechMonitor
{
    public partial class OverlayWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private IntPtr _hwnd;
        public bool IsClickThroughLocked { get; private set; } = true;
        public event Action<double, double>? PositionChanged;

        public OverlayWindow()
        {
            InitializeComponent();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            SetClickThrough(IsClickThroughLocked);
            ForceTopMost();
        }

        public void SetClickThrough(bool lockOverlay)
        {
            IsClickThroughLocked = lockOverlay;
            if (_hwnd == IntPtr.Zero) return;

            int extendedStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            int baseFlags = extendedStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;

            if (lockOverlay)
            {
                SetWindowLong(_hwnd, GWL_EXSTYLE, baseFlags | WS_EX_TRANSPARENT);
                MainBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255));
            }
            else
            {
                SetWindowLong(_hwnd, GWL_EXSTYLE, baseFlags & ~WS_EX_TRANSPARENT);
                MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsClickThroughLocked && e.ButtonState == MouseButtonState.Pressed)
            {
                this.DragMove();
                PositionChanged?.Invoke(this.Left, this.Top);
            }
        }

        public void ForceTopMost()
        {
            if (_hwnd == IntPtr.Zero || !this.IsVisible) return;

            if (this.WindowState == WindowState.Minimized)
                this.WindowState = WindowState.Normal;

            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        public void ApplyVisibilityConfig(AppConfig cfg, bool hasDiscreteGpu)
        {
            PanelCpu.Visibility = cfg.ShowCpu ? Visibility.Visible : Visibility.Collapsed;
            PanelGpu.Visibility = (cfg.ShowGpu && hasDiscreteGpu) ? Visibility.Visible : Visibility.Collapsed;
            PanelRam.Visibility = cfg.ShowRam ? Visibility.Visible : Visibility.Collapsed;
            PanelStatus.Visibility = cfg.ShowStatus ? Visibility.Visible : Visibility.Collapsed;

            // Tự động ẩn/hiện dấu chấm ngăn cách để không bị dư dấu ở đầu hoặc cuối
            var visibleBlocks = new List<FrameworkElement> { PanelCpu, PanelGpu, PanelRam, PanelStatus }
                .Where(b => b.Visibility == Visibility.Visible).ToList();

            Sep1.Visibility = (PanelCpu.Visibility == Visibility.Visible && visibleBlocks.IndexOf(PanelCpu) < visibleBlocks.Count - 1)
                ? Visibility.Visible : Visibility.Collapsed;
            Sep2.Visibility = (PanelGpu.Visibility == Visibility.Visible && visibleBlocks.IndexOf(PanelGpu) < visibleBlocks.Count - 1)
                ? Visibility.Visible : Visibility.Collapsed;
            Sep3.Visibility = (PanelRam.Visibility == Visibility.Visible && visibleBlocks.IndexOf(PanelRam) < visibleBlocks.Count - 1)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        public void UpdateData(SensorSnapshot data, AppConfig cfg)
        {
            ApplyVisibilityConfig(cfg, data.HasDiscreteGpu);
            ForceTopMost();

            if (data.IsSensorBlocked)
            {
                PanelCpu.Visibility = Visibility.Collapsed;
                PanelGpu.Visibility = Visibility.Collapsed;
                PanelRam.Visibility = Visibility.Collapsed;
                Sep1.Visibility = Sep2.Visibility = Sep3.Visibility = Visibility.Collapsed;

                PanelStatus.Visibility = Visibility.Visible;
                PanelStatus.Background = new SolidColorBrush(Color.FromArgb(55, 239, 68, 68));
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                TxtStatus.Text = "⚠ Không đọc được cảm biến (Bị chặn bởi Windows Security / Core Isolation)";
                return;
            }

            TxtCpu.Text = $"{data.CpuTemp:F0}°C ({data.CpuLoad:F0}% | {data.CpuPower:F0}W)";
            TxtGpu.Text = data.IsGpuSleeping
                ? "Đang ngủ"
                : $"{data.GpuTemp:F0}°C ({data.GpuLoad:F0}% | {data.GpuPower:F0}W)";
            TxtRam.Text = $"{data.RamUsedGb:F1}/{data.RamTotalGb:F0}GB";
            TxtStatus.Text = data.HealthStatus;

            if (data.StatusLevel == "Critical")
            {
                PanelStatus.Background = new SolidColorBrush(Color.FromArgb(45, 239, 68, 68));
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            }
            else if (data.StatusLevel == "Warning")
            {
                PanelStatus.Background = new SolidColorBrush(Color.FromArgb(45, 245, 158, 11));
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
            }
            else
            {
                PanelStatus.Background = new SolidColorBrush(Color.FromArgb(35, 34, 197, 94));
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
            }
        }
    }
}