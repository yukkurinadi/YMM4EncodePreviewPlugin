using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using EncodePreviewPlugin.ViewModels;

namespace EncodePreviewPlugin.Views
{
    public partial class EncodePreviewWindow : Window
    {
        private readonly EncodePreviewViewModel _viewModel;
        private readonly AspectRatioResizer _resizer;

        private bool _fitPending;
        private bool _userResized;

        public EncodePreviewWindow(EncodePreviewViewModel viewModel, EncodePreviewPluginSettings settings)
        {
            InitializeComponent();
            _viewModel = viewModel;
            PreviewControl.DataContext = viewModel;

            _resizer = new AspectRatioResizer(this, () => PreviewControl.VideoHostElement, GetVideoAspect);
            _resizer.UserResizing += () => _userResized = true;

            ContentRendered += (_, _) => RefitToVideoAspect();

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (nameof(EncodePreviewViewModel.PreviewAreaWidth)
                or nameof(EncodePreviewViewModel.PreviewAreaHeight)))
            {
                return;
            }

            // 幅と高さは続けて更新されるため、1回のリサイズにまとめる
            if (_fitPending) return;
            _fitPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _fitPending = false;
                RefitToVideoAspect();
            }));
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

        // 動画のアスペクト比に合わせてウィンドウをリサイズする
        private void RefitToVideoAspect()
        {
            if (WindowState != WindowState.Normal) return;

            // 手動リサイズ後はアスペクト比の補正のみ行い、ユーザーが決めた大きさを尊重する
            if (_userResized)
            {
                double hostWidth = PreviewControl.VideoHostElement.ActualWidth;
                if (hostWidth <= 1) return;
                _resizer.FitToVideoWidth(hostWidth);
                return;
            }

            // プレビュー描画領域の目標幅は ViewModel が決める (横 640 固定、縦長時は縮小)
            double targetWidth = _viewModel.PreviewAreaWidth;
            if (targetWidth <= 1) return;

            _resizer.FitToVideoWidth(targetWidth);
        }

        protected override void OnClosed(EventArgs e)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _resizer.Dispose();
            base.OnClosed(e);
        }
    }
}
