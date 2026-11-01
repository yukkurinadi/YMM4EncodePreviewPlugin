using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using EncodePreviewPlugin.ViewModels;

namespace EncodePreviewPlugin.Views
{
    public partial class EncodePreviewControl : UserControl
    {
        public EncodePreviewControl()
        {
            InitializeComponent();

            // Slider は IsMoveToPointEnabled により PreviewMouseLeftButtonDown を処理済みに
            // してしまうため、handledEventsToo: true で受け取る。
            ScrubSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnScrubDragStarted));
            ScrubSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnScrubDragCompleted));
            ScrubSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnScrubPreviewMouseLeftButtonDown), true);
            ScrubSlider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnScrubPreviewMouseLeftButtonUp), true);
            ScrubSlider.AddHandler(LostMouseCaptureEvent, new MouseEventHandler(OnScrubLostMouseCapture), true);
            ScrubSlider.ValueChanged += OnScrubValueChanged;
        }

        public FrameworkElement VideoHostElement => VideoHost;

        private void OnUpdateBannerClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not EncodePreviewViewModel vm || string.IsNullOrWhiteSpace(vm.UpdateUrl))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(vm.UpdateUrl) { UseShellExecute = true });
            }
            catch
            {
            }
        }

        private void OnScrubDragStarted(object sender, DragStartedEventArgs e) => BeginScrub();

        private void OnScrubDragCompleted(object sender, DragCompletedEventArgs e) => EndScrub();

        private void OnScrubPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginScrub();

        private void OnScrubPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndScrub();

        private void OnScrubLostMouseCapture(object sender, MouseEventArgs e) => EndScrub();

        private void OnScrubValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // 出力済みより先へつまみが動いてしまった場合は、選択中の位置まで戻す
            if (DataContext is not EncodePreviewViewModel vm) return;
            if (!vm.IsScrubbing && !vm.HasScrubSelection) return;

            if (ScrubSlider.Value > vm.ScrubPosition + 0.5)
            {
                ScrubSlider.Value = vm.ScrubPosition;
            }
        }

        private void BeginScrub()
        {
            if (DataContext is EncodePreviewViewModel vm)
            {
                vm.BeginScrub();
            }
        }

        private void EndScrub()
        {
            if (DataContext is EncodePreviewViewModel vm)
            {
                vm.EndScrub();
            }
        }
    }
}
