using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace EncodePreviewPlugin
{
    public static class PluginVersion
    {
        public static readonly Version Current = new(1, 0, 3);
    }

    public readonly record struct UpdateCheckResult(bool HasUpdate, Version? LatestVersion, string? HtmlUrl);

    public static class UpdateChecker
    {
        private const string ReleasesApiUrl =
            "https://api.github.com/repos/yukkurinadi/YMM4EncodePreviewPlugin/releases";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"EncodePreviewPlugin/{PluginVersion.Current}");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        public static async Task<UpdateCheckResult> CheckAsync()
        {
            try
            {
                using var response = await Http.GetAsync(ReleasesApiUrl).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return default;
                }

                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var jsonDoc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

                foreach (var release in jsonDoc.RootElement.EnumerateArray())
                {
                    bool isDraft = release.TryGetProperty("draft", out var draftEl) && draftEl.GetBoolean();
                    bool isPrerelease = release.TryGetProperty("prerelease", out var preEl) && preEl.GetBoolean();
                    if (isDraft || isPrerelease) continue;

                    var tag = release.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
                    if (string.IsNullOrWhiteSpace(tag) || !Version.TryParse(tag, out var latestVersion))
                    {
                        break;
                    }

                    if (latestVersion > PluginVersion.Current)
                    {
                        string? htmlUrl = release.TryGetProperty("html_url", out var urlEl)
                            ? urlEl.GetString()
                            : null;
                        return new UpdateCheckResult(true, latestVersion, htmlUrl);
                    }

                    break;
                }
            }
            catch
            {
            }

            return default;
        }
    }
}
