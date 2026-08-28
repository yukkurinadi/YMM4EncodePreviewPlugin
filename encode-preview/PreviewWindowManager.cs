using System;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using EncodePreviewPlugin.ViewModels;
using EncodePreviewPlugin.Views;

namespace EncodePreviewPlugin
{
    public static class PreviewWindowManager
    {
        private static Thread? _windowThread;
        private static Dispatcher? _windowDispatcher;
        private static EncodePreviewWindow? _window;
        private static EncodePreviewViewModel? _viewModel;
        private static readonly object _lock = new();

        public static void EnsureWindowShown()
        {
            lock (_lock)
            {
                if (_windowThread != null && _windowThread.IsAlive && _windowDispatcher != null)
                {
                    _windowDispatcher.BeginInvoke(() =>
                    {
                        if (_window != null)
                        {
                            if (_window.WindowState == System.Windows.WindowState.Minimized)
                            {
                                _window.WindowState = System.Windows.WindowState.Normal;
                            }
                            _window.Show();
                            _window.Activate();
                            _window.Topmost = true; // 出力時に前面に表示
                            _window.Topmost = false;
                        }
                    });
                    return;
                }

                _windowThread = new Thread(() =>
                {
                    _windowDispatcher = Dispatcher.CurrentDispatcher;
                    _viewModel = new EncodePreviewViewModel();
                    _window = new EncodePreviewWindow(_viewModel);
                    
                    _window.Closed += (s, e) =>
                    {
                        _windowDispatcher.InvokeShutdown();
                    };

                    _window.Show();
                    _window.Activate();

                    Dispatcher.Run();

                    lock (_lock)
                    {
                        _viewModel?.Dispose();
                        _viewModel = null;
                        _window = null;
                        _windowDispatcher = null;
                        _windowThread = null;
                    }
                });

                _windowThread.SetApartmentState(ApartmentState.STA);
                _windowThread.IsBackground = true;
                _windowThread.Name = "EncodePreviewWindowThread";
                _windowThread.Start();
            }
        }

        public static void CloseWindow()
        {
            lock (_lock)
            {
                if (_windowDispatcher != null && !_windowDispatcher.HasShutdownStarted)
                {
                    _windowDispatcher.BeginInvoke(() =>
                    {
                        _window?.Close();
                    });
                }
            }
        }
    }
}
