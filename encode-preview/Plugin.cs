using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using EncodePreviewPlugin.Patches;
using YukkuriMovieMaker.Plugin;

namespace EncodePreviewPlugin
{
    public class EncodePreviewPluginMain : IPlugin
    {
        public string Name => "動画出力リアルタイムプレビュー";

        private static bool _initialized = false;
        private static readonly object _initLock = new();

        public EncodePreviewPluginMain()
        {
            EnsureInitialized();
        }

#pragma warning disable CA2255
        [ModuleInitializer]
#pragma warning restore CA2255
        public static void Initialize()
        {
            EnsureInitialized();
        }

        public static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;
                try
                {
                    VideoFileWriterPatch.Log("Plugin initialized. Applying Harmony patches...");
                    var harmony = new Harmony("com.ymm4.plugin.encodepreview");
                    VideoFileWriterPatch.Apply(harmony);
                    _initialized = true;
                    VideoFileWriterPatch.Log("Harmony patches applied successfully.");
                }
                catch (Exception ex)
                {
                    VideoFileWriterPatch.Log($"Initialization failed: {ex}");
                }
            }
        }
    }
}
