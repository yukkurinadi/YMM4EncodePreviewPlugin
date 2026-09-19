using System.Windows.Controls;
using EncodePreviewPlugin;

namespace EncodePreviewPlugin.Views
{
    public partial class EncodePreviewSettingsControl : UserControl
    {
        public EncodePreviewSettingsControl()
        {
            InitializeComponent();
            DataContext = EncodePreviewPluginSettings.Default;
        }
    }
}
