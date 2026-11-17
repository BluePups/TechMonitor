using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TechMonitor
{
    public partial class App : Application
    {
        private static Mutex? _mutex;
        private static EventWaitHandle? _wakeUpEvent;
        private const string UniqueMutexName = "Global\\TechMonitor_SingleInstance_Mutex_2026";
        private const string UniqueEventName = "Global\\TechMonitor_WakeUp_Event_2026";

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, UniqueMutexName, out bool isNewInstance);

            if (!isNewInstance)
            {
                // Nếu app đã chạy rồi -> gửi tín hiệu đánh thức cửa sổ đang chạy và thoát tiến trình mới này
                try
                {
                    using var existingEvent = EventWaitHandle.OpenExisting(UniqueEventName);
                    existingEvent.Set();
                }
                catch { }

                Shutdown();
                return;
            }

            // Tạo luồng lắng nghe tín hiệu nếu người dùng bấm mở app lần 2
            _wakeUpEvent = new EventWaitHandle(false, EventResetMode.AutoReset, UniqueEventName);
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

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _wakeUpEvent?.Dispose();
            base.OnExit(e);
        }
    }
}