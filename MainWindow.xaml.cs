using System;
using System.ComponentModel;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using TechMonitor.Services;

namespace TechMonitor
{
    public partial class MainWindow : Window
    {
        private readonly LiveSensorService _sensorService;
        private readonly SystemSpecService _specService;
        private readonly OverlayWindow _overlayWindow;
        private readonly DispatcherTimer _timer;
        private readonly TaskbarIcon _trayIcon;
        private AppConfig _config;
        private bool _isInitializing = true;
        private bool _isForceExit = false;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint VK_O = 0x4F; // Phím O -> Tổ hợp Ctrl + Shift + O

        private readonly System.Collections.ObjectModel.ObservableCollection<HardwareAlertEvent> _alertHistory = new();
        private System.ComponentModel.ICollectionView? _alertView;

        public MainWindow()
        {
            InitializeComponent();

            _config = ConfigService.Load();
            _sensorService = new LiveSensorService();
            _specService = new SystemSpecService();

            // Khởi tạo Overlay theo cấu hình đã lưu
            _overlayWindow = new OverlayWindow
            {
                Left = _config.OverlayLeft,
                Top = _config.OverlayTop
            };
            _overlayWindow.SetClickThrough(_config.LockOverlay);
            _overlayWindow.PositionChanged += (left, top) =>
            {
                _config.OverlayLeft = left;
                _config.OverlayTop = top;
                ConfigService.Save(_config);
            };

            if (_config.ShowOverlay)
                _overlayWindow.Show();

            // Khởi tạo icon ở khay hệ thống (System Tray)
            _trayIcon = CreateTrayIcon();

            // Nạp trạng thái lên các CheckBox giao diện
            LoadConfigToUi();
            _isInitializing = false;
            if (_config.AutoStartWithWindows)
            {
                ConfigService.ApplyAutoStart(true);
            }

            // Đọc cấu hình phần cứng tĩnh 1 lần lúc mở app
            LoadHardwareSpecs();

            // Bắt đầu vòng lặp cập nhật Overlay mỗi 1.5 giây
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _timer.Tick += (s, e) => RefreshSensors();
            _timer.Start();
            RefreshSensors();

            LstAlertHistory.ItemsSource = _alertHistory;
            _alertView = System.Windows.Data.CollectionViewSource.GetDefaultView(_alertHistory);
            _alertView.Filter = FilterAlertItem;

            _overlayWindow.AlertBadgeClicked += () =>
            {
                ShowMainWindow();
                TabHistory.IsSelected = true;
            };
        }

        private TaskbarIcon CreateTrayIcon()
        {
            var menu = new ContextMenu();

            var itemOpen = new MenuItem { Header = "Mở bảng điều khiển" };
            itemOpen.Click += (s, e) => ShowMainWindow();

            var itemToggleOverlay = new MenuItem { Header = "Ẩn / Hiện thanh Overlay" };
            itemToggleOverlay.Click += (s, e) =>
            {
                ChkShowOverlay.IsChecked = !(ChkShowOverlay.IsChecked ?? true);
            };

            var itemToggleLock = new MenuItem { Header = "Khóa / Mở khóa kéo thả Overlay" };
            itemToggleLock.Click += (s, e) => BtnToggleLock_Click(this, new RoutedEventArgs());

            var itemExit = new MenuItem { Header = "Thoát hoàn toàn" };
            itemExit.Click += (s, e) => ExitApplication();

            menu.Items.Add(itemOpen);
            menu.Items.Add(itemToggleOverlay);
            menu.Items.Add(itemToggleLock);
            menu.Items.Add(new Separator());
            menu.Items.Add(itemExit);

            var icon = new TaskbarIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Shield,
                ToolTipText = "TechMonitor - Theo dõi nhiệt độ & hiệu năng",
                ContextMenu = menu
            };
            icon.TrayMouseDoubleClick += (s, e) => ShowMainWindow();
            return icon;
        }

        private void LoadConfigToUi()
        {
            ChkShowOverlay.IsChecked = _config.ShowOverlay;
            ChkShowCpu.IsChecked = _config.ShowCpu;
            ChkShowGpu.IsChecked = _config.ShowGpu;
            ChkShowRam.IsChecked = _config.ShowRam;
            ChkShowStatus.IsChecked = _config.ShowStatus;

            ChkAutoStart.IsChecked = _config.AutoStartWithWindows;
            ChkMinimizeToTray.IsChecked = _config.MinimizeToTrayOnClose;
            UpdateLockButtonText();
        }

        private async void LoadHardwareSpecs()
        {
            BtnRefreshSpecs.IsEnabled = false;
            var specs = await Task.Run(() => _specService.GetSystemSpecs());

            TxtSpecCpu.Text = $"CPU: {specs.CpuName} ({specs.CpuCoresInfo})";
            TxtSpecGpu.Text = $"GPU Rời: {specs.DiscreteGpuName}";
            TxtSpecRam.Text = $"RAM: {specs.RamSummary}";
            TxtSpecRamSticks.Text = specs.RamSticks.Count > 0 ? string.Join("  |  ", specs.RamSticks) : "";

            ListDrives.ItemsSource = specs.Drives;
            BtnRefreshSpecs.IsEnabled = true;

            TxtSpecCpu.Text = $"CPU: {specs.CpuName} ({specs.CpuCoresInfo})";
            TxtSpecGpu.Text = $"GPU Rời: {specs.DiscreteGpuName}";
            TxtSpecGpuDetail.Text = specs.DiscreteGpuDetail;
            TxtSpecGpuDetail.Visibility = string.IsNullOrEmpty(specs.DiscreteGpuDetail) ? Visibility.Collapsed : Visibility.Visible;

            TxtSpecRam.Text = $"RAM: {specs.RamSummary}";
            TxtSpecRamSticks.Text = specs.RamSticks.Count > 0 ? string.Join("  |  ", specs.RamSticks) : "";
            TxtSpecBattery.Text = $"Sức khỏe Pin: {specs.BatteryHealthSummary}";
            TxtSpecUptime.Text = $"Thời gian hoạt động (Uptime): {specs.UptimeSummary}";
        }

        private void RefreshSensors()
        {
            var data = _sensorService.ReadSensors();

            if (_config.ShowOverlay)
                _overlayWindow.UpdateData(data, _config);

            if (this.IsVisible)
            {
                string gpuPeakText = data.HasDiscreteGpu
                    ? $"{data.MaxGpuTemp:F0}°C ({data.MaxGpuPower:F0}W)"
                    : "Không có";
                TxtPeakStats.Text = $"CPU Peak: {data.MaxCpuTemp:F0}°C ({data.MaxCpuPower:F0}W)   |   GPU Peak: {gpuPeakText}";
            }

            if (data.NewAlerts.Count > 0)
            {
                foreach (var alert in data.NewAlerts)
                {
                    _alertHistory.Insert(0, alert); // Mới nhất nằm trên cùng
                    if (_alertHistory.Count > 150)
                        _alertHistory.RemoveAt(_alertHistory.Count - 1);
                }
            }
        }

        private void OverlayOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            _config.ShowOverlay = ChkShowOverlay.IsChecked ?? true;
            _config.ShowCpu = ChkShowCpu.IsChecked ?? true;
            _config.ShowGpu = ChkShowGpu.IsChecked ?? true;
            _config.ShowRam = ChkShowRam.IsChecked ?? true;
            _config.ShowStatus = ChkShowStatus.IsChecked ?? true;
            ConfigService.Save(_config);

            if (_config.ShowOverlay)
            {
                if (!_overlayWindow.IsVisible) _overlayWindow.Show();
                RefreshSensors();
            }
            else
            {
                _overlayWindow.Hide();
            }
        }

        private void SettingOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            bool newAutoStart = ChkAutoStart.IsChecked ?? false;
            if (newAutoStart != _config.AutoStartWithWindows)
            {
                _config.AutoStartWithWindows = newAutoStart;
                ConfigService.ApplyAutoStart(newAutoStart);
            }

            _config.MinimizeToTrayOnClose = ChkMinimizeToTray.IsChecked ?? true;
            ConfigService.Save(_config);
        }

        private void BtnToggleLock_Click(object sender, RoutedEventArgs e)
        {
            bool nextLock = !_overlayWindow.IsClickThroughLocked;
            _overlayWindow.SetClickThrough(nextLock);
            _config.LockOverlay = nextLock;
            ConfigService.Save(_config);
            UpdateLockButtonText();
        }

        private void UpdateLockButtonText()
        {
            BtnToggleLock.Content = _overlayWindow.IsClickThroughLocked
                ? "Đang KHÓA xuyên chuột (Bấm để mở khóa kéo thả vị trí)"
                : "Đang MỞ KHÓA (Hãy kéo thanh Overlay tới góc màn hình bạn muốn)";
        }

        private void BtnRefreshSpecs_Click(object sender, RoutedEventArgs e) => LoadHardwareSpecs();

        private async void BtnCleanTemp_Click(object sender, RoutedEventArgs e)
        {
            BtnCleanTemp.IsEnabled = false;
            TxtCleanResult.Text = "Đang quét và dọn dẹp file tạm...";

            double freedMb = await Task.Run(() => _specService.CleanTempFiles());
            TxtCleanResult.Text = $"✓ Hoàn tất! Đã giải phóng {freedMb:F1} MB dung lượng rác.";

            LoadHardwareSpecs(); // Cập nhật lại thanh dung lượng ổ đĩa sau khi dọn
            BtnCleanTemp.IsEnabled = true;
        }

        private void ShowMainWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            _overlayWindow.ClearUnreadBadge();
            this.Activate();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_isForceExit && _config.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                this.Hide();
                return;
            }
            base.OnClosing(e);
        }

        private void BtnExitApp_Click(object sender, RoutedEventArgs e) => ExitApplication();

        public void ExitApplication()
        {
            _isForceExit = true;
            _timer.Stop();

            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            UnregisterHotKey(helper.Handle, HOTKEY_ID);

            _trayIcon.Dispose();
            _overlayWindow.Close();
            _sensorService.Dispose();
            Application.Current.Shutdown();
        }

        private void BtnResetPeak_Click(object sender, RoutedEventArgs e)
        {
            _sensorService.ResetPeaks();
            RefreshSensors();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            var source = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
            source?.AddHook(HwndHook);
            RegisterHotKey(helper.Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, VK_O);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                ChkShowOverlay.IsChecked = !(ChkShowOverlay.IsChecked ?? true);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private async void BtnCopyReport_Click(object sender, RoutedEventArgs e)
        {
            string report =
                $"=== BÁO CÁO TÌNH TRẠNG MÁY (TechMonitor v1.0.3) ===\n" +
                $"• {TxtSpecCpu.Text}\n" +
                $"• {TxtSpecGpu.Text}\n" +
                $"• {TxtSpecRam.Text}\n" +
                $"• {TxtSpecBattery.Text}\n" +
                $"• {TxtSpecUptime.Text}\n" +
                $"• {TxtPeakStats.Text}";

            Clipboard.SetText(report);

            string oldText = BtnCopyReport.Content.ToString() ?? "📋 Copy tình trạng máy";
            BtnCopyReport.Content = "✓ Đã copy! Dán gửi ngay";
            await Task.Delay(2000);
            BtnCopyReport.Content = oldText;
        }

        private bool FilterAlertItem(object obj)
        {
            if (obj is not HardwareAlertEvent item) return false;
            if (CmbAlertFilter.SelectedItem is not ComboBoxItem selected) return true;

            string filter = selected.Content?.ToString() ?? "Tất cả";
            return filter == "Tất cả" || item.Category.Equals(filter, StringComparison.OrdinalIgnoreCase);
        }

        private void CmbAlertFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _alertView?.Refresh();
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            _alertHistory.Clear();
            _overlayWindow.ClearUnreadBadge();
        }
    }
}