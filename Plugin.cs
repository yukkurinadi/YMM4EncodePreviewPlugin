using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using EncodePreviewPlugin.Patches;
using EncodePreviewPlugin.Views;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Settings;

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
                    AppDomain.CurrentDomain.AssemblyResolve -= ResolveHostOrPluginAssembly;
                    AppDomain.CurrentDomain.AssemblyResolve += ResolveHostOrPluginAssembly;

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

        /// <summary>
        /// C:\YMM4 以外の配置でも、起動中の YMM 本体フォルダ / プラグインフォルダから依存 DLL を解決する。
        /// </summary>
        private static Assembly? ResolveHostOrPluginAssembly(object? sender, ResolveEventArgs args)
        {
            try
            {
                var simple = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(simple) || simple.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(loaded.GetName().Name, simple, StringComparison.OrdinalIgnoreCase))
                    {
                        return loaded;
                    }
                }

                string[] dirs =
                {
                    AppContext.BaseDirectory,
                    Path.GetDirectoryName(typeof(EncodePreviewPluginMain).Assembly.Location) ?? "",
                    Path.GetDirectoryName(Environment.ProcessPath) ?? ""
                };

                foreach (var dir in dirs)
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    var path = Path.Combine(dir, simple + ".dll");
                    if (File.Exists(path))
                    {
                        return Assembly.LoadFrom(path);
                    }
                }
            }
            catch
            {
            }

            return null;
        }
    }

    public class EncodePreviewPluginSettings : SettingsBase<EncodePreviewPluginSettings>
    {
        public override string Name => "動画出力リアルタイムプレビュー";
        public override SettingsCategory Category => SettingsCategory.VideoFileWriter;

        public override bool HasSettingView => true;

        [Newtonsoft.Json.JsonIgnore]
        public override object? SettingView => new EncodePreviewSettingsControl();

        public override void Initialize()
        {
        }

        private bool _pluginEnabled = true;
        public bool PluginEnabled
        {
            get => _pluginEnabled;
            set => Set(ref _pluginEnabled, value, nameof(PluginEnabled));
        }

        private int _previewFpsLimit = 0;
        /// <summary>プレビューのフレームレート上限。0 で無制限。</summary>
        public int PreviewFpsLimit
        {
            get => _previewFpsLimit;
            set => Set(ref _previewFpsLimit, Math.Max(0, value), nameof(PreviewFpsLimit));
        }
    }
}
