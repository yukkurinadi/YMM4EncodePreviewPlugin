using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using EncodePreviewPlugin.ViewModels;
using EncodePreviewPlugin.Views;

namespace EncodePreviewPlugin
{
    public static class PreviewWindowManager
    {
        private static EncodePreviewWindow? _window;
        private static EncodePreviewViewModel? _viewModel;
        private static Dispatcher? _previewDispatcher;
        private static readonly object _lock = new();
        private static bool _starting;
        private const string PreviewTitle = "動画出力リアルタイムプレビュー";

        private const int GWL_STYLE = -16;
        private const int WS_DISABLED = 0x08000000;
        private const int WM_ENABLE = 0x000A;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_CLOSE = 0x0010;

        [DllImport("user32.dll")]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        private static void SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
            {
                SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            }
            else
            {
                SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32());
            }
        }

        private static void ClearDisabledStyle(IntPtr hwnd)
        {
            var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            if ((style & WS_DISABLED) != 0)
            {
                SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style & ~WS_DISABLED));
            }
            EnableWindow(hwnd, true);
        }

        private static void MakePreviewModeless(Window window)
        {
            window.Owner = null;
            window.ShowActivated = false;
            window.ShowInTaskbar = true;

            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            SetParent(hwnd, IntPtr.Zero);
            ClearDisabledStyle(hwnd);
        }

        private static void AttachDisableGuard(Window window)
        {
            window.SourceInitialized += (_, _) =>
            {
                MakePreviewModeless(window);
                var hwnd = new WindowInteropHelper(window).Handle;
                var source = HwndSource.FromHwnd(hwnd);
                source?.AddHook(WndProc);
            };
            window.Loaded += (_, _) =>
            {
                MakePreviewModeless(window);
                CloseDuplicatePreviewWindows(window);
            };
        }

        private static void CloseDuplicatePreviewWindows(Window? keep)
        {
            IntPtr keepHwnd = IntPtr.Zero;
            if (keep != null)
            {
                keepHwnd = new WindowInteropHelper(keep).Handle;
            }

            try
            {
                var app = Application.Current;
                if (app != null)
                {
                    void CloseWpfDupes()
                    {
                        Window? keeper = keep;
                        var extras = new List<Window>();
                        foreach (Window w in app.Windows)
                        {
                            if (w is not EncodePreviewWindow) continue;
                            if (keeper == null) keeper = w;
                            else if (!ReferenceEquals(w, keeper)) extras.Add(w);
                        }
                        foreach (var w in extras)
                        {
                            try { w.Close(); } catch { }
                        }
                    }

                    if (app.Dispatcher.CheckAccess()) CloseWpfDupes();
                    else app.Dispatcher.BeginInvoke(CloseWpfDupes, DispatcherPriority.Normal);
                }
            }
            catch
            {
            }

            var found = new List<IntPtr>();
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                var sb = new StringBuilder(512);
                if (GetWindowText(hWnd, sb, sb.Capacity) <= 0) return true;
                if (sb.ToString() != PreviewTitle) return true;
                found.Add(hWnd);
                return true;
            }, IntPtr.Zero);

            if (found.Count <= 1) return;

            if (keepHwnd == IntPtr.Zero)
            {
                keepHwnd = found[0];
            }

            foreach (var hwnd in found)
            {
                if (hwnd == keepHwnd) continue;
                PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_ENABLE && wParam == IntPtr.Zero)
            {
                ClearDisabledStyle(hwnd);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private static void ShowOnHostDispatcher()
        {
            var d = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            void Show()
            {
                if (_window != null)
                {
                    MakePreviewModeless(_window);
                    _window.Show();
                    MakePreviewModeless(_window);
                    return;
                }

                var settings = EncodePreviewPluginSettings.Default;
                _viewModel = new EncodePreviewViewModel(settings);
                _window = new EncodePreviewWindow(_viewModel, settings)
                {
                    Owner = null,
                    ShowActivated = false,
                    ShowInTaskbar = true,
                    Topmost = true
                };
                AttachDisableGuard(_window);
                _window.Closed += (_, _) =>
                {
                    _viewModel?.Dispose();
                    _viewModel = null;
                    _window = null;
                };
                MakePreviewModeless(_window);
                _window.Show();
                MakePreviewModeless(_window);
                _previewDispatcher = Dispatcher.CurrentDispatcher;
                CloseDuplicatePreviewWindows(_window);
            }

            if (d.CheckAccess()) Show();
            else d.Invoke(Show);
        }

        public static void EnsureWindowShown()
        {
            if (!EncodePreviewPluginSettings.Default.PluginEnabled)
            {
                CloseWindow();
                return;
            }

            lock (_lock)
            {
                if (_previewDispatcher != null && !_previewDispatcher.HasShutdownStarted && _window != null)
                {
                    _previewDispatcher.BeginInvoke(() =>
                    {
                        if (_window == null) return;
                        if (_window.WindowState == WindowState.Minimized)
                        {
                            _window.WindowState = WindowState.Normal;
                        }
                        _window.Topmost = true;
                        MakePreviewModeless(_window);
                        _window.Show();
                        MakePreviewModeless(_window);
                        CloseDuplicatePreviewWindows(_window);
                    }, DispatcherPriority.Normal);
                    return;
                }

                if (_starting)
                {
                    return;
                }
                _starting = true;

                var ready = new ManualResetEventSlim(false);
                Exception? startError = null;

                var thread = new Thread(() =>
                {
                    try
                    {
                        SynchronizationContext.SetSynchronizationContext(
                            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                        var settings = EncodePreviewPluginSettings.Default;
                        _viewModel = new EncodePreviewViewModel(settings);
                        _window = new EncodePreviewWindow(_viewModel, settings)
                        {
                            Owner = null,
                            ShowActivated = false,
                            ShowInTaskbar = true,
                            Topmost = true
                        };

                        AttachDisableGuard(_window);
                        _window.Closed += (_, _) =>
                        {
                            _viewModel?.Dispose();
                            _viewModel = null;
                            _window = null;
                            Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                        };

                        MakePreviewModeless(_window);
                        _window.Show();
                        MakePreviewModeless(_window);
                        _previewDispatcher = Dispatcher.CurrentDispatcher;
                        CloseDuplicatePreviewWindows(_window);
                    }
                    catch (Exception ex)
                    {
                        startError = ex;
                    }
                    finally
                    {
                        _starting = false;
                        ready.Set();
                    }

                    if (startError == null)
                    {
                        Dispatcher.Run();
                    }

                    lock (_lock)
                    {
                        _previewDispatcher = null;
                        _window = null;
                        _viewModel = null;
                    }
                });

                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Name = "EncodePreviewUI";
                thread.Start();
                ready.Wait();

                if (startError != null && _window == null)
                {
                    Patches.VideoFileWriterPatch.Log($"Preview STA thread failed, fallback to host dispatcher: {startError}");
                    ShowOnHostDispatcher();
                }
                else
                {
                    CloseDuplicatePreviewWindows(_window);
                }
            }
        }

        public static void CloseWindow()
        {
            Dispatcher? d;
            lock (_lock)
            {
                d = _previewDispatcher;
            }

            if (d == null || d.HasShutdownStarted)
            {
                return;
            }

            d.BeginInvoke(() => _window?.Close(), DispatcherPriority.Normal);
        }
    }
}
