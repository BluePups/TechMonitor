using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TechMonitor
{
    public partial class App : Application
    {
        private static Mutex? _mutex;
        private static EventWaitHandle? _wakeUpEvent;
        private static EventWaitHandle? _shutdownOldEvent;

        private const string UniqueMutexName = "Global\\TechMonitor_SingleInstance_Mutex_2026";
        private const string UniqueWakeUpEventName = "Global\\TechMonitor_WakeUp_Event_2026";
        private const string UniqueShutdownEventName = "Global\\TechMonitor_ShutdownOld_Event_2026";

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, UniqueMutexName, out bool isNewInstance);

            if (!isNewInstance)
            {
                if (ShouldReplaceRunningInstance())
                {
                    // Bản mới hơn / chạy từ file mới -> Yêu cầu bản cũ tự đóng để giải phóng driver
                    try
                    {
                        using var shutdownEvent = EventWaitHandle.OpenExisting(UniqueShutdownEventName);
                        shutdownEvent.Set();
                    }
                    catch { }

                    // Đợi tối đa 3 giây cho tiến trình cũ thoát hẳn
                    WaitForOldProcessesToExit(3000);

                    // Chiếm lại quyền Mutex để chạy tiếp bản mới này
                    try { _mutex.WaitOne(1000); } catch (AbandonedMutexException) { }
                }
                else
                {
                    // Bấm đúp trùng vào chính file đang chạy -> Chỉ đánh thức cửa sổ cũ lên
                    try
                    {
                        using var wakeEvent = EventWaitHandle.OpenExisting(UniqueWakeUpEventName);
                        wakeEvent.Set();
                    }
                    catch { }

                    Shutdown();
                    return;
                }
            }

            StartIpcListeners();
            base.OnStartup(e);
        }

        // Kiểm tra xem file vừa bấm mở có phải là bản cập nhật mới hơn hoặc nằm ở vị trí khác bản đang chạy không
        private static bool ShouldReplaceRunningInstance()
        {
            try
            {
                string currentPath = Environment.ProcessPath ?? "";
                Version currentVer = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

                var currentProc = Process.GetCurrentProcess();
                var otherProcs = Process.GetProcessesByName(currentProc.ProcessName)
                                        .Where(p => p.Id != currentProc.Id);

                foreach (var proc in otherProcs)
                {
                    try
                    {
                        string? runningPath = proc.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(runningPath))
                        {
                            // Nếu khác đường dẫn (VD: chạy file mới tải ở Downloads trong khi bản cũ đang chạy ở ProgramData)
                            if (!runningPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
                                return true;

                            var runningVerInfo = FileVersionInfo.GetVersionInfo(runningPath);
                            if (Version.TryParse(runningVerInfo.FileVersion, out var runningVer) && currentVer > runningVer)
                                return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return false;
        }

        private static void WaitForOldProcessesToExit(int timeoutMs)
        {
            var currentProc = Process.GetCurrentProcess();
            var otherProcs = Process.GetProcessesByName(currentProc.ProcessName)
                                    .Where(p => p.Id != currentProc.Id)
                                    .ToList();

            foreach (var proc in otherProcs)
            {
                try
                {
                    if (!proc.WaitForExit(timeoutMs))
                        proc.Kill(); // Ép đóng nếu bản cũ bị treo quá 3 giây
                }
                catch { }
            }
        }

        private void StartIpcListeners()
        {
            // Luồng 1: Lắng nghe lệnh đánh thức cửa sổ chính
            _wakeUpEvent = new EventWaitHandle(false, EventResetMode.AutoReset, UniqueWakeUpEventName);
            Task.Run(() =>
            {
                while (true)
                {
                    _wakeUpEvent.WaitOne();
                    Dispatcher.Invoke(() =>
                    {
                        if (MainWindow is MainWindow mainWin)
                        {
                            mainWin.Show();
                            mainWin.WindowState = WindowState.Normal;
                            mainWin.Activate();
                        }
                    });
                }
            });

            // Luồng 2: Lắng nghe lệnh nhường quyền từ phiên bản mới hơn
            _shutdownOldEvent = new EventWaitHandle(false, EventResetMode.AutoReset, UniqueShutdownEventName);
            Task.Run(() =>
            {
                _shutdownOldEvent.WaitOne();
                Dispatcher.Invoke(() =>
                {
                    if (MainWindow is MainWindow mainWin)
                        mainWin.ExitApplication();
                    else
                        Shutdown();
                });
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            _wakeUpEvent?.Dispose();
            _shutdownOldEvent?.Dispose();
            base.OnExit(e);
        }
    }
}