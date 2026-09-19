namespace EncodePreviewPlugin.Settings
{
    public static class SettingsManager
    {
        private static EncodePreviewSettings? _instance;

        public static EncodePreviewSettings Instance
        {
            get
            {
                _instance ??= new EncodePreviewSettings();
                return _instance;
            }
        }

        public static void Initialize(EncodePreviewSettings settings)
        {
            _instance = settings;
        }
    }
}
