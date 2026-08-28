using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

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

        private readonly DispatcherTimer _uiTimer;
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

        public EncodePreviewViewModel()
        {
            _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16) // ~60fps UI refresh
            };
            _uiTimer.Tick += OnUiTimerTick;
            _uiTimer.Start();

            EncodeStateHolder.EncodingStarted += OnEncodingStarted;
            EncodeStateHolder.EncodingFinished += OnEncodingFinished;
        }

        private void OnEncodingStarted()
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                IsEncoding = true;
                StatusText = "出力中...";
                ResolutionText = $"{EncodeStateHolder.VideoWidth} x {EncodeStateHolder.VideoHeight} ({EncodeStateHolder.FPS} fps)";
                Progress = 0;

                // Calculate exact aspect ratio fitting size without any margins or letterboxing
                int vw = EncodeStateHolder.VideoWidth;
                int vh = EncodeStateHolder.VideoHeight;
                if (vw > 0 && vh > 0)
                {
                    double targetWidth = 480.0;
                    if (vw < vh)
                    {
                        // Vertical video (e.g. 1080x1920)
                        targetWidth = 270.0;
                    }
                    double targetHeight = targetWidth * vh / vw;

                    PreviewAreaWidth = targetWidth;
                    PreviewAreaHeight = targetHeight;
                }

                // Configure background rendering based on transparency support
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
                        VideoBackgroundBrush = new SolidColorBrush(c);
                    }
                }
            });
        }

        private void OnEncodingFinished()
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                IsEncoding = false;
                StatusText = "出力完了";
                Progress = 100;
            });
        }

        private void OnUiTimerTick(object? sender, EventArgs e)
        {
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
