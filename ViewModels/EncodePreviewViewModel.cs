using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EncodePreviewPlugin.ViewModels
{
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
        public void Execute(object? parameter) => _execute();
    }

    public class EncodePreviewViewModel : INotifyPropertyChanged, IDisposable
    {
        private WriteableBitmap? _previewImage;
        private string _statusText = "エンコード待機中";
        private string _frameText = "0 / 0 f";
        private string _resolutionText = "-";
        private double _progress;
        private bool _isEncoding;
        private bool _isPaused;
        private bool _isStopRequested;
        private bool _hasFrame;
        private int _renderedFps;
        private int _fpsCounter;
        private DateTime _lastFpsUpdate = DateTime.UtcNow;

        // 停止位置バー用
        private double _scrubPosition;
        private double _scrubMaximum = 1;
        private int _scrubMaxFrames;
        private bool _isScrubbing;
        private bool _hasScrubSelection;
        private bool _isTrimming;

        // プレビュー描画領域の横幅 (元動画の解像度に関わらず固定)
        private const double PreviewTargetWidth = 640;

        // プレビュー描画領域の高さの上限。超えた場合は横幅を縮める
        private const double PreviewMaxHeight = 1280;

        private double _previewAreaWidth = 640;
        private double _previewAreaHeight = 360;

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

        public ICommand TogglePauseCommand { get; }
        public ICommand StopHereCommand { get; }

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
            private set
            {
                if (SetField(ref _isEncoding, value))
                {
                    OnPropertyChanged(nameof(CanControl));
                }
            }
        }

        public bool IsPaused
        {
            get => _isPaused;
            private set
            {
                if (SetField(ref _isPaused, value))
                {
                    OnPropertyChanged(nameof(PauseButtonText));
                }
            }
        }

        public bool IsStopRequested
        {
            get => _isStopRequested;
            private set
            {
                if (SetField(ref _isStopRequested, value))
                {
                    OnPropertyChanged(nameof(CanControl));
                    OnPropertyChanged(nameof(CanStopHere));
                }
            }
        }

        public bool IsTrimming
        {
            get => _isTrimming;
            private set
            {
                if (SetField(ref _isTrimming, value))
                {
                    OnPropertyChanged(nameof(CanControl));
                    OnPropertyChanged(nameof(CanStopHere));
                }
            }
        }

        /// <summary>停止位置バーのつまみ位置 (出力先頭からのフレーム数)。</summary>
        public double ScrubPosition
        {
            get => _scrubPosition;
            set
            {
                double v = double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;

                if (_isScrubbing || _hasScrubSelection)
                {
                    v = Math.Min(v, Math.Max(0, _scrubMaxFrames));
                }

                v = Math.Clamp(v, 0, Math.Max(1, ScrubMaximum));
                v = Math.Round(v);

                if (SetField(ref _scrubPosition, v))
                {
                    if (_isScrubbing || _hasScrubSelection)
                    {
                        HasScrubSelection = true;
                    }

                    OnPropertyChanged(nameof(ScrubTimeText));
                    OnPropertyChanged(nameof(StopHereButtonText));

                    // バー操作中は選択位置のフレームをプレビューへ反映する
                    if (_isScrubbing || _hasScrubSelection)
                    {
                        RequestScrubPreview();
                    }
                }
                else if (Math.Abs(v - value) > 0.001)
                {
                    // 出力済み範囲の外へ動かそうとした場合は、つまみを元の位置へ戻す
                    OnPropertyChanged(nameof(ScrubPosition));
                }
            }
        }

        /// <summary>停止位置バーの最大値 (総フレーム数)。</summary>
        public double ScrubMaximum
        {
            get => _scrubMaximum;
            private set
            {
                if (SetField(ref _scrubMaximum, Math.Max(1, value)))
                {
                    OnPropertyChanged(nameof(ScrubTimeText));
                }
            }
        }

        /// <summary>バーで停止位置を選択した状態かどうか。</summary>
        public bool HasScrubSelection
        {
            get => _hasScrubSelection;
            private set
            {
                if (SetField(ref _hasScrubSelection, value))
                {
                    OnPropertyChanged(nameof(StopHereButtonText));
                    OnPropertyChanged(nameof(CanStopHere));
                    OnPropertyChanged(nameof(CanControl));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>バーのつまみを操作中かどうか。</summary>
        public bool IsScrubbing
        {
            get => _isScrubbing;
            private set
            {
                if (SetField(ref _isScrubbing, value))
                {
                    OnPropertyChanged(nameof(CanStopHere));
                }
            }
        }

        public bool CanControl => IsEncoding && !IsStopRequested && !IsTrimming;

        public bool CanStopHere => CanControl && !IsScrubbing;

        public string PauseButtonText => IsPaused ? "再開" : "一時停止";

        public string StopHereButtonText =>
            HasScrubSelection ? "この位置で停止を確定" : "出力をここで停止";

        public string ScrubTimeText
        {
            get
            {
                if (EncodeStateHolder.TotalFrames <= 0) return "- / -";

                int fps = EncodeStateHolder.FPS > 0 ? EncodeStateHolder.FPS : 30;
                double position = Math.Max(0, ScrubPosition);
                double total = Math.Max(0, ScrubMaximum);

                return $"{FormatTime(position / fps)} / {FormatTime(total / fps)}  " +
                       $"({position:F0} / {total:F0} f)";
            }
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
            _dispatcher = Dispatcher.CurrentDispatcher;

            TogglePauseCommand = new RelayCommand(ExecuteTogglePause, () => CanControl);
            StopHereCommand = new RelayCommand(ExecuteStopHere, () => CanStopHere);

            _uiTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            {
                Interval = GetUiInterval()
            };
            _uiTimer.Tick += OnUiTimerTick;
            _uiTimer.Start();

            EncodeStateHolder.EncodingStarted += OnEncodingStarted;
            EncodeStateHolder.EncodingFinished += OnEncodingFinished;
            EncodeStateHolder.PauseStateChanged += OnPauseStateChanged;
            EncodeStateHolder.StopRequested += OnStopRequested;
            EncodeStateHolder.TrimmingStateChanged += OnTrimmingStateChanged;

            _ = CheckForUpdatesAsync();
        }

        private void ExecuteTogglePause()
        {
            if (!CanControl) return;

            if (IsPaused)
            {
                ClearScrubSelection();
                EncodeStateHolder.Resume();
            }
            else
            {
                EncodeStateHolder.Pause();
            }
        }

        /// <summary>バーのつまみを掴んだとき。エンコードを一時停止し、現在の出力位置を上限にする。</summary>
        public void BeginScrub()
        {
            if (_isScrubbing) return;
            if (!CanControl) return;

            int written = GetWrittenFrames();
            _scrubMaxFrames = written;
            ScrubMaximum = Math.Max(1, EncodeStateHolder.TotalFrames);

            if (!IsPaused)
            {
                EncodeStateHolder.Pause();
            }

            IsScrubbing = true;

            // まだ位置を選んでいない場合だけ、掴んだ時点の出力位置から動かし始める。
            // トラック上をクリックした場合は Slider が先にクリック位置へ動かしているため、
            // その位置を尊重する (つまみを掴んだだけなら出力位置のまま)。
            if (!HasScrubSelection)
            {
                if (_scrubPosition > written || Math.Abs(_scrubPosition - written) < 0.5)
                {
                    SetScrubPositionCore(written);
                }

                HasScrubSelection = true;
            }

            UpdateStatusForScrub();
            RequestScrubPreview();
        }

        /// <summary>バーのつまみを離したとき。一時停止したまま確定待ちにする。</summary>
        public void EndScrub()
        {
            if (!_isScrubbing) return;

            IsScrubbing = false;

            if (IsEncoding && !EncodeStateHolder.IsPaused && !IsStopRequested)
            {
                EncodeStateHolder.Pause();
            }

            HasScrubSelection = true;
            OnPropertyChanged(nameof(CanStopHere));
            OnPropertyChanged(nameof(StopHereButtonText));
            CommandManager.InvalidateRequerySuggested();
            UpdateStatusForScrub();
            RequestScrubPreview();
        }

        private void ClearScrubSelection()
        {
            IsScrubbing = false;
            _scrubMaxFrames = 0;
            HasScrubSelection = false;
            EncodeStateHolder.ClearScrubPreview();
            SetScrubPositionCore(GetWrittenFrames());
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>選択中の停止位置のフレームをプレビュー描画するよう要求する。</summary>
        private void RequestScrubPreview()
        {
            if (!IsEncoding || IsStopRequested || IsTrimming) return;

            int total = EncodeStateHolder.TotalFrames;
            int maxOffset = total > 0 ? total - 1 : 0;
            double position = Math.Clamp(_scrubPosition, 0, Math.Max(0, maxOffset));
            int offset = (int)Math.Round(position);

            // 停止位置で実際に残る最後のフレーム (position 個を残す場合は position-1) を表示する
            if (offset > 0) offset--;

            int timelineFrame = EncodeStateHolder.StartFrame + Math.Clamp(offset, 0, Math.Max(0, maxOffset));

            EncodeStateHolder.RequestScrubPreview(timelineFrame);
        }

        private void SetScrubPositionCore(double value)
        {
            double v = Math.Clamp(value, 0, Math.Max(1, ScrubMaximum));
            v = Math.Round(v);

            if (SetField(ref _scrubPosition, v, nameof(ScrubPosition)))
            {
                OnPropertyChanged(nameof(ScrubTimeText));
                OnPropertyChanged(nameof(StopHereButtonText));
            }
        }

        private void UpdateStatusForScrub()
        {
            if (!IsEncoding || IsStopRequested) return;

            if (_isScrubbing)
            {
                StatusText = $"停止位置を選択中... ({ScrubTimeText})";
            }
            else if (HasScrubSelection)
            {
                StatusText = $"一時停止中 - 停止位置 {ScrubTimeText}";
            }
        }

        private static int GetWrittenFrames()
        {
            if (EncodeStateHolder.GetLatestFrame() == null) return 0;

            int total = EncodeStateHolder.TotalFrames;
            int written = EncodeStateHolder.CurrentFrame - EncodeStateHolder.StartFrame + 1;
            if (written < 0) written = 0;
            if (total > 0 && written > total) written = total;
            return written;
        }

        private static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;

            var time = TimeSpan.FromSeconds(seconds);
            return time.TotalHours >= 1
                ? time.ToString(@"h\:mm\:ss\.f")
                : time.ToString(@"m\:ss\.f");
        }

        private void ExecuteStopHere()
        {
            if (!CanStopHere) return;

            if (HasScrubSelection)
            {
                // バーで選んだ位置で確定する (ボタン自体が確認操作)
                int written = GetWrittenFrames();
                int keepFrames = (int)Math.Round(ScrubPosition);
                keepFrames = Math.Clamp(keepFrames, 1, Math.Max(1, written));

                if (keepFrames < written)
                {
                    // 出力済みより手前: 保存後にファイルを切り詰める
                    EncodeStateHolder.RequestTrim(keepFrames);
                    StatusText = "出力を停止中 (切り詰め準備中)...";
                }
                else
                {
                    StatusText = "出力を停止中 (保存中)...";
                }

                IsStopRequested = true;
                EncodeStateHolder.StopHere();
                return;
            }

            // 確認中にエンコードが進まないよう一時停止
            bool wasPaused = IsPaused;
            if (!wasPaused)
            {
                EncodeStateHolder.Pause();
            }

            int current = EncodeStateHolder.CurrentFrame - EncodeStateHolder.StartFrame;
            int total = EncodeStateHolder.TotalFrames;
            string msg = $"現在出力済みのフレーム ({current} / {total} f、タイムライン: {EncodeStateHolder.CurrentFrame}f) で出力を終了し、動画ファイルを完成させますか？\r\n\r\n※ ここまでの出力内容で正常に保存されます。";

            var result = MessageBox.Show(msg, "出力をここで停止", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                IsStopRequested = true;
                StatusText = "出力を停止中 (保存中)...";
                EncodeStateHolder.StopHere();
            }
            else
            {
                if (!wasPaused)
                {
                    EncodeStateHolder.Resume();
                }
            }
        }

        private void OnPauseStateChanged(bool isPaused)
        {
            void Update()
            {
                IsPaused = isPaused;

                if (IsTrimming)
                {
                    StatusText = "動画を切り詰め中... (保存中)";
                }
                else if (IsStopRequested)
                {
                    StatusText = "出力を停止中 (保存中)...";
                }
                else if (isPaused && HasScrubSelection)
                {
                    StatusText = $"一時停止中 - 停止位置 {ScrubTimeText}";
                }
                else if (isPaused)
                {
                    StatusText = $"一時停止中 ({Progress:F1}%)";
                }
                else if (IsEncoding)
                {
                    StatusText = $"出力中... ({Progress:F1}%)";
                }
            }

            if (_dispatcher.CheckAccess()) Update();
            else _dispatcher.BeginInvoke(Update);
        }

        private void OnStopRequested()
        {
            void Update()
            {
                IsStopRequested = true;
                StatusText = "出力を停止中 (保存中)...";
            }

            if (_dispatcher.CheckAccess()) Update();
            else _dispatcher.BeginInvoke(Update);
        }

        private void OnTrimmingStateChanged(bool isTrimming)
        {
            void Update()
            {
                IsTrimming = isTrimming;
                if (isTrimming)
                {
                    StatusText = "動画を切り詰め中... (保存中)";
                }
            }

            if (_dispatcher.CheckAccess()) Update();
            else _dispatcher.BeginInvoke(Update);
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
                IsPaused = false;
                IsStopRequested = false;
                IsTrimming = false;
                StatusText = "出力中...";
                ResolutionText = $"{EncodeStateHolder.VideoWidth} x {EncodeStateHolder.VideoHeight} ({EncodeStateHolder.FPS} fps)";
                Progress = 0;

                _scrubMaxFrames = 0;
                IsScrubbing = false;
                HasScrubSelection = false;
                ScrubMaximum = Math.Max(1, EncodeStateHolder.TotalFrames);
                SetScrubPositionCore(0);

                UpdatePreviewArea(EncodeStateHolder.VideoWidth, EncodeStateHolder.VideoHeight);
                UpdateBackground();
            }

            if (_dispatcher.CheckAccess()) Update();
            else _dispatcher.BeginInvoke(Update);
        }

        /// <summary>
        /// 標準出力ウィンドウへ組み込まれた時点で、既に開始済みのエンコード状態を反映する。
        /// </summary>
        public void SyncFromEncodeState()
        {
            void Update()
            {
                UpdatePreviewArea(EncodeStateHolder.VideoWidth, EncodeStateHolder.VideoHeight);
                UpdateBackground();

                if (!EncodeStateHolder.IsEncoding) return;

                IsEncoding = true;
                IsPaused = EncodeStateHolder.IsPaused;
                IsStopRequested = EncodeStateHolder.IsStopRequested;
                IsTrimming = EncodeStateHolder.IsTrimming;
                ResolutionText = $"{EncodeStateHolder.VideoWidth} x {EncodeStateHolder.VideoHeight} ({EncodeStateHolder.FPS} fps)";

                int current = EncodeStateHolder.CurrentFrame - EncodeStateHolder.StartFrame;
                int total = EncodeStateHolder.TotalFrames;
                if (total > 0)
                {
                    Progress = Math.Clamp((double)current / total * 100.0, 0.0, 100.0);
                }

                _scrubMaxFrames = 0;
                IsScrubbing = false;
                HasScrubSelection = false;
                ScrubMaximum = Math.Max(1, total);
                SetScrubPositionCore(GetWrittenFrames());

                if (IsTrimming)
                {
                    StatusText = "動画を切り詰め中... (保存中)";
                }
                else if (IsStopRequested)
                {
                    StatusText = "出力を停止中 (保存中)...";
                }
                else if (IsPaused)
                {
                    StatusText = "一時停止中";
                }
                else
                {
                    StatusText = "出力中...";
                }
            }

            if (_dispatcher.CheckAccess()) Update();
            else _dispatcher.BeginInvoke(Update);
        }

        private void UpdatePreviewArea(int videoWidth, int videoHeight)
        {
            if (videoWidth <= 0 || videoHeight <= 0) return;

            // 横幅は元動画の解像度に関わらず 640 DIP 固定。
            // 縦長動画で高さが 1280 DIP を超える場合のみ、高さを基準に横幅を縮める。
            double width = PreviewTargetWidth;
            double height = width * videoHeight / videoWidth;

            if (height > PreviewMaxHeight)
            {
                height = PreviewMaxHeight;
                width = height * videoWidth / videoHeight;
            }

            PreviewAreaWidth = width;
            PreviewAreaHeight = height;
        }

        private void UpdateBackground()
        {
            if (EncodeStateHolder.IsTransparentSupported)
            {
                ShowCheckerboard = true;
                VideoBackgroundBrush = Brushes.Transparent;
                return;
            }

            ShowCheckerboard = false;
            var c = EncodeStateHolder.BackgroundColor;
            if (c.A == 0)
            {
                VideoBackgroundBrush = Brushes.Black;
                return;
            }

            var brush = new SolidColorBrush(c);
            if (brush.CanFreeze) brush.Freeze();
            VideoBackgroundBrush = brush;
        }

        private void OnEncodingFinished()
        {
            void Update()
            {
                IsEncoding = false;
                IsPaused = false;
                IsStopRequested = false;
                StatusText = "出力完了";
                Progress = 100;

                _scrubMaxFrames = 0;
                IsScrubbing = false;
                HasScrubSelection = false;
                ScrubMaximum = Math.Max(1, EncodeStateHolder.TotalFrames);
                SetScrubPositionCore(GetWrittenFrames());
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
            var interval = GetUiInterval() ;
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

                UpdatePreviewArea(latest.Width, latest.Height);
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

                ScrubMaximum = Math.Max(1, total);

                // バー操作中・停止位置選択中・停止処理中は自動で進めない
                if (!IsScrubbing && !HasScrubSelection && !IsStopRequested && !IsTrimming)
                {
                    SetScrubPositionCore(GetWrittenFrames());
                }

                if (IsTrimming)
                {
                    StatusText = "動画を切り詰め中... (保存中)";
                }
                else if (IsStopRequested)
                {
                    StatusText = "出力を停止中 (保存中)...";
                }
                else if (IsScrubbing)
                {
                    StatusText = $"停止位置を選択中... ({ScrubTimeText})";
                }
                else if (HasScrubSelection)
                {
                    StatusText = $"一時停止中 - 停止位置 {ScrubTimeText}";
                }
                else if (IsPaused)
                {
                    StatusText = $"一時停止中 ({Progress:F1}%)";
                }
                else if (IsEncoding)
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
            EncodeStateHolder.PauseStateChanged -= OnPauseStateChanged;
            EncodeStateHolder.StopRequested -= OnStopRequested;
            EncodeStateHolder.TrimmingStateChanged -= OnTrimmingStateChanged;
        }
    }
}
