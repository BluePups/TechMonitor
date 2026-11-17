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

            // Đọc cấu hình phần cứng tĩnh 1 lần lúc mở app
            LoadHardwareSpecs();

            // Bắt đầu vòng lặp cập nhật Overlay mỗi 1.5 giây
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _timer.Tick += (s, e) => RefreshSensors();
            _timer.Start();
            RefreshSensors();
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
                Icon = SystemIcons.Shield,
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
        }

        private void RefreshSensors()
        {
            if (!_config.ShowOverlay) return;
            var data = _sensorService.ReadSensors();
            _overlayWindow.UpdateData(data, _config);
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

        private void ExitApplication()
        {
            _isForceExit = true;
            _timer.Stop();
            _trayIcon.Dispose();
            _overlayWindow.Close();
            _sensorService.Dispose();
            Application.Current.Shutdown();
        }
    }
}