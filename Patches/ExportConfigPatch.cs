using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using HarmonyLib;
using YukkuriMovieMaker.Plugin.FileWriter;
using YukkuriMovieMaker.Project;

namespace EncodePreviewPlugin.Patches
{
    public static class ExportConfigPatch
    {
        private static readonly HashSet<Type> _patchedPluginTypes = new();
        private static Harmony? _harmony;

        public static void Apply(Harmony harmony)
        {
            _harmony = harmony;
            try
            {
                // 1. Patch Mp4ConfigViewModel Constructor
                var mp4ConfigType = AccessTools.TypeByName("YukkuriMovieMaker.ViewModels.Mp4ConfigViewModel");
                if (mp4ConfigType != null)
                {
                    var ctors = mp4ConfigType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var postfix = new HarmonyMethod(typeof(ExportConfigPatch), nameof(Mp4ConfigViewModel_Postfix));
                    foreach (var ctor in ctors)
                    {
                        try
                        {
                            harmony.Patch(ctor, postfix: postfix);
                            VideoFileWriterPatch.Log($"Patched Mp4ConfigViewModel ctor: {ctor}");
                        }
                        catch (Exception ex)
                        {
                            VideoFileWriterPatch.Log($"Skip patching Mp4ConfigViewModel ctor: {ex.Message}");
                        }
                    }
                }

                // 2. Patch all IVideoFileWriterPlugin implementations currently loaded
                PatchLoadedVideoFileWriterPlugins();

                // 3. Listen for future loaded assemblies
                AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
                {
                    try
                    {
                        PatchPluginsInAssembly(args.LoadedAssembly);
                    }
                    catch { }
                };
            }
            catch (Exception ex)
            {
                VideoFileWriterPatch.Log($"Failed to apply ExportConfigPatch: {ex}");
            }
        }

        public static void PatchLoadedVideoFileWriterPlugins()
        {
            if (_harmony == null) return;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    PatchPluginsInAssembly(asm);
                }
                catch { }
            }
        }

        private static void PatchPluginsInAssembly(Assembly asm)
        {
            if (_harmony == null) return;
            if (asm.IsDynamic) return;

            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types!;
            }
            catch
            {
                return;
            }

            var pluginInterface = typeof(IVideoFileWriterPlugin);
            var postfix = new HarmonyMethod(typeof(ExportConfigPatch), nameof(GetVideoConfigView_Postfix));

            foreach (var t in types)
            {
                if (t == null || t.IsInterface || t.IsAbstract) continue;
                if (!pluginInterface.IsAssignableFrom(t)) continue;

                lock (_patchedPluginTypes)
                {
                    if (_patchedPluginTypes.Contains(t)) continue;

                    var method = AccessTools.Method(t, "GetVideoConfigView");
                    if (method != null)
                    {
                        try
                        {
                            _harmony.Patch(method, postfix: postfix);
                            _patchedPluginTypes.Add(t);
                            VideoFileWriterPatch.Log($"Patched {t.FullName}.GetVideoConfigView successfully!");
                        }
                        catch (Exception ex)
                        {
                            VideoFileWriterPatch.Log($"Failed to patch {t.FullName}.GetVideoConfigView: {ex.Message}");
                        }
                    }
                }
            }
        }

        public static void Mp4ConfigViewModel_Postfix(object __instance, object? timeline)
        {
            try
            {
                VideoFileWriterPatch.Log("Mp4ConfigViewModel opened. Registering to ExportConfigManager.");
                ExportConfigManager.OnMp4ConfigOpened(__instance, timeline as Timeline);

                // Ensure all video file writer plugins are patched
                PatchLoadedVideoFileWriterPlugins();
            }
            catch (Exception ex)
            {
                VideoFileWriterPatch.Log($"Error in Mp4ConfigViewModel_Postfix: {ex}");
            }
        }

        public static void GetVideoConfigView_Postfix(ref UIElement? __result)
        {
            try
            {
                if (__result == null) return;
                VideoFileWriterPatch.Log("Wrapping GetVideoConfigView result with extension panel.");
                __result = ExportConfigManager.WrapWithExtensionPanel(__result);
            }
            catch (Exception ex)
            {
                VideoFileWriterPatch.Log($"Error in GetVideoConfigView_Postfix: {ex}");
            }
        }
    }
}
