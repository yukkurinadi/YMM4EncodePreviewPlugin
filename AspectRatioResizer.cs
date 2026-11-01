using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace EncodePreviewPlugin
{
    /// <summary>
    /// ウィンドウの縦横比をプレビュー映像のアスペクト比に固定してリサイズさせる。
    /// 横幅を変えれば高さも、高さを変えれば幅も連動して変わる。
    /// </summary>
    public sealed class AspectRatioResizer : IDisposable
    {
        private const int WM_SIZING = 0x0214;
        private const int WMSZ_LEFT = 1;
        private const int WMSZ_RIGHT = 2;
        private const int WMSZ_TOP = 3;
        private const int WMSZ_TOPLEFT = 4;
        private const int WMSZ_TOPRIGHT = 5;
        private const int WMSZ_BOTTOM = 6;
        private const int WMSZ_BOTTOMLEFT = 7;
        private const int WMSZ_BOTTOMRIGHT = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private readonly Window _window;
        private readonly Func<FrameworkElement?> _hostProvider;
        private readonly Func<double> _aspectProvider;
        private HwndSource? _source;
        private double _chromeWidth;
        private double _chromeHeight;
        private bool _disposed;

        /// <summary>ユーザーがリサイズを開始したときに発生する。</summary>
        public event Action? UserResizing;

        /// <param name="window">対象のウィンドウ。</param>
        /// <param name="hostProvider">プレビュー映像の描画領域を返す関数。ウィンドウの枠の実寸計測に使う。</param>
        /// <param name="aspectProvider">固定したい縦横比 (幅 / 高さ) を返す関数。</param>
        public AspectRatioResizer(Window window, Func<FrameworkElement?> hostProvider, Func<double> aspectProvider)
        {
            _window = window;
            _hostProvider = hostProvider;
            _aspectProvider = aspectProvider;

            if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            {
                Attach();
            }
            else
            {
                window.SourceInitialized += OnSourceInitialized;
            }
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _window.SourceInitialized -= OnSourceInitialized;
            Attach();
        }

        private void Attach()
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero) return;

            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(WndProc);
        }

        public double ChromeWidth => _chromeWidth;
        public double ChromeHeight => _chromeHeight;

        /// <summary>
        /// ウィンドウ全体とプレビュー描画領域の差分 (枠・タイトルバー・ボタン類) を実測する。
        /// </summary>
        public bool CaptureChrome()
        {
            var host = _hostProvider();
            if (host == null || host.ActualWidth <= 0 || host.ActualHeight <= 0) return false;
            if (_window.ActualWidth <= 0 || _window.ActualHeight <= 0) return false;

            _chromeWidth = Math.Max(0, _window.ActualWidth - host.ActualWidth);
            _chromeHeight = Math.Max(0, _window.ActualHeight - host.ActualHeight);
            return _chromeWidth > 0 && _chromeHeight > 0;
        }

        /// <summary>
        /// プレビュー描画領域が指定の幅かつ映像のアスペクト比になるようウィンドウサイズを合わせる。
        /// 枠の実寸を測り直しながら収束させるので、レイアウト差異があっても比率がずれない。
        /// </summary>
        /// <param name="targetVideoWidth">プレビュー描画領域の目標幅 (DIP)。</param>
        public void FitToVideoWidth(double targetVideoWidth)
        {
            if (_disposed) return;
            if (_window.WindowState != WindowState.Normal) return;

            double aspect = _aspectProvider();
            if (!IsValidAspect(aspect)) return;

            // まだレイアウトされていなければ、計測できるサイズまで一旦広げる
            if (!CaptureChrome())
            {
                _window.Width = SafeMax(_window.MinWidth, 520);
                _window.Height = SafeMax(_window.MinHeight, 420);
                _window.UpdateLayout();
                if (!CaptureChrome()) return;
            }

            var work = SystemParameters.WorkArea;
            double requestedWidth = Math.Max(160, targetVideoWidth);

            for (int i = 0; i < 6; i++)
            {
                SyncMinHeight(aspect);

                double videoWidth = requestedWidth;
                double maxVideoWidth = Math.Max(160, work.Width - _chromeWidth);
                double maxVideoHeight = Math.Max(90, work.Height - _chromeHeight);
                videoWidth = Math.Min(videoWidth, maxVideoWidth);
                if (videoWidth / aspect > maxVideoHeight)
                {
                    videoWidth = maxVideoHeight * aspect;
                }

                double width = Math.Max(Math.Round(videoWidth + _chromeWidth), SafeMax(_window.MinWidth, 0));
                double height = Math.Max(Math.Round(videoWidth / aspect + _chromeHeight), SafeMax(_window.MinHeight, 0));

                _window.Width = width;
                _window.Height = height;
                _window.UpdateLayout();

                var host = _hostProvider();
                if (host == null || host.ActualWidth <= 0.5 || host.ActualHeight <= 0.5) break;

                double chromeWidth = Math.Max(0, _window.ActualWidth - host.ActualWidth);
                double chromeHeight = Math.Max(0, _window.ActualHeight - host.ActualHeight);
                if (chromeWidth <= 0 || chromeHeight <= 0) break;
                _chromeWidth = chromeWidth;
                _chromeHeight = chromeHeight;

                // 画面やウィンドウの上限で高さが切り詰められると、映像領域が横長になり黒帯が出る。
                // その場合は切り詰め後の実寸から正しい幅を求め直して収束させる。
                double hostWidth = host.ActualWidth;
                double hostHeight = host.ActualHeight;
                double expectedHeight = hostWidth / aspect;

                if (expectedHeight > hostHeight + 0.5)
                {
                    // 高さが足りない分だけ幅を詰める
                    double corrected = hostHeight * aspect;
                    if (Math.Abs(corrected - requestedWidth) < 0.5) break;
                    requestedWidth = corrected;
                    continue;
                }

                if (Math.Abs(expectedHeight - hostHeight) <= 0.5) break;
            }

            ClampToWorkArea(work);
        }

        // 最小サイズも映像のアスペクト比に合わせ、縦横連動と矛盾しない値にする
        private void SyncMinHeight(double aspect)
        {
            double minWidth = SafeMax(_window.MinWidth, 0);
            if (minWidth <= 0) return;

            double minHeight = (minWidth - _chromeWidth) / aspect + _chromeHeight;
            if (minHeight > 0 && !double.IsNaN(minHeight) && !double.IsInfinity(minHeight))
            {
                _window.MinHeight = Math.Round(minHeight);
            }
        }

        private void ClampToWorkArea(Rect work)
        {
            if (double.IsNaN(_window.Left) || double.IsNaN(_window.Top)) return;

            _window.Left = Math.Clamp(_window.Left, work.Left, Math.Max(work.Left, work.Right - _window.Width));
            _window.Top = Math.Clamp(_window.Top, work.Top, Math.Max(work.Top, work.Bottom - _window.Height));
        }

        private static bool IsValidAspect(double aspect)
        {
            return aspect > 0 && !double.IsNaN(aspect) && !double.IsInfinity(aspect);
        }

        // double.NaN を無視して大きい方を返す (Math.Max は NaN を伝播させるため)
        private static double SafeMax(double a, double b)
        {
            bool aValid = !double.IsNaN(a);
            bool bValid = !double.IsNaN(b);
            if (aValid && bValid) return Math.Max(a, b);
            if (aValid) return a;
            if (bValid) return b;
            return 0;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_SIZING || _disposed)
            {
                return IntPtr.Zero;
            }

            try
            {
                var rect = Marshal.PtrToStructure<RECT>(lParam);
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0)
                {
                    return IntPtr.Zero;
                }

                double aspect = _aspectProvider();
                if (!IsValidAspect(aspect))
                {
                    return IntPtr.Zero;
                }

                if (_chromeWidth <= 0 || _chromeHeight <= 0)
                {
                    CaptureChrome();
                }

                // WM_SIZING の矩形は物理ピクセル、枠の計測値は DIP なので換算する
                var dpi = VisualTreeHelper.GetDpi(_window);
                double chromeW = _chromeWidth * dpi.DpiScaleX;
                double chromeH = _chromeHeight * dpi.DpiScaleY;

                double videoW = Math.Max(1, width - chromeW);
                double videoH = Math.Max(1, height - chromeH);
                int edge = wParam.ToInt32();

                UserResizing?.Invoke();

                // 左右の辺は幅基準、上下の辺は高さ基準、角は幅基準で高さを連動させる
                bool adjustHeight = edge is not (WMSZ_TOP or WMSZ_BOTTOM);

                if (adjustHeight)
                {
                    videoH = videoW / aspect;
                    int newHeight = (int)Math.Round(videoH + chromeH);
                    if (edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT)
                    {
                        rect.Top = rect.Bottom - newHeight;
                    }
                    else
                    {
                        rect.Bottom = rect.Top + newHeight;
                    }
                }
                else
                {
                    videoW = videoH * aspect;
                    int newWidth = (int)Math.Round(videoW + chromeW);
                    if (edge is WMSZ_LEFT or WMSZ_TOPLEFT or WMSZ_BOTTOMLEFT)
                    {
                        rect.Left = rect.Right - newWidth;
                    }
                    else
                    {
                        rect.Right = rect.Left + newWidth;
                    }
                }

                ClampToMinimum(ref rect, edge, aspect, dpi, chromeW, chromeH);

                Marshal.StructureToPtr(rect, lParam, true);
                handled = true;
            }
            catch
            {
            }

            return IntPtr.Zero;
        }

        // 連動で縮めた結果が最小サイズを下回らないよう、比率を保ったまま拡大し直す
        private void ClampToMinimum(ref RECT rect, int edge, double aspect, DpiScale dpi, double chromeW, double chromeH)
        {
            double minWidth = SafeMax(_window.MinWidth, 0) * dpi.DpiScaleX;
            double minHeight = SafeMax(_window.MinHeight, 0) * dpi.DpiScaleY;

            double width = rect.Right - rect.Left;
            double height = rect.Bottom - rect.Top;
            if (width >= minWidth && height >= minHeight) return;

            double requiredWidthForMinHeight = Math.Max(1, (minHeight - chromeH) * aspect + chromeW);
            double newWidth = Math.Max(Math.Max(minWidth, requiredWidthForMinHeight), width);
            double newHeight = (newWidth - chromeW) / aspect + chromeH;
            if (newHeight < minHeight)
            {
                newHeight = minHeight;
                newWidth = (newHeight - chromeH) * aspect + chromeW;
            }

            int w = (int)Math.Round(newWidth);
            int h = (int)Math.Round(newHeight);

            if (edge is WMSZ_LEFT or WMSZ_TOPLEFT or WMSZ_BOTTOMLEFT)
            {
                rect.Left = rect.Right - w;
            }
            else
            {
                rect.Right = rect.Left + w;
            }

            if (edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT)
            {
                rect.Top = rect.Bottom - h;
            }
            else
            {
                rect.Bottom = rect.Top + h;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _window.SourceInitialized -= OnSourceInitialized;
            _source?.RemoveHook(WndProc);
            _source = null;
        }
    }
}
