using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using HarmonyLib;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Project;

namespace EncodePreviewPlugin.Patches
{
    public static class VideoFileWriterPatch
    {
        private static byte[]? _tempPixelBuffer;
        private static ID2D1Bitmap1? _previewCpuBitmap;
        private static readonly object _bitmapLock = new();
        private static long _lastPreviewCopyTimestamp;

        // スクラブプレビュー用: 一時停止中に任意フレームを描画するための描画コンテキスト
        private static object? _scrubRenderInstance;
        private static object? _scrubVideoSource;
        private static ID2D1Bitmap? _scrubTargetBitmap;
        private static MethodBase? _scrubRenderMethod;
        private static readonly Action<int> _scrubPreviewRenderer = RenderScrubPreviewFrame;

        // スクラブプレビュー用に Render を再入呼び出ししている間だけ true (エンコードスレッド)
        [ThreadStatic]
        private static bool _renderingScrubPreview;

        private static string? _cachedLogDir;

        public static void Log(string message)
        {
            try
            {
                if (_cachedLogDir == null)
                {
                    string[] candidates =
                    {
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        Path.GetTempPath()
                    };
                    foreach (var dir in candidates)
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                            {
                                _cachedLogDir = dir;
                                break;
                            }
                        }
                        catch { }
                    }
                    if (_cachedLogDir == null) _cachedLogDir = Path.GetTempPath();
                }

                string logFile = Path.Combine(_cachedLogDir, "ymm4_encodepreview.log");
                File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        private static Type? FindVideoFileWriterType()
        {
            var t = AccessTools.TypeByName("YukkuriMovieMaker.VideoFileWriter.VideoFileWriter");
            if (t != null) return t;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = asm.GetType("YukkuriMovieMaker.VideoFileWriter.VideoFileWriter", false);
                    if (t != null) return t;

                    foreach (var candidate in asm.GetTypes())
                    {
                        if (candidate.Name == "VideoFileWriter" &&
                            candidate.Namespace != null &&
                            candidate.Namespace.Contains("VideoFileWriter", StringComparison.Ordinal))
                        {
                            return candidate;
                        }
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        public static void Apply(Harmony harmony)
        {
            try
            {
                Log("Applying Harmony patches...");
                var vfwType = FindVideoFileWriterType();
                if (vfwType == null)
                {
                    Log("ERROR: YukkuriMovieMaker.VideoFileWriter.VideoFileWriter type not found!");
                    return;
                }
                Log($"VideoFileWriter type: {vfwType.AssemblyQualifiedName}");

                // 1. Patch Constructor (Prefix) - Open window immediately
                var ctors = vfwType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var ctorPrefix = new HarmonyMethod(typeof(VideoFileWriterPatch), nameof(Constructor_Prefix));
                foreach (var ctor in ctors)
                {
                    try
                    {
                        var ps = ctor.GetParameters();
                        harmony.Patch(ctor, prefix: ctorPrefix);
                        Log($"Patched VideoFileWriter ctor ({ps.Length} params): {ctor}");
                    }
                    catch (Exception ex)
                    {
                        Log($"Skip patching ctor ({ctor}): {ex.Message}");
                    }
                }

                // 2. Patch CreateFileAsync (Prefix only)
                var createMethod = AccessTools.Method(vfwType, "CreateFileAsync");
                if (createMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(VideoFileWriterPatch), nameof(CreateFileAsync_Prefix));
                    harmony.Patch(createMethod, prefix: prefix);
                    Log("Patched VideoFileWriter.CreateFileAsync");
                }
                else
                {
                    Log("ERROR: CreateFileAsync method not found!");
                }

                // 3. Patch Render (Prefix & Postfix)
                var renderMethod = AccessTools.Method(vfwType, "Render");
                if (renderMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(VideoFileWriterPatch), nameof(Render_Prefix));
                    var postfix = new HarmonyMethod(typeof(VideoFileWriterPatch), nameof(Render_Postfix));
                    harmony.Patch(renderMethod, prefix: prefix, postfix: postfix);
                    Log("Patched VideoFileWriter.Render");
                }
                else
                {
                    Log("ERROR: Render method not found!");
                }

                // 4. Patch Dispose (Postfix) - True finish hook
                var disposeMethod = AccessTools.Method(vfwType, "Dispose", new Type[] { typeof(bool) })
                                 ?? AccessTools.Method(vfwType, "Dispose", Type.EmptyTypes);
                if (disposeMethod != null)
                {
                    var disposePostfix = new HarmonyMethod(typeof(VideoFileWriterPatch), nameof(Dispose_Postfix));
                    harmony.Patch(disposeMethod, postfix: disposePostfix);
                    Log($"Patched VideoFileWriter.Dispose: {disposeMethod}");
                }
                else
                {
                    Log("ERROR: Dispose method not found!");
                }

                // 5. Patch audio processing method (Postfix)
                var audioMethod = AccessTools.Method(vfwType, "WriteAudioSamples");
                if (audioMethod != null)
                {
                    var audioPostfix = new HarmonyMethod(typeof(VideoFileWriterPatch), nameof(WriteAudioSamples_Postfix));
                    harmony.Patch(audioMethod, postfix: audioPostfix);
                    Log("Patched VideoFileWriter.WriteAudioSamples");
                }
                else
                {
                    // Try to find audio-related methods
                    Log("WriteAudioSamples method not found, searching for audio methods...");
                    var audioMethods = vfwType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        .Where(m => m.Name.ToLower().Contains("audio") || m.Name.ToLower().Contains("sample") || m.Name.ToLower().Contains("sound"))
                        .ToList();
                    
                    foreach (var method in audioMethods)
                    {
                        Log($"Found audio-related method: {method.Name} - {method}");
                    }

                    // Try common audio method names
                    var alternativeNames = new[] { "WriteAudio", "AddAudio", "WriteSamples", "ProcessAudio", "WriteAudioData" };
                    foreach (var name in alternativeNames)
                    {
                        var altMethod = AccessTools.Method(vfwType, name);
                        if (altMethod != null)
                        {
                            Log($"Found alternative audio method: {name}");
                            var audioPostfix = new HarmonyMethod(typeof(VideoFileWriterPatch), nameof(WriteAudioSamples_Postfix));
                            harmony.Patch(altMethod, postfix: audioPostfix);
                            Log($"Patched VideoFileWriter.{name}");
                            break;
                        }
                    }
                }

                // 6. Apply ExportConfigPatch (inject options into all encoder config views)
                ExportConfigPatch.Apply(harmony);

                // 7. Patch ProgressView so the preview is merged into YMM4's own output window
                ProgressViewPatch.Apply(harmony);
            }
            catch (Exception ex)
            {
                Log($"Failed to apply patches: {ex}");
            }
        }

        public static void Constructor_Prefix(object __instance, object[] __args)
        {
            try
            {
                if (!EncodePreviewPluginSettings.Default.PluginEnabled) return;

                string? path = null;
                if (__args != null)
                {
                    foreach (var a in __args)
                    {
                        if (a is string s && !string.IsNullOrEmpty(s) && (s.Contains('\\') || s.Contains('/') || s.EndsWith(".mp4") || s.EndsWith(".mov") || s.EndsWith(".avi") || s.EndsWith(".wmv") || s.EndsWith(".png")))
                        {
                            path = s;
                            break;
                        }
                    }
                }
                // この直後に YMM4 標準の出力進捗ウィンドウが開く
                Log($"VideoFileWriter constructor called! path: {path ?? "(unknown)"}");
                ProgressViewPatch.ExpectOutputWindow = true;
                PreviewWindowManager.EnsureWindowShownDeferred();
            }
            catch (Exception ex)
            {
                Log($"Error in Constructor_Prefix: {ex}");
            }
        }

        public static void CreateFileAsync_Prefix(object __instance, ref CancellationToken cancellationToken)
        {
            try
            {
                if (!EncodePreviewPluginSettings.Default.PluginEnabled) return;

                var stopCts = new CancellationTokenSource();
                EncodeStateHolder.SetCancellationTokenSource(stopCts);
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopCts.Token);
                cancellationToken = linkedCts.Token;

                Log("CreateFileAsync_Prefix called! Token linked for StopHere feature.");
                PreviewWindowManager.EnsureWindowShownDeferred();

                var vfwType = __instance.GetType();
                var sceneField = vfwType.GetField("scene", BindingFlags.NonPublic | BindingFlags.Instance);
                var scene = sceneField?.GetValue(__instance);
                var pathField = vfwType.GetField("path", BindingFlags.NonPublic | BindingFlags.Instance);
                string outPath = (string)(pathField?.GetValue(__instance) ?? "");

                var pluginField = vfwType.GetField("plugin", BindingFlags.NonPublic | BindingFlags.Instance);
                var plugin = pluginField?.GetValue(__instance);
                string pluginName = plugin?.GetType().GetProperty("Name")?.GetValue(plugin) as string ?? "";

                // Determine if transparency is supported for this export format
                bool isTransparent = false;
                string ext = Path.GetExtension(outPath).ToLowerInvariant();
                if (ext == ".png" || ext == ".apng" || ext == ".webp" || 
                    pluginName.Contains("透過") || pluginName.Contains("アルファ") || pluginName.Contains("PNG"))
                {
                    isTransparent = true;
                }

                if (scene != null)
                {
                    var timelineProp = scene.GetType().GetProperty("Timeline");
                    var timeline = timelineProp?.GetValue(scene);
                    if (timeline != null)
                    {
                        var videoInfoProp = timeline.GetType().GetProperty("VideoInfo");
                        var videoInfo = videoInfoProp?.GetValue(timeline);
                        var lengthProp = timeline.GetType().GetProperty("Length");
                        int timelineLength = (int)(lengthProp?.GetValue(timeline) ?? 0);

                        if (videoInfo != null)
                        {
                            var widthProp = videoInfo.GetType().GetProperty("Width");
                            var heightProp = videoInfo.GetType().GetProperty("Height");
                            var fpsProp = videoInfo.GetType().GetProperty("FPS");
                            var bgProp = videoInfo.GetType().GetProperty("BackgroundColor");

                            int width = (int)(widthProp?.GetValue(videoInfo) ?? 1920);
                            int height = (int)(heightProp?.GetValue(videoInfo) ?? 1080);
                            int fps = (int)(fpsProp?.GetValue(videoInfo) ?? 60);

                            // Background color from VideoInfo
                            Color bgColor = Colors.Black;
                            var rawBg = bgProp?.GetValue(videoInfo);
                            if (rawBg != null)
                            {
                                var colorType = rawBg.GetType();
                                byte a = (byte)(colorType.GetProperty("A")?.GetValue(rawBg) ?? (byte)255);
                                byte r = (byte)(colorType.GetProperty("R")?.GetValue(rawBg) ?? (byte)0);
                                byte g = (byte)(colorType.GetProperty("G")?.GetValue(rawBg) ?? (byte)0);
                                byte b = (byte)(colorType.GetProperty("B")?.GetValue(rawBg) ?? (byte)0);
                                bgColor = Color.FromArgb(a, r, g, b);
                            }

                            int start = 0;
                            int total = timelineLength;

                            var settingsType = AccessTools.TypeByName("YukkuriMovieMaker.VideoFileWriter.VideoFileWriterSettings");
                            if (settingsType != null)
                            {
                                var defaultProp = settingsType.BaseType?.GetProperty("Default", BindingFlags.Public | BindingFlags.Static);
                                var settingsInst = defaultProp?.GetValue(null);
                                if (settingsInst != null)
                                {
                                    int encodeFrom = (int)(settingsType.GetProperty("EncodeFrom")?.GetValue(settingsInst) ?? 0);
                                    int encodeTo = (int)(settingsType.GetProperty("EncodeTo")?.GetValue(settingsInst) ?? timelineLength);
                                    start = Math.Max(0, Math.Min(Math.Min(encodeFrom, encodeTo), timelineLength - 1));
                                    int end = Math.Min(timelineLength, Math.Max(encodeFrom, encodeTo));
                                    total = Math.Max(1, end - start);
                                }
                            }

                            Log($"Encoding started: {width}x{height} @ {fps}fps, Transparent: {isTransparent}, BG: {bgColor}");
                            EncodeStateHolder.OnEncodeStart(start, total, width, height, fps, isTransparent, bgColor);
                            // OnEncodeStart が OutputPath を初期化するため、開始処理の後で記録する
                            EncodeStateHolder.SetOutputPath(string.IsNullOrWhiteSpace(outPath) ? null : outPath);

                            // Apply item hiding if 'OnlySelectedItems' option is active
                            ExportConfigManager.ApplyItemHiding(timeline as Timeline);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error in CreateFileAsync_Prefix: {ex}");
            }
        }

        public static void Dispose_Postfix()
        {
            try
            {
                Log("VideoFileWriter.Dispose called! Real encode finished. Auto-closing preview window in 800ms...");

                // バーで戻した位置まで切り詰める (再エンコードなし)
                try
                {
                    int keepFrames = EncodeStateHolder.ConsumeTrimRequest();
                    if (keepFrames > 0)
                    {
                        Log($"Trimming output to {keepFrames}f...");
                        EncodeStateHolder.SetTrimming(true);
                        try
                        {
                            // UI スレッド上で呼ばれる場合に「切り詰め中」を一度描画させる
                            TryRefreshUi();

                            bool ok = VideoTrimmer.TryTrim(
                                EncodeStateHolder.OutputPath,
                                keepFrames,
                                EncodeStateHolder.FPS,
                                out string trimMessage);

                            if (ok) Log($"Trim succeeded: {trimMessage}");
                            else Log($"Trim failed: {trimMessage}");
                        }
                        finally
                        {
                            EncodeStateHolder.SetTrimming(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error in trimming: {ex}");
                }

                EncodeStateHolder.OnEncodeFinish();
                ProgressViewPatch.ExpectOutputWindow = false;
                ExportConfigManager.RestoreItemHiding();
                
                lock (_bitmapLock)
                {
                    _previewCpuBitmap?.Dispose();
                    _previewCpuBitmap = null;
                    _tempPixelBuffer = null;
                }

                // Automatically close the preview window after output is really finished
                Task.Run(async () =>
                {
                    await Task.Delay(800);
                    PreviewWindowManager.CloseWindow();
                });
            }
            catch (Exception ex)
            {
                Log($"Error in Dispose_Postfix: {ex}");
            }
        }

        /// <summary>
        /// UI スレッド上で呼ばれた場合、保留中の描画を一度処理してから重い処理に入る。
        /// </summary>
        private static void TryRefreshUi()
        {
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null || !dispatcher.CheckAccess()) return;

                var frame = new System.Windows.Threading.DispatcherFrame();
                dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Render,
                    new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            catch
            {
            }
        }

        public static void Render_Prefix(
            object __instance,
            object videoSource,
            ID2D1Bitmap targetBitmap,
            MethodBase __originalMethod)
        {
            try
            {
                if (!EncodePreviewPluginSettings.Default.PluginEnabled) return;

                // スクラブプレビュー用の再入呼び出しは、そのまま描画だけ行う
                if (_renderingScrubPreview) return;

                // 一時停止中にスクラブ位置のフレームを描画できるよう、現在の描画コンテキストを保持
                _scrubRenderInstance = __instance;
                _scrubVideoSource = videoSource;
                _scrubTargetBitmap = targetBitmap;
                _scrubRenderMethod = __originalMethod;

                EncodeStateHolder.WaitIfPaused(_scrubPreviewRenderer);
            }
            catch (Exception ex)
            {
                Log($"Error in Render_Prefix: {ex}");
            }
        }

        /// <summary>
        /// 一時停止中に、スクラブ位置のタイムラインフレームを Render の再入呼び出しで描画する。
        /// 再入中は Render_Prefix が一時停止処理をスキップし、Render_Postfix がプレビュー用に取り込む。
        /// </summary>
        private static void RenderScrubPreviewFrame(int timelineFrame)
        {
            var instance = _scrubRenderInstance;
            var videoSource = _scrubVideoSource;
            var targetBitmap = _scrubTargetBitmap;
            var renderMethod = _scrubRenderMethod;

            if (instance == null || videoSource == null || targetBitmap == null || renderMethod == null)
            {
                return;
            }

            try
            {
                _renderingScrubPreview = true;
                renderMethod.Invoke(instance, new object?[] { videoSource, targetBitmap, timelineFrame });
            }
            catch (Exception ex)
            {
                Log($"Error in scrub preview render: {ex}");
            }
            finally
            {
                _renderingScrubPreview = false;
            }
        }

        public static void Render_Postfix(object __instance, object targetBitmap, int frame)
        {
            try
            {
                if (!EncodePreviewPluginSettings.Default.PluginEnabled) return;
                if (targetBitmap is not ID2D1Bitmap d2dBitmap) return;

                bool isScrubPreview = _renderingScrubPreview;

                if (!isScrubPreview)
                {
                    int fpsLimit = EncodePreviewPluginSettings.Default.PreviewFpsLimit;
                    if (fpsLimit > 0)
                    {
                        long now = Stopwatch.GetTimestamp();
                        double minInterval = (double)Stopwatch.Frequency / fpsLimit;
                        if (now - _lastPreviewCopyTimestamp < minInterval) return;
                        _lastPreviewCopyTimestamp = now;
                    }
                }

                CapturePreview(__instance, d2dBitmap, frame, isScrubPreview);
            }
            catch (Exception ex)
            {
                Log($"Error in Render_Postfix: {ex}");
            }
        }

        /// <summary>
        /// 描画済みビットマップを CPU 側へコピーし、プレビュー用バッファを更新する。
        /// </summary>
        private static void CapturePreview(object __instance, ID2D1Bitmap d2dBitmap, int frame, bool isScrubPreview)
        {
            var vfwType = __instance.GetType();
            var devicesField = vfwType.GetField("devicesAndContext", BindingFlags.NonPublic | BindingFlags.Instance);
            if (devicesField?.GetValue(__instance) is not IGraphicsDevicesAndContext devicesAndContext)
            {
                return;
            }

            var sceneField = vfwType.GetField("scene", BindingFlags.NonPublic | BindingFlags.Instance);
            var scene = sceneField?.GetValue(__instance);
            if (scene == null) return;

            var timelineProp = scene.GetType().GetProperty("Timeline");
            var timeline = timelineProp?.GetValue(scene);
            if (timeline == null) return;

            var videoInfoProp = timeline.GetType().GetProperty("VideoInfo");
            var videoInfo = videoInfoProp?.GetValue(timeline);
            if (videoInfo == null) return;

            int width = (int)(videoInfo.GetType().GetProperty("Width")?.GetValue(videoInfo) ?? 0);
            int height = (int)(videoInfo.GetType().GetProperty("Height")?.GetValue(videoInfo) ?? 0);

            if (width <= 0 || height <= 0) return;

            lock (_bitmapLock)
            {
                if (_previewCpuBitmap == null ||
                    _previewCpuBitmap.PixelSize.Width != width ||
                    _previewCpuBitmap.PixelSize.Height != height)
                {
                    _previewCpuBitmap?.Dispose();
                    _previewCpuBitmap = devicesAndContext.DeviceContext.CreateNotInitializedBitmap(
                        width,
                        height,
                        BitmapOptions.CannotDraw | BitmapOptions.CpuRead
                    );
                    _tempPixelBuffer = new byte[width * height * 4];
                }

                if (d2dBitmap is ID2D1Bitmap1 d2dBitmap1)
                {
                    _previewCpuBitmap.CopyFromBitmap(d2dBitmap1);
                }
                else
                {
                    _previewCpuBitmap.CopyFromBitmap(d2dBitmap);
                }

                byte[] tempBuf = _tempPixelBuffer ?? new byte[width * height * 4];
                if (_previewCpuBitmap != null)
                {
                    _previewCpuBitmap.ToBytes(ref tempBuf!);
                    _tempPixelBuffer = tempBuf;
                }

                if (_tempPixelBuffer != null)
                {
                    if (isScrubPreview)
                    {
                        // 出力済み位置 (CurrentFrame) を進めずにプレビューだけ差し替える
                        EncodeStateHolder.UpdateScrubPreviewFrame(_tempPixelBuffer, width, height, frame);
                    }
                    else
                    {
                        EncodeStateHolder.UpdateFrame(_tempPixelBuffer, width, height, frame);
                    }
                }
            }
        }

        public static void WriteAudioSamples_Postfix(object __instance, object? audioData, int sampleCount)
        {
            try
            {
                if (audioData == null || sampleCount <= 0) return;

                var vfwType = __instance.GetType();

                int sampleRate = 48000;
                int channels = 2;
                int bitsPerSample = 16;

                var audioFormatField = vfwType.GetField("audioFormat", BindingFlags.NonPublic | BindingFlags.Instance);
                if (audioFormatField != null)
                {
                    var audioFormat = audioFormatField.GetValue(__instance);
                    if (audioFormat != null)
                    {
                        var sampleRateProp = audioFormat.GetType().GetProperty("SampleRate");
                        var channelsProp = audioFormat.GetType().GetProperty("Channels");
                        var bitsProp = audioFormat.GetType().GetProperty("BitsPerSample");

                        if (sampleRateProp != null)
                            sampleRate = (int)(sampleRateProp.GetValue(audioFormat) ?? 48000);
                        if (channelsProp != null)
                            channels = (int)(channelsProp.GetValue(audioFormat) ?? 2);
                        if (bitsProp != null)
                            bitsPerSample = (int)(bitsProp.GetValue(audioFormat) ?? 16);
                    }
                }

                byte[] audioBytes;
                if (audioData is float[] floatSamples)
                {
                    audioBytes = new byte[floatSamples.Length * 2];
                    for (int i = 0; i < floatSamples.Length; i++)
                    {
                        short sample = (short)(floatSamples[i] * short.MaxValue);
                        audioBytes[i * 2] = (byte)(sample & 0xFF);
                        audioBytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                    }
                }
                else if (audioData is byte[] byteSamples)
                {
                    audioBytes = byteSamples;
                }
                else if (audioData is short[] shortSamples)
                {
                    audioBytes = new byte[shortSamples.Length * 2];
                    Buffer.BlockCopy(shortSamples, 0, audioBytes, 0, audioBytes.Length);
                }
                else
                {
                    Log($"Unsupported audio data type: {audioData.GetType()}");
                    return;
                }

                EncodeStateHolder.UpdateAudio(audioBytes, sampleRate, channels, bitsPerSample);
            }
            catch (Exception ex)
            {
                Log($"Error in WriteAudioSamples_Postfix: {ex}");
            }
        }
    }
}
