using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EncodePreviewPlugin.Settings
{
    public class EncodePreviewSettings : INotifyPropertyChanged
    {
        private bool _pluginEnabled = true;
        private int _previewFpsLimit = 0;
        private bool _enableAudioPlayback = false;
        private double _volume = 1.0;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool PluginEnabled
        {
            get => _pluginEnabled;
            set
            {
                if (SetField(ref _pluginEnabled, value))
                {
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public int PreviewFpsLimit
        {
            get => _previewFpsLimit;
            set
            {
                if (SetField(ref _previewFpsLimit, Math.Max(0, value)))
                {
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public bool EnableAudioPlayback
        {
            get => _enableAudioPlayback;
            set
            {
                if (SetField(ref _enableAudioPlayback, value))
                {
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public double Volume
        {
            get => _volume;
            set
            {
                if (SetField(ref _volume, Math.Clamp(value, 0.0, 1.0)))
                {
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public event EventHandler? SettingsChanged;

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
