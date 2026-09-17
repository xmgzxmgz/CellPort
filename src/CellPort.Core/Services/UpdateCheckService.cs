using System.Net.Http;
using System.Text.Json;

namespace CellPort.Core.Services;

/// <summary>一次更新检查的结果。HasUpdate=false 且 LatestTag 非空表示已是最新。</summary>
/// <param name="HasUpdate">远端版本是否比当前新。</param>
/// <param name="CurrentVersion">当前程序版本。</param>
/// <param name="LatestTag">远端最新 Release 的 tag（如 v1.0.1）。</param>
/// <param name="ReleaseUrl">Release 页面地址。</param>
public sealed record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    string? LatestTag,
    string? ReleaseUrl);

/// <summary>
/// 通过 GitHub Releases 检查新版本。只读匿名 API；网络失败静默返回 null，
/// 由调用方决定提示方式 —— 检查更新永远不能阻塞或打断主流程。
/// </summary>
public static class UpdateCheckService
{
    public const string RepoOwner = "xmgzxmgz";
    public const string RepoName = "CellPort";
    public const string ReleasesPage = $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>解析 v1.2.3 / 1.2.3 / 1.2.3-beta 形式的 tag；失败返回 null。</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var s = tag.Trim().TrimStart('v', 'V');
        // 1.2.3-beta 中的预发布后缀 Version.TryParse 不接受，先截掉
        var dash = s.IndexOf('-');
        if (dash > 0)
        {
            s = s[..dash];
        }

        return Version.TryParse(s, out var v) ? v : null;
    }

    /// <summary>查询远端最新版本并与当前版本比较。异常 / 限流 / 不可达时返回 null。</summary>
    public static async Task<UpdateCheckResult?> CheckAsync(
        Version currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CellPort-UpdateCheck");

            using var resp = await http.GetAsync(
                $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest", ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var tag = doc.RootElement.TryGetProperty("tag_name", out var t)
                ? t.GetString() : null;
            var url = doc.RootElement.TryGetProperty("html_url", out var u)
                ? u.GetString() : null;

            var latest = ParseTag(tag);
            if (latest is null)
            {
                return null;
            }

            return new UpdateCheckResult(
                latest > currentVersion,
                currentVersion.ToString(),
                tag,
                url ?? ReleasesPage);
        }
        catch
        {
            return null;
        }
    }
}
