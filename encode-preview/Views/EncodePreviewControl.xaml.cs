using System.Windows.Controls;
using EncodePreviewPlugin.ViewModels;

namespace EncodePreviewPlugin.Views
{
    public partial class EncodePreviewControl : UserControl
    {
        public EncodePreviewViewModel ViewModel { get; }

        public EncodePreviewControl() : this(new EncodePreviewViewModel())
        {
        }

        public EncodePreviewControl(EncodePreviewViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = ViewModel;
        }
    }
}
