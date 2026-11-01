using System;
using System.Threading;
using System.Windows.Media;

namespace EncodePreviewPlugin
{
    public class FrameBuffer
    {
        public byte[] Pixels { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Stride { get; set; }
        public int Frame { get; set; }
        public DateTime Timestamp { get; set; }

        public FrameBuffer(int width, int height)
        {
            Width = width;
            Height = height;
            Stride = width * 4;
            Pixels = new byte[Stride * height];
        }
    }

    public class AudioBuffer
    {
        public byte[] Samples { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int BitsPerSample { get; set; }
        public DateTime Timestamp { get; set; }

        public AudioBuffer(int sampleRate, int channels, int bitsPerSample, int sampleCount)
        {
            SampleRate = sampleRate;
            Channels = channels;
            BitsPerSample = bitsPerSample;
            int bytesPerSample = bitsPerSample / 8;
            Samples = new byte[sampleCount * channels * bytesPerSample];
        }
    }

    public static class EncodeStateHolder
    {
        public static bool IsEncoding { get; private set; }
        public static bool IsPaused { get; private set; }
        public static bool IsStopRequested { get; private set; }
        public static bool IsTrimming { get; private set; }
        public static string? OutputPath { get; private set; }
        public static int StartFrame { get; private set; }
        public static int TotalFrames { get; private set; }
        public static int CurrentFrame { get; private set; }
        public static int VideoWidth { get; private set; }
        public static int VideoHeight { get; private set; }
        public static int FPS { get; private set; }
        public static bool IsTransparentSupported { get; private set; }
        public static Color BackgroundColor { get; private set; }

        // Audio properties
        public static int AudioSampleRate { get; private set; }
        public static int AudioChannels { get; private set; }
        public static int AudioBitsPerSample { get; private set; }

        private static FrameBuffer? _latestFrame;
        private static AudioBuffer? _latestAudio;
        private static readonly object _syncLock = new();

        private static readonly ManualResetEventSlim _pauseEvent = new(true);
        private static CancellationTokenSource? _stopCts;

        // 停止確定時に「ここまで残す」フレーム数 (出力先頭からの相対)。-1 のときは切り詰めなし。
        private static int _trimKeepFrames = -1;

        // スクラブ中にプレビュー描画するタイムラインフレーム。int.MinValue のときは要求なし。
        private static int _scrubPreviewFrame = int.MinValue;

        public static event Action? EncodingStarted;
        public static event Action? EncodingFinished;
        public static event Action<bool>? PauseStateChanged;
        public static event Action? StopRequested;
        public static event Action<bool>? TrimmingStateChanged;
        public static event Action<FrameBuffer>? FrameRendered;
        public static event Action<AudioBuffer>? AudioRendered;

        /// <summary>出力先のファイルパスを記録する (切り詰め処理で使用)。</summary>
        public static void SetOutputPath(string? path)
        {
            OutputPath = path;
        }

        /// <summary>
        /// 出力完了後に、指定フレーム数までファイルを切り詰めるよう要求する。
        /// </summary>
        public static void RequestTrim(int keepFrames)
        {
            lock (_syncLock)
            {
                _trimKeepFrames = Math.Max(1, keepFrames);
            }
        }

        /// <summary>
        /// 切り詰め要求を取り出してクリアする。要求がなければ -1。
        /// </summary>
        public static int ConsumeTrimRequest()
        {
            lock (_syncLock)
            {
                int value = _trimKeepFrames;
                _trimKeepFrames = -1;
                return value;
            }
        }

        public static void SetTrimming(bool value)
        {
            if (IsTrimming == value) return;
            IsTrimming = value;
            TrimmingStateChanged?.Invoke(value);
        }

        /// <summary>スクラブ位置のプレビュー描画を要求する (タイムラインフレーム指定)。</summary>
        public static void RequestScrubPreview(int timelineFrame)
        {
            lock (_syncLock)
            {
                _scrubPreviewFrame = timelineFrame;
            }
        }

        /// <summary>保留中のスクラブプレビュー要求を破棄する。</summary>
        public static void ClearScrubPreview()
        {
            lock (_syncLock)
            {
                _scrubPreviewFrame = int.MinValue;
            }
        }

        private static bool TryConsumeScrubPreview(out int timelineFrame)
        {
            lock (_syncLock)
            {
                timelineFrame = _scrubPreviewFrame;
                if (timelineFrame == int.MinValue) return false;

                _scrubPreviewFrame = int.MinValue;
                return true;
            }
        }

        /// <summary>
        /// 一時停止中は再開されるまで待機する。待機中にスクラブプレビューの要求が来たら、
        /// 指定されたコールバックでそのフレームを描画する。
        /// </summary>
        public static void WaitIfPaused(Action<int>? renderScrubPreview = null)
        {
            while (IsPaused && IsEncoding)
            {
                if (renderScrubPreview != null && TryConsumeScrubPreview(out int previewFrame))
                {
                    renderScrubPreview(previewFrame);
                    continue;
                }

                try
                {
                    CancellationToken token;
                    lock (_syncLock)
                    {
                        token = _stopCts?.Token ?? CancellationToken.None;
                    }

                    // スクラブプレビュー要求にすぐ反応できるよう短い間隔で待つ
                    if (token.CanBeCanceled)
                    {
                        _pauseEvent.Wait(20, token);
                    }
                    else
                    {
                        _pauseEvent.Wait(20);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public static void SetCancellationTokenSource(CancellationTokenSource? cts)
        {
            lock (_syncLock)
            {
                _stopCts = cts;
            }
        }

        public static void Pause()
        {
            lock (_syncLock)
            {
                if (!IsEncoding || IsPaused) return;
                IsPaused = true;
                _pauseEvent.Reset();
            }

            PauseStateChanged?.Invoke(true);
        }

        public static void Resume()
        {
            lock (_syncLock)
            {
                if (!IsPaused) return;
                IsPaused = false;
                _pauseEvent.Set();
            }

            PauseStateChanged?.Invoke(false);
        }

        public static void StopHere()
        {
            lock (_syncLock)
            {
                if (!IsEncoding) return;
                IsStopRequested = true;
                try
                {
                    _stopCts?.Cancel();
                }
                catch
                {
                }

                IsPaused = false;
                _pauseEvent.Set();
            }

            PauseStateChanged?.Invoke(false);
            StopRequested?.Invoke();
        }

        public static void OnEncodeStart(
            int startFrame,
            int totalFrames,
            int width,
            int height,
            int fps,
            bool isTransparentSupported,
            Color backgroundColor,
            int audioSampleRate = 48000,
            int audioChannels = 2,
            int audioBitsPerSample = 16)
        {
            lock (_syncLock)
            {
                IsEncoding = true;
                IsPaused = false;
                IsStopRequested = false;
                IsTrimming = false;
                OutputPath = null;
                _trimKeepFrames = -1;
                _scrubPreviewFrame = int.MinValue;
                _pauseEvent.Set();

                StartFrame = startFrame;
                TotalFrames = totalFrames;
                CurrentFrame = startFrame;
                VideoWidth = width;
                VideoHeight = height;
                FPS = fps;
                IsTransparentSupported = isTransparentSupported;
                BackgroundColor = backgroundColor;
                AudioSampleRate = audioSampleRate;
                AudioChannels = audioChannels;
                AudioBitsPerSample = audioBitsPerSample;
                _latestFrame = null;
                _latestAudio = null;
            }

            EncodingStarted?.Invoke();
        }

        public static void OnEncodeFinish()
        {
            lock (_syncLock)
            {
                IsEncoding = false;
                IsPaused = false;
                IsStopRequested = false;
                IsTrimming = false;
                OutputPath = null;
                _trimKeepFrames = -1;
                _scrubPreviewFrame = int.MinValue;
                _pauseEvent.Set();
                _stopCts?.Dispose();
                _stopCts = null;
            }

            EncodingFinished?.Invoke();
        }

        public static void UpdateFrame(byte[] pixelData, int width, int height, int frame)
        {
            CurrentFrame = frame;

            var buffer = new FrameBuffer(width, height)
            {
                Frame = frame,
                Timestamp = DateTime.UtcNow
            };

            int bytesToCopy = Math.Min(pixelData.Length, buffer.Pixels.Length);
            Buffer.BlockCopy(pixelData, 0, buffer.Pixels, 0, bytesToCopy);

            Interlocked.Exchange(ref _latestFrame, buffer);

            FrameRendered?.Invoke(buffer);
        }

        /// <summary>
        /// スクラブ位置のプレビュー用フレームを差し替える。
        /// 出力済み位置 (CurrentFrame) は変更しない。
        /// </summary>
        public static void UpdateScrubPreviewFrame(byte[] pixelData, int width, int height, int frame)
        {
            var buffer = new FrameBuffer(width, height)
            {
                Frame = frame,
                Timestamp = DateTime.UtcNow
            };

            int bytesToCopy = Math.Min(pixelData.Length, buffer.Pixels.Length);
            Buffer.BlockCopy(pixelData, 0, buffer.Pixels, 0, bytesToCopy);

            Interlocked.Exchange(ref _latestFrame, buffer);
        }

        public static void UpdateAudio(byte[] audioData, int sampleRate, int channels, int bitsPerSample)
        {
            var buffer = new AudioBuffer(sampleRate, channels, bitsPerSample, audioData.Length / (channels * (bitsPerSample / 8)))
            {
                Timestamp = DateTime.UtcNow
            };

            int bytesToCopy = Math.Min(audioData.Length, buffer.Samples.Length);
            Buffer.BlockCopy(audioData, 0, buffer.Samples, 0, bytesToCopy);

            Interlocked.Exchange(ref _latestAudio, buffer);

            AudioRendered?.Invoke(buffer);
        }

        public static FrameBuffer? GetLatestFrame()
        {
            return Volatile.Read(ref _latestFrame);
        }

        public static AudioBuffer? GetLatestAudio()
        {
            return Volatile.Read(ref _latestAudio);
        }
    }
}
