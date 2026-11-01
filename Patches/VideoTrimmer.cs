using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace EncodePreviewPlugin.Patches
{
    /// <summary>
    /// 出力済みの動画ファイルを、指定フレーム数まで「再エンコードせずに」切り詰める。
    /// ffmpeg のストリームコピー (-c copy) を使うため、画質は変化せず処理も高速。
    /// </summary>
    public static class VideoTrimmer
    {
        private static readonly HashSet<string> TrimmableExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".asf",
            ".ts", ".m2ts", ".mpg", ".mpeg", ".flv",
            ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus"
        };

        // ffmpeg が固まった場合に備えた上限 (10 分)
        private const int FfmpegTimeoutMs = 10 * 60 * 1000;

        /// <summary>
        /// 出力ファイルを keepFrames フレーム分だけ残して切り詰める。
        /// 成功した場合は元のファイルが置き換えられる。
        /// </summary>
        public static bool TryTrim(string? inputPath, int keepFrames, int fps, out string message)
        {
            message = "";

            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            {
                message = "出力ファイルが見つかりません";
                return false;
            }

            if (keepFrames <= 0)
            {
                message = "切り詰め先のフレームが不正です";
                return false;
            }

            string extension = Path.GetExtension(inputPath);
            if (!TrimmableExtensions.Contains(extension))
            {
                message = $"この形式は切り詰めに対応していません ({extension})";
                return false;
            }

            if (fps <= 0) fps = 30;

            if (!TryFindFfmpeg(out string ffmpeg))
            {
                message = "ffmpeg が見つかりませんでした";
                return false;
            }

            string? directory = Path.GetDirectoryName(inputPath);
            if (string.IsNullOrEmpty(directory)) directory = ".";

            string tempPath = Path.Combine(
                directory,
                Path.GetFileNameWithoutExtension(inputPath) + ".trimming" + extension);

            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
            }

            double seconds = (double)keepFrames / fps;
            string secondsText = seconds.ToString("0.######", CultureInfo.InvariantCulture);

            // 音声・映像のみをコピーする (字幕などのデータストリームは除外)
            string arguments =
                $"-y -hide_banner -nostdin -loglevel error -i \"{inputPath}\" " +
                $"-t {secondsText} -map 0:v? -map 0:a? -c copy -avoid_negative_ts make_zero \"{tempPath}\"";

            string errorText;
            try
            {
                errorText = RunFfmpeg(ffmpeg, arguments);
            }
            catch (Exception ex)
            {
                message = $"ffmpeg の実行に失敗しました: {ex.Message}";
                CleanUp(tempPath);
                return false;
            }

            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length <= 0)
            {
                message = string.IsNullOrWhiteSpace(errorText)
                    ? "ffmpeg が出力ファイルを生成しませんでした"
                    : errorText.Trim();
                CleanUp(tempPath);
                return false;
            }

            try
            {
                ReplaceFile(tempPath, inputPath);
            }
            catch (Exception ex)
            {
                message = $"切り詰め結果の置き換えに失敗しました: {ex.Message} (一時ファイル: {tempPath})";
                return false;
            }

            message = $"{keepFrames}f ({secondsText}s) に切り詰めました";
            return true;
        }

        private static string RunFfmpeg(string ffmpeg, string arguments)
        {
            var startInfo = new ProcessStartInfo(ffmpeg, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) throw new InvalidOperationException("ffmpeg を起動できませんでした");

            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(FfmpegTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                throw new TimeoutException("ffmpeg がタイムアウトしました");
            }

            string error = errorTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error) ? $"終了コード {process.ExitCode}" : error.Trim());
            }

            return error;
        }

        private static void ReplaceFile(string tempPath, string destinationPath)
        {
            try
            {
                File.Replace(tempPath, destinationPath, null);
                return;
            }
            catch
            {
                // ネットワークドライブなど File.Replace が使えない場合はコピーで上書きする
            }

            File.Copy(tempPath, destinationPath, overwrite: true);
            CleanUp(tempPath);
        }

        private static void CleanUp(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }

        private static bool TryFindFfmpeg(out string path)
        {
            foreach (string candidate in EnumerateCandidates())
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        path = candidate;
                        return true;
                    }
                }
                catch
                {
                }
            }

            // PATH 上の ffmpeg
            string? pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVariable))
            {
                foreach (string directory in pathVariable.Split(
                    Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string candidate = Path.Combine(directory.Trim('"'), "ffmpeg.exe");
                        if (File.Exists(candidate))
                        {
                            path = candidate;
                            return true;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            path = "";
            return false;
        }

        private static IEnumerable<string> EnumerateCandidates()
        {
            var roots = new List<string>();

            void AddRoot(string? root)
            {
                if (string.IsNullOrWhiteSpace(root)) return;
                try
                {
                    string full = Path.GetFullPath(root);
                    if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
                }
                catch
                {
                }
            }

            AddRoot(AppContext.BaseDirectory);
            AddRoot(AppDomain.CurrentDomain.BaseDirectory);
            AddRoot(Path.GetDirectoryName(Environment.ProcessPath));
            AddRoot(Path.GetDirectoryName(typeof(VideoTrimmer).Assembly.Location));

            foreach (string root in roots)
            {
                yield return Path.Combine(root, "Resources", "bin", "x64", "ffmpeg", "ffmpeg.exe");
                yield return Path.Combine(root, "Resources", "bin", "x86", "ffmpeg", "ffmpeg.exe");
                yield return Path.Combine(root, "ffmpeg", "bin", "ffmpeg.exe");
                yield return Path.Combine(root, "ffmpeg.exe");

                // プラグインフォルダから見た YMM4 本体のフォルダ
                string? parent = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(parent))
                {
                    yield return Path.Combine(parent, "Resources", "bin", "x64", "ffmpeg", "ffmpeg.exe");
                    yield return Path.Combine(parent, "Resources", "bin", "x86", "ffmpeg", "ffmpeg.exe");
                }
            }
        }
    }
}
