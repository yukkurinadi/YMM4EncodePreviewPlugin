using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EncodePreviewPlugin.ViewModels;

namespace EncodePreviewPlugin.Views
{
    public partial class EncodePreviewControl : UserControl
    {
        public EncodePreviewControl()
        {
            InitializeComponent();
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
    }
}
