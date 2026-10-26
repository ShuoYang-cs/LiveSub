using System.Net.Http;
using LiveSub.Core.Chat;
using LiveSub.App.Translation;

namespace LiveSub.App.Services;

/// <summary>
/// 启动时看一眼本机有什么能用的模型：Claude Code 命令行，正在运行的 Ollama、LM Studio（与 macOS 的 LocalServiceDetector 相同）。
/// 结果用来在模型菜单里标“已检测到”，并把正在运行的本机服务加进模型服务。
/// </summary>
public static class LocalServiceDetector
{
    private static readonly (string Name, string BaseUrl)[] Services =
    [
        ("Ollama", "http://localhost:11434/v1"),
        ("LM Studio", "http://localhost:1234/v1"),
    ];

    /// <summary>返回有没有往模型服务里加新的（要保存）。</summary>
    public static async Task<bool> RunAsync(AppSettings settings)
    {
        var claudePath = settings.ClaudePath;
        settings.DetectedClaude = await Task.Run(() => ClaudeLocator.Locate(claudePath) is not null);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
        bool added = false;
        foreach (var (name, baseUrl) in Services)
        {
            var models = await FetchModels(http, baseUrl);
            if (models.Count == 0) continue;
            settings.RunningLocalServices.Add(baseUrl);
            added |= settings.AddDetectedLocalService(name, baseUrl, models);
        }
        return added;
    }

    /// <summary>服务没在运行时连接马上被拒绝，不会等。</summary>
    private static async Task<List<string>> FetchModels(HttpClient http, string baseUrl)
    {
        try
        {
            using var response = await http.GetAsync(baseUrl + "/models");
            if (!response.IsSuccessStatusCode) return [];
            return ChatEndpoints.ParseModelIds(await response.Content.ReadAsStringAsync());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return [];
        }
    }
}
