using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;

namespace NakTaWallpaper;

// 시작 시 GitHub Releases API로 최신 버전 조회 → 더 높으면 사용자에게 알림 → 설치 실행
public static class UpdaterService
{
    private const string ApiUrl =
        "https://api.github.com/repos/codingnakta/NakTaWallpaper/releases/latest";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NakTaWallpaper-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static async Task CheckForUpdateAsync()
    {
        try
        {
            var json = await Http.GetStringAsync(ApiUrl);
            var release = JsonSerializer.Deserialize<ReleaseInfo>(json);
            if (release?.TagName is null || release.Assets is null) return;

            var latest = ParseVersion(release.TagName);
            var current = typeof(UpdaterService).Assembly.GetName().Version
                          ?? new Version(0, 0, 0, 0);

            if (latest <= current) return;

            // .exe 자산 찾기 (인스톨러)
            AssetInfo? installer = null;
            foreach (var a in release.Assets)
            {
                if (!string.IsNullOrEmpty(a.Name) &&
                    a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    installer = a;
                    break;
                }
            }
            if (installer?.BrowserDownloadUrl is null) return;

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var msg = $"새 버전이 있습니다: {release.TagName}\n현재 버전: v{current.ToString(3)}\n\n" +
                          "지금 다운로드해서 업데이트할까요?\n(설치 중 앱이 종료됩니다.)";
                var result = System.Windows.MessageBox.Show(msg, "NakTaWallpaper 업데이트",
                    MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                    _ = DownloadAndRunAsync(installer.BrowserDownloadUrl, installer.Name ?? "setup.exe");
            });
        }
        catch
        {
            // 네트워크 오류 등은 조용히 무시
        }
    }

    private static async Task DownloadAndRunAsync(string url, string fileName)
    {
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(),
                "NakTaWallpaper-Update-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + fileName);

            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            using (var src = await resp.Content.ReadAsStreamAsync())
            using (var dst = File.Create(tempPath))
            {
                await src.CopyToAsync(dst);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = tempPath,
                UseShellExecute = true,
                Verb = "runas" // 인스톨러는 관리자 권한 필요
            });

            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"업데이트 다운로드 실패:\n{ex.Message}",
                "NakTaWallpaper 업데이트 오류",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static Version ParseVersion(string tag)
    {
        var clean = tag.TrimStart('v', 'V');
        return Version.TryParse(clean, out var v) ? v : new Version(0, 0, 0, 0);
    }

    private class ReleaseInfo
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public AssetInfo[]? Assets { get; set; }
    }

    private class AssetInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
