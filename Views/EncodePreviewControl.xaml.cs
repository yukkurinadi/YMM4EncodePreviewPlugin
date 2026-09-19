using System.Windows;
using System.Windows.Controls;

namespace EncodePreviewPlugin.Views
{
    public partial class EncodePreviewControl : UserControl
    {
        public EncodePreviewControl()
        {
            InitializeComponent();
        }

        public FrameworkElement VideoHostElement => VideoHost;
    }
}
