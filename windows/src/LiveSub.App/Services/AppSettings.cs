using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveSub.Core;
using LiveSub.Core.Chat;
using LiveSub.Core.Speech;
using LiveSub.Core.Subtitles;

namespace LiveSub.App.Services;

public enum AudioSourceMode { Microphone, System }

public enum EngineKind { ClaudeCode, Anthropic, OpenAICompatible }

/// <summary>显示/隐藏字幕的全局快捷键。默认 Win+Alt+L：部分键盘布局（例如波兰语）里 Ctrl+Alt 等同于 AltGr，会占用输入字符。</summary>
public enum HotKeyChoice { WinAlt, CtrlAlt, None }

public static class HotKeys
{
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_WIN = 0x0008;
    public const uint VirtualKeyL = 0x4C;

    public static readonly IReadOnlyList<HotKeyChoice> All = [HotKeyChoice.WinAlt, HotKeyChoice.CtrlAlt, HotKeyChoice.None];

    /// <summary>显示用的文字；没有快捷键时为 null。</summary>
    public static string? Text(HotKeyChoice choice) => choice switch
    {
        HotKeyChoice.WinAlt => "Win+Alt+L",
        HotKeyChoice.CtrlAlt => "Ctrl+Alt+L",
        _ => null,
    };

    public static uint Modifiers(HotKeyChoice choice) => choice switch
    {
        HotKeyChoice.WinAlt => MOD_WIN | MOD_ALT,
        HotKeyChoice.CtrlAlt => MOD_CONTROL | MOD_ALT,
        _ => 0,
    };
}

/// <summary>一个 OpenAI 兼容服务。密钥不在这里，在 <see cref="SecretStore"/> 里按 Id 保存。</summary>
public sealed class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>云服务需要密钥；本机或局域网里的服务（Ollama、LM Studio）不需要。</summary>
    [JsonIgnore] public bool RequiresKey => !OpenAICompatibleTransport.IsLocalServer(BaseUrl);
    /// <summary>上次“获取模型”得到的列表。</summary>
    public List<string> Models { get; set; } = [];
}

/// <summary>所有用户设置，保存在 %APPDATA%\LiveSub\settings.json。API 密钥不在这里。</summary>
public sealed class AppSettings
{
    public string SourceLanguage { get; set; } = "en-US";
    public string TargetLanguage { get; set; } = "zh-Hans";
    public AudioSourceMode AudioSource { get; set; } = AudioSourceMode.System;

    public EngineKind Engine { get; set; } = EngineKind.ClaudeCode;
    /// <summary>Claude Code 命令行的模型：haiku / sonnet / opus。</summary>
    public string ClaudeCodeModel { get; set; } = "haiku";
    public string ClaudePath { get; set; } = "";
    public string AnthropicModel { get; set; } = AnthropicModelPolicy.DefaultModel;
    public List<string> AnthropicModels { get; set; } = [];
    public List<ProviderConfig> Providers { get; set; } = [];
    public string? ActiveProviderId { get; set; }

    public string WhisperModel { get; set; } = Core.Speech.WhisperModel.Default.Id;
    public bool UseGpu { get; set; } = true;
    public bool LivePreview { get; set; } = true;
    public bool FilterChinese { get; set; }
    /// <summary>模型下载地址（为空时用 https://huggingface.co）。</summary>
    public string ModelMirror { get; set; } = "";

    public bool KeepOriginalText { get; set; }
    public bool ShowStatusBar { get; set; } = true;

    public DisplayMode DisplayMode { get; set; } = DisplayMode.Bilingual;
    public double TranslationFontSize { get; set; } = 26;
    public double SourceFontSize { get; set; } = 16;
    public double BackgroundOpacity { get; set; } = 0.62;
    public int VisibleLines { get; set; } = 2;
    public double OverlayWidth { get; set; } = 900;
    /// <summary>0 = 高度按“显示句数”算；大于 0 = 用户拖上下边框定的高度。两种情况字幕都在框里滚动，窗口不随内容变。</summary>
    public double OverlayHeight { get; set; }
    /// <summary>字幕左边与底边的位置（与设备无关的像素）；null 表示放在屏幕底部居中。</summary>
    public double? OverlayLeft { get; set; }
    public double? OverlayBottom { get; set; }
    public bool ClickThrough { get; set; }
    public HotKeyChoice HotKey { get; set; } = HotKeyChoice.WinAlt;

    public string TranscriptFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LiveSub");

    [JsonIgnore] public LanguageOption Source => LanguageOption.Source(SourceLanguage);
    [JsonIgnore] public LanguageOption Target => LanguageOption.Target(TargetLanguage);
    [JsonIgnore] public ProviderConfig? ActiveProvider => Providers.FirstOrDefault(p => p.Id == ActiveProviderId);
}

/// <summary>读写设置文件；改动后调用 <see cref="Commit"/> 保存并通知。</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveSub");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public AppSettings Current { get; private set; }

    /// <summary>设置改变（已保存）。参数说明改了什么，便于只做必要的更新。</summary>
    public event Action<SettingsChange>? Changed;

    public SettingsService()
    {
        Current = Load();
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath, Encoding.UTF8), Json) ?? new AppSettings();
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("settings: load failed, using defaults", e);
        }
        return new AppSettings();
    }

    /// <summary>保存并通知。</summary>
    public void Commit(SettingsChange change)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json), Encoding.UTF8);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("settings: save failed", e);
        }
        Changed?.Invoke(change);
    }
}

/// <summary>设置改动的种类。</summary>
[Flags]
public enum SettingsChange
{
    None = 0,
    /// <summary>字幕外观（字号、显示内容、位置等），只需重画。</summary>
    Appearance = 1,
    /// <summary>翻译服务或模型、语言：需要换翻译会话。</summary>
    Translation = 2,
    /// <summary>音源、讲话语言、识别模型：需要重启识别。</summary>
    Recognition = 4,
    All = Appearance | Translation | Recognition,
}

/// <summary>
/// API 密钥：用 Windows 数据保护（DPAPI，仅当前用户可解密）加密后保存在 %APPDATA%\LiveSub\secrets.bin，
/// 不写入设置文件，也不写进日志。
/// </summary>
public sealed class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LiveSub.Windows.Secrets.v1");
    private static string FilePath => Path.Combine(SettingsService.Folder, "secrets.bin");

    public const string AnthropicKey = "anthropic";
    public static string ProviderKey(string providerId) => "provider:" + providerId;

    private readonly Dictionary<string, string> secrets;

    public SecretStore()
    {
        secrets = Load();
    }

    public string? Get(string name) => secrets.TryGetValue(name, out var v) && v.Length > 0 ? v : null;

    public bool Has(string name) => Get(name) is not null;

    public void Set(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) secrets.Remove(name);
        else secrets[name] = value.Trim();
        Save();
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("secrets: load failed", e);
            return [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.Folder);
            var data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(secrets), Entropy, DataProtectionScope.CurrentUser);
            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("secrets: save failed", e);
        }
    }
}
