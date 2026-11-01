using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EncodePreviewPlugin.ViewModels;
using EncodePreviewPlugin.Views;
using HarmonyLib;

namespace EncodePreviewPlugin.Patches
{
    /// <summary>
    /// YMM4 標準の出力進捗ウィンドウ (ProgressView) を検出し、その中身をプレビュー UI に差し替える。
    /// </summary>
    public static class ProgressViewPatch
    {
        // 差し替え後のプレビュー描画領域の初期幅 (DIP)
        private const double TargetVideoWidth = 480;

        private static string? _outputWindowTitle;

        /// <summary>
        /// 標準出力ウィンドウへの統合パッチを適用できたか。
        /// 適用できていない場合は独立したプレビューウィンドウを使う。
        /// </summary>
        public static bool IsAvailable { get; private set; }

        /// <summary>
        /// 動画出力の開始直後は、開かれる進捗ウィンドウを出力用とみなす。
        /// </summary>
        public static bool ExpectOutputWindow { get; set; }

        public static void Apply(Harmony harmony)
        {
            try
            {
                var viewType = AccessTools.TypeByName("YukkuriMovieMaker.Views.ProgressView");
                if (viewType == null)
                {
                    VideoFileWriterPatch.Log("ProgressView type not found. Preview will use the standalone window.");
                    return;
                }

                var ctor = viewType.GetConstructor(Type.EmptyTypes);
                if (ctor == null)
                {
                    VideoFileWriterPatch.Log("ProgressView parameterless constructor not found.");
                    return;
                }

                harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(ProgressViewPatch), nameof(Constructor_Postfix)));
                IsAvailable = true;
                VideoFileWriterPatch.Log("Patched ProgressView constructor.");
            }
            catch (Exception ex)
            {
                VideoFileWriterPatch.Log($"Failed to patch ProgressView: {ex}");
            }
        }

        public static void Constructor_Postfix(object __instance)
        {
            if (__instance is not FrameworkElement view) return;

            void TryAttachSafely()
            {
                try
                {
                    if (!EncodePreviewPluginSettings.Default.PluginEnabled) return;
                    TryAttach(view);
                }
                catch (Exception ex)
                {
                    VideoFileWriterPatch.Log($"Error attaching preview to progress window: {ex}");
                }
            }

            view.Loaded += (_, _) =>
            {
                if (view.DataContext == null)
                {
                    view.DataContextChanged += (_, _) => TryAttachSafely();
                    return;
                }

                TryAttachSafely();
            };
        }

        private static string? GetOutputWindowTitle()
        {
            if (_outputWindowTitle != null) return _outputWindowTitle;

            try
            {
                var textsType = AccessTools.TypeByName("YukkuriMovieMaker.Resources.Localization.Texts");
                var value = textsType?
                    .GetProperty("OutputProgressWindowTitle", BindingFlags.Public | BindingFlags.Static)?
                    .GetValue(null) as string;

                if (!string.IsNullOrWhiteSpace(value))
                {
                    _outputWindowTitle = value;
                }
            }
            catch
            {
            }

            return _outputWindowTitle;
        }

        private static bool IsOutputWindow(string? title)
        {
            // 動画出力の開始が検出済みなら、その間に開く進捗ウィンドウは出力用
            if (ExpectOutputWindow) return true;

            if (string.IsNullOrWhiteSpace(title)) return false;

            var expected = GetOutputWindowTitle();
            if (!string.IsNullOrEmpty(expected))
            {
                return string.Equals(title, expected, StringComparison.Ordinal);
            }

            // ローカライズ文字列を取得できない場合のフォールバック
            return title.Contains("出力", StringComparison.Ordinal) &&
                   title.Contains("中", StringComparison.Ordinal);
        }

        private static void TryAttach(FrameworkElement view)
        {
            var window = Window.GetWindow(view);
            if (window == null) return;

            var dataContext = view.DataContext;
            if (dataContext == null) return;

            string? title = dataContext.GetType()
                .GetProperty("Title", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(dataContext) as string;

            if (!IsOutputWindow(title)) return;
            if (view is not ContentControl contentControl) return;
            if (contentControl.Content is not Panel root) return;
            if (root.Children.OfType<EncodePreviewControl>().Any()) return;

            var settings = EncodePreviewPluginSettings.Default;
            var viewModel = new EncodePreviewViewModel(settings);
            var control = new EncodePreviewControl { DataContext = viewModel };

            foreach (UIElement child in root.Children)
            {
                child.Visibility = Visibility.Collapsed;
            }

            int rowSpan = root is Grid grid ? Math.Max(1, grid.RowDefinitions.Count) : 1;
            control.SetValue(Grid.RowProperty, 0);
            control.SetValue(Grid.RowSpanProperty, rowSpan);
            root.Children.Add(control);

            // 標準の進捗表示は 340x120 固定なので、プレビューが収まるサイズへ広げる
            view.Width = double.NaN;
            view.Height = double.NaN;

            // 縦横を連動させるリサイザー。映像のアスペクト比に固定される。
            var resizer = new AspectRatioResizer(window, () => control.VideoHostElement, () => GetVideoAspect(viewModel));

            bool userResized = false;
            resizer.UserResizing += () => userResized = true;

            // 解像度が判明したら、プレビュー領域が目標幅になるようウィンドウを合わせ直す
            System.ComponentModel.PropertyChangedEventHandler onPropertyChanged = (_, e) =>
            {
                if (e.PropertyName is not (nameof(EncodePreviewViewModel.PreviewAreaWidth)
                    or nameof(EncodePreviewViewModel.PreviewAreaHeight)))
                {
                    return;
                }

                if (userResized) return;

                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (userResized) return;
                    double targetWidth = viewModel.PreviewAreaWidth;
                    if (targetWidth > 1) resizer.FitToVideoWidth(targetWidth);
                }));
            };

            viewModel.PropertyChanged += onPropertyChanged;

            window.Closed += (_, _) =>
            {
                viewModel.PropertyChanged -= onPropertyChanged;
                resizer.Dispose();
                viewModel.Dispose();
                PreviewWindowManager.UnregisterIntegrated();
            };

            MakeWindowResizable(window, resizer, viewModel);

            PreviewWindowManager.RegisterIntegrated();
            viewModel.SyncFromEncodeState();

            VideoFileWriterPatch.Log($"Preview merged into YMM4 output window. Title: {title}");
        }

        private static double GetVideoAspect(EncodePreviewViewModel viewModel)
        {
            if (viewModel.PreviewAreaWidth > 0 && viewModel.PreviewAreaHeight > 0)
            {
                return viewModel.PreviewAreaWidth / viewModel.PreviewAreaHeight;
            }

            int vw = EncodeStateHolder.VideoWidth;
            int vh = EncodeStateHolder.VideoHeight;
            if (vw > 0 && vh > 0)
            {
                return (double)vw / vh;
            }

            return 16.0 / 9.0;
        }

        private static void MakeWindowResizable(Window window, AspectRatioResizer resizer, EncodePreviewViewModel viewModel)
        {
            // スタイル側の SizeToContent / ResizeMode よりローカル値が優先される
            window.SizeToContent = SizeToContent.Manual;
            window.ResizeMode = ResizeMode.CanResize;
            window.MinWidth = 360;

            // プレビュー領域が映像と同じアスペクト比になるようウィンドウを合わせる
            double targetWidth = viewModel.PreviewAreaWidth;
            resizer.FitToVideoWidth(targetWidth > 1 ? targetWidth : 640);
        }
    }
}
