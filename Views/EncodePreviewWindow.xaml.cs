using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EncodePreviewPlugin.ViewModels;

namespace EncodePreviewPlugin.Views
{
    public partial class EncodePreviewWindow : Window
    {
        private readonly EncodePreviewViewModel _viewModel;
        private const int WM_SIZING = 0x0214;
        private const int WMSZ_LEFT = 1;
        private const int WMSZ_RIGHT = 2;
        private const int WMSZ_TOP = 3;
        private const int WMSZ_TOPLEFT = 4;
        private const int WMSZ_TOPRIGHT = 5;
        private const int WMSZ_BOTTOM = 6;
        private const int WMSZ_BOTTOMLEFT = 7;
        private const int WMSZ_BOTTOMRIGHT = 8;

        private double _chromeWidth;
        private double _chromeHeight;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public EncodePreviewWindow(EncodePreviewViewModel viewModel, EncodePreviewPluginSettings settings)
        {
            InitializeComponent();
            _viewModel = viewModel;
            PreviewControl.DataContext = viewModel;

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            };

            ContentRendered += (_, _) =>
            {
                SizeToContent = SizeToContent.Manual;
                CacheChrome();
            };
        }

        private void CacheChrome()
        {
            var host = PreviewControl.VideoHostElement;
            if (host.ActualWidth <= 0 || host.ActualHeight <= 0) return;
            _chromeWidth = Math.Max(0, ActualWidth - host.ActualWidth);
            _chromeHeight = Math.Max(0, ActualHeight - host.ActualHeight);
        }

        private double GetVideoAspect()
        {
            if (_viewModel.PreviewAreaWidth > 0 && _viewModel.PreviewAreaHeight > 0)
            {
                return _viewModel.PreviewAreaWidth / _viewModel.PreviewAreaHeight;
            }

            int vw = EncodeStateHolder.VideoWidth;
            int vh = EncodeStateHolder.VideoHeight;
            if (vw > 0 && vh > 0)
            {
                return (double)vw / vh;
            }

            return 16.0 / 9.0;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_SIZING)
            {
                return IntPtr.Zero;
            }

            var rect = Marshal.PtrToStructure<RECT>(lParam);
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
            {
                return IntPtr.Zero;
            }

            double aspect = GetVideoAspect();
            double chromeW = _chromeWidth;
            double chromeH = _chromeHeight;
            double videoW = Math.Max(1, width - chromeW);
            double videoH = Math.Max(1, height - chromeH);
            int edge = wParam.ToInt32();

            bool adjustHeight = edge is WMSZ_LEFT or WMSZ_RIGHT or WMSZ_TOPLEFT or WMSZ_TOPRIGHT or WMSZ_BOTTOMLEFT or WMSZ_BOTTOMRIGHT;
            if (edge is WMSZ_TOP or WMSZ_BOTTOM)
            {
                adjustHeight = false;
            }

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

            Marshal.StructureToPtr(rect, lParam, true);
            handled = true;
            return IntPtr.Zero;
        }
    }
}
