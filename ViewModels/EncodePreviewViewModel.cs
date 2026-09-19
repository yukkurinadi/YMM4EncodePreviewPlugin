using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EncodePreviewPlugin;

namespace EncodePreviewPlugin.ViewModels
{
    public class EncodePreviewViewModel : INotifyPropertyChanged, IDisposable
    {
        private WriteableBitmap? _previewImage;
        private string _statusText = "エンコード待機中";
        private string _frameText = "0 / 0 f";
        private string _resolutionText = "-";
        private double _progress;
        private bool _isEncoding;
        private bool _hasFrame;
        private int _renderedFps;
        private int _fpsCounter;
        private DateTime _lastFpsUpdate = DateTime.UtcNow;

        private double _previewAreaWidth = 480;
        private double _previewAreaHeight = 270;

        private bool _showCheckerboard = true;
        private Brush _videoBackgroundBrush = Brushes.Black;
        private bool _hasUpdate;
        private string _updateMessage = "";
        private string? _updateUrl;

        private readonly DispatcherTimer _uiTimer;
        private readonly Dispatcher _dispatcher;
        private readonly EncodePreviewPluginSettings _settings;
        private bool _disposed;

        public event PropertyChangedEventHandler? PropertyChanged;

        public WriteableBitmap? PreviewImage
        {
            get => _previewImage;
            private set => SetField(ref _previewImage, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetField(ref _statusText, value);
        }

        public string FrameText
        {
            get => _frameText;
            private set => SetField(ref _frameText, value);
        }

        public string ResolutionText
        {
            get => _resolutionText;
            private set => SetField(ref _resolutionText, value);
        }

        public double Progress
        {
            get => _progress;
            private set => SetField(ref _progress, value);
        }

        public bool IsEncoding
        {
            get => _isEncoding;
            private set => SetField(ref _isEncoding, value);
        }

        public bool HasFrame
        {
            get => _hasFrame;
            private set => SetField(ref _hasFrame, value);
        }

        public int RenderedFps
        {
            get => _renderedFps;
            private set => SetField(ref _renderedFps, value);
        }

        public double PreviewAreaWidth
        {
            get => _previewAreaWidth;
            set => SetField(ref _previewAreaWidth, value);
        }

        public double PreviewAreaHeight
        {
            get => _previewAreaHeight;
            set => SetField(ref _previewAreaHeight, value);
        }

        public bool ShowCheckerboard
        {
            get => _showCheckerboard;
            private set => SetField(ref _showCheckerboard, value);
        }

        public Brush VideoBackgroundBrush
        {
            get => _videoBackgroundBrush;
            private set => SetField(ref _videoBackgroundBrush, value);
        }

        public bool HasUpdate
        {
            get => _hasUpdate;
            private set => SetField(ref _hasUpdate, value);
        }

        public string UpdateMessage
        {
            get => _updateMessage;
            private set => SetField(ref _updateMessage, value);
        }

        public string? UpdateUrl
        {
            get => _updateUrl;
            private set => SetField(ref _updateUrl, value);
        }

        public EncodePreviewViewModel(EncodePreviewPluginSettings settings)
        {
            _settings = settings;
            // プレビュー専用 STA スレッド上で生成される想定。YMM のモーダル Dispatcher は使わない。
            _dispatcher = Dispatcher.CurrentDispatcher;

            _uiTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            {
                Interval = GetUiInterval()
            };
            _uiTimer.Tick += OnUiTimerTick;
            _uiTimer.Start();

            EncodeStateHolder.EncodingStarted += OnEncodingStarted;
            EncodeStateHolder.EncodingFinished += OnEncodingFinished;

            _ = CheckForUpdatesAsync();
        }

        private async Task CheckForUpdatesAsync()
        {
            var result = await UpdateChecker.CheckAsync().ConfigureAwait(false);
            if (!result.HasUpdate || result.LatestVersion == null) return;

            await _dispatcher.InvokeAsync(() =>
            {
                UpdateUrl = result.HtmlUrl;
                UpdateMessage = $"アップデートがあります（{PluginVersion.Current} → {result.LatestVersion}）。クリックでリリースページを開きます。";
                HasUpdate = true;
            });
        }

        private void OnEncodingStarted()
        {
            void Update()
            {
                IsEncoding = true;
                StatusText = "出力中...";
                ResolutionText = $"{EncodeStateHolder.VideoWidth} x {EncodeStateHolder.VideoHeight} ({EncodeStateHolder.FPS} fps)";
                Progress = 0;

                int vw = EncodeStateHolder.VideoWidth;
                int vh = EncodeStateHolder.VideoHeight;
                if (vw > 0 && vh > 0)
                {
                    double targetWidth = 480.0;
                    if (vw < vh)
                    {
                        targetWidth = 270.0;
                    }
                    double targetHeight = targetWidth * vh / vw;

                    PreviewAreaWidth = targetWidth;
                    PreviewAreaHeight = targetHeight;
                }

                if (EncodeStateHolder.IsTransparentSupported)
                {
                    ShowCheckerboard = true;
                    VideoBackgroundBrush = Brushes.Transparent;
                }
                else
                {
                    ShowCheckerboard = false;
                    var c = EncodeStateHolder.BackgroundColor;
                    if (c.A == 0)
                    {
                        VideoBackgroundBrush = Brushes.Black;
                    }
                    else
                    {
                        var brush = new SolidColorBrush(c);
                        if (brush.CanFreeze) brush.Freeze();
                        VideoBackgroundBrush = brush;
                    }
                }
            }

            if (_dispatcher.CheckAccess()) Update();
            else _dispatcher.BeginInvoke(Update);
        }

        private void OnEncodingFinished()
        {
            void Update()
            {
                IsEncoding = false;
                StatusText = "出力完了";
                Progress = 100;
            }

            if (_dispatcher.CheckAccess()) Update();
            else _dispatcher.BeginInvoke(Update);
        }

        private TimeSpan GetUiInterval()
        {
            int limit = _settings.PreviewFpsLimit;
            if (limit <= 0) return TimeSpan.FromMilliseconds(16);
            return TimeSpan.FromMilliseconds(Math.Max(1.0, 1000.0 / limit));
        }

        private void OnUiTimerTick(object? sender, EventArgs e)
        {
            var interval = GetUiInterval();
            if (_uiTimer.Interval != interval)
            {
                _uiTimer.Interval = interval;
            }

            var latest = EncodeStateHolder.GetLatestFrame();
            if (latest == null) return;

            if (_previewImage == null || 
                _previewImage.PixelWidth != latest.Width || 
                _previewImage.PixelHeight != latest.Height)
            {
                _previewImage = new WriteableBitmap(
                    latest.Width,
                    latest.Height,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null
                );
                OnPropertyChanged(nameof(PreviewImage));

                // Also ensure preview aspect ratio is updated if it wasn't yet
                if (latest.Width > 0 && latest.Height > 0)
                {
                    double targetWidth = 480.0;
                    if (latest.Width < latest.Height)
                    {
                        targetWidth = 270.0;
                    }
                    PreviewAreaWidth = targetWidth;
                    PreviewAreaHeight = targetWidth * latest.Height / latest.Width;
                }
            }

            try
            {
                _previewImage.WritePixels(
                    new Int32Rect(0, 0, latest.Width, latest.Height),
                    latest.Pixels,
                    latest.Stride,
                    0
                );
                HasFrame = true;

                int current = EncodeStateHolder.CurrentFrame - EncodeStateHolder.StartFrame;
                int total = EncodeStateHolder.TotalFrames;
                FrameText = $"{current} / {total} f (現在: {EncodeStateHolder.CurrentFrame}f)";
                
                if (total > 0)
                {
                    Progress = Math.Clamp((double)current / total * 100.0, 0.0, 100.0);
                }

                if (IsEncoding)
                {
                    StatusText = $"出力中... ({Progress:F1}%)";
                }

                _fpsCounter++;
                var now = DateTime.UtcNow;
                if ((now - _lastFpsUpdate).TotalSeconds >= 1.0)
                {
                    RenderedFps = _fpsCounter;
                    _fpsCounter = 0;
                    _lastFpsUpdate = now;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EncodePreview] WritePixels error: {ex}");
            }
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _uiTimer.Stop();
            EncodeStateHolder.EncodingStarted -= OnEncodingStarted;
            EncodeStateHolder.EncodingFinished -= OnEncodingFinished;
        }
    }
}
