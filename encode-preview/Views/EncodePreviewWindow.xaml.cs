using System;
using System.Windows;
using EncodePreviewPlugin.ViewModels;

namespace EncodePreviewPlugin.Views
{
    public partial class EncodePreviewWindow : Window
    {
        private static EncodePreviewWindow? _currentInstance;

        public EncodePreviewWindow(EncodePreviewViewModel viewModel)
        {
            InitializeComponent();
            PreviewControl.DataContext = viewModel;

            Closed += (s, e) =>
            {
                if (_currentInstance == this)
                {
                    _currentInstance = null;
                }
            };
        }

        public static void ShowWindow(EncodePreviewViewModel viewModel)
        {
            if (_currentInstance != null)
            {
                if (_currentInstance.WindowState == WindowState.Minimized)
                {
                    _currentInstance.WindowState = WindowState.Normal;
                }
                _currentInstance.Activate();
                return;
            }

            _currentInstance = new EncodePreviewWindow(viewModel);
            _currentInstance.Show();
        }
    }
}
