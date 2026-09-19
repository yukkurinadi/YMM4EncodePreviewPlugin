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

        public static event Action? EncodingStarted;
        public static event Action? EncodingFinished;
        public static event Action<FrameBuffer>? FrameRendered;
        public static event Action<AudioBuffer>? AudioRendered;

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
