using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveSub.App.Localization;
using LiveSub.Core;
using LiveSub.Core.Chat;
using LiveSub.Core.Speech;
using LiveSub.Core.Subtitles;

namespace LiveSub.App.Services;

/// <summary>音源：麦克风、系统正在播放的声音，或两者同时（各自识别，字幕前标出来自哪个音源）。</summary>
public enum AudioSourceMode { Microphone, System, Both }

public static class AudioSourceModes
{
    public static readonly IReadOnlyList<AudioSourceMode> All = [AudioSourceMode.Microphone, AudioSourceMode.System, AudioSourceMode.Both];

    /// <summary>完整的名字（托盘菜单、状态）：麦克风、系统音频、麦克风和系统音频。</summary>
    public static string Title(this AudioSourceMode mode) => mode switch
    {
        AudioSourceMode.Microphone => Strings.Source_Microphone,
        AudioSourceMode.System => Strings.Source_System,
        _ => Strings.Source_Both,
    };

    /// <summary>简短的名字（设置里的分段按钮，与 macOS 相同）：麦克风、系统音频、两者。</summary>
    public static string ShortTitle(this AudioSourceMode mode) =>
        mode == AudioSourceMode.Both ? Strings.Source_BothShort : mode.Title();
}

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

/// <summary>所有用户设置，保存在 %APPDATA%\LiveSub\settings.json。API 密钥不在这里。</summary>
public sealed class AppSettings
{
    public string SourceLanguage { get; set; } = "en-US";
    public string TargetLanguage { get; set; } = "zh-Hans";
    public AudioSourceMode AudioSource { get; set; } = AudioSourceMode.System;
    /// <summary>麦克风降噪：用 Windows 的通话语音处理（降噪、回声消除）打开麦克风。默认关闭。</summary>
    public bool MicNoiseReduction { get; set; }

    /// <summary>字幕翻译使用的模型。</summary>
    public ModelChoice TranslationModel { get; set; } = ModelCatalog.DefaultTranslation;
    /// <summary>AI 助手和笔记使用的模型（在 AI 助手面板的标题栏里选）。</summary>
    public ModelChoice AssistantModel { get; set; } = ModelCatalog.DefaultAssistant;
    /// <summary>用户添加的模型服务。密钥在 <see cref="SecretStore"/> 里。</summary>
    public List<ModelEndpoint> Endpoints { get; set; } = [];
    public string ClaudePath { get; set; } = "";

    public string WhisperModel { get; set; } = Core.Speech.WhisperModel.Default.Id;
    public bool UseGpu { get; set; } = true;
    public bool LivePreview { get; set; } = true;
    public bool FilterChinese { get; set; }
    /// <summary>模型下载地址（为空时用 https://huggingface.co）。</summary>
    public string ModelMirror { get; set; } = "";

    /// <summary>识别困难（口音重、语速快、噪音大）时让大模型按发音和上下文纠正识别错误。默认关闭。</summary>
    public bool AutoCorrect { get; set; }
    /// <summary>纠正改过原文时，在记录里同时保留识别器原来给出的文字。</summary>
    public bool KeepOriginalText { get; set; }
    public bool ShowStatusBar { get; set; } = true;

    /// <summary>
    /// AI 助手面板的大小和左上角位置（与设备无关的像素）。位置只在用户拖动标题栏或边框后保存；
    /// 为 null（或那里已不在任何屏幕上）时，每次打开都放在字幕旁边。
    /// </summary>
    public double AssistantWidth { get; set; } = 380;
    public double AssistantHeight { get; set; } = 420;
    public double? AssistantLeft { get; set; }
    public double? AssistantTop { get; set; }
    /// <summary>AI 助手面板是否打开。不保存：每次启动时都是关着的（与 macOS 版相同）。</summary>
    [JsonIgnore] public bool AssistantVisible { get; set; }

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

    public string TranscriptFolder { get; set; } = DefaultTranscriptFolder;

    private static string DefaultTranscriptFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LiveSub");

    [JsonIgnore] public LanguageOption Source => LanguageOption.Source(SourceLanguage);
    [JsonIgnore] public LanguageOption Target => LanguageOption.Target(TargetLanguage);

    public ModelEndpoint? Endpoint(string? id) => id is null ? null : Endpoints.FirstOrDefault(e => e.Id == id);

    /// <summary>“Claude Haiku”，或“服务名 · 模型名”。</summary>
    public string DisplayName(ModelChoice choice) => ModelCatalog.DisplayName(choice, Endpoints);

    /// <summary>可选的模型：云端模型、本地模型两组。</summary>
    public List<ModelGroup> ModelGroups() => ModelCatalog.Groups(Endpoints);

    /// <summary>删除一个模型服务：正在用它的地方换回默认模型。密钥由调用方删除。</summary>
    public void RemoveEndpoint(ModelEndpoint endpoint)
    {
        Endpoints.Remove(endpoint);
        TranslationModel = ModelCatalog.WithoutEndpoint(TranslationModel, endpoint.Id, ModelCatalog.DefaultTranslation);
        AssistantModel = ModelCatalog.WithoutEndpoint(AssistantModel, endpoint.Id, ModelCatalog.DefaultAssistant);
    }

    /// <summary>读进来的设置可能缺字段或被手改坏：补上默认值。</summary>
    internal void Normalize()
    {
        TranslationModel = Valid(TranslationModel) ? TranslationModel : ModelCatalog.DefaultTranslation;
        AssistantModel = Valid(AssistantModel) ? AssistantModel : ModelCatalog.DefaultAssistant;
        Endpoints = (Endpoints ?? []).Where(e => e is not null).ToList();
        foreach (var e in Endpoints)
        {
            if (string.IsNullOrWhiteSpace(e.Id)) e.Id = Guid.NewGuid().ToString("N");
            e.Name ??= "";
            e.BaseUrl ??= "";
            e.Models = (e.Models ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        }
        ClaudePath ??= "";
        ModelMirror ??= "";
        if (string.IsNullOrWhiteSpace(TranscriptFolder)) TranscriptFolder = DefaultTranscriptFolder;
    }

    private static bool Valid(ModelChoice? choice) => choice is not null && !string.IsNullOrWhiteSpace(choice.Model)
        && (choice.Kind == ModelKind.Endpoint ? !string.IsNullOrEmpty(choice.EndpointId) : ModelCatalog.ClaudeCodeModels.Contains(choice.Model));
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

    /// <summary>
    /// 旧版设置还没迁移成（密钥文件读不出来，或者搬密钥、保存失败）时，启动时读到的设置文件：之后保存设置时不写新的模型字段，
    /// 换回其中的旧字段，文件仍是旧格式，下次启动再迁移（<see cref="LegacyModelSettings.KeepUnmigrated"/>）。迁移成了或不用迁移时为 null。
    /// </summary>
    private readonly string? unmigratedJson;

    /// <param name="secrets">迁移旧版设置时，旧的 Anthropic API 密钥要搬到新的“Anthropic”服务名下。</param>
    public SettingsService(SecretStore secrets)
    {
        Current = Load(secrets, out unmigratedJson);
    }

    private static AppSettings Load(SecretStore secrets, out string? unmigrated)
    {
        unmigrated = null;
        string json;
        AppSettings settings;
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            json = File.ReadAllText(FilePath, Encoding.UTF8);
            settings = JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("settings: load failed, using defaults", e);
            return new AppSettings();
        }
        settings.Normalize();
        if (LegacyModelSettings.TryRead(json) is { } legacy)
        {
            if (!Migrate(settings, legacy, secrets)) unmigrated = json;
        }
        return settings;
    }

    /// <summary>
    /// Windows 0.1 的“翻译服务 + 模型”迁移到“翻译模型 + 模型服务”，迁移后马上保存。
    /// 密钥先复制（确实写进文件）、设置保存之后再删旧的（<see cref="ModelMigration.Commit"/>）：
    /// 中途失败时下次启动会再迁移一遍（结果相同），密钥不会丢。
    /// 密钥文件读不出来时不迁移：不知道有没有旧的 Anthropic 密钥，也写不了密钥。
    /// </summary>
    /// <returns>迁移完成（新格式的设置已经保存）。</returns>
    private static bool Migrate(AppSettings settings, LegacyModelSettings legacy, SecretStore secrets)
    {
        if (secrets.Unreadable)
        {
            AppLog.Error("settings: migration postponed, secrets.bin could not be read");
            return false;
        }
        var result = ModelMigration.Migrate(legacy, secrets.Has(SecretStore.LegacyAnthropicKey));
        settings.TranslationModel = result.Translation;
        settings.Endpoints = result.Endpoints;
        string? anthropicKey = result.AnthropicEndpointId is { } id ? SecretStore.EndpointKey(id) : null;
        if (!ModelMigration.Commit(anthropicKey, SecretStore.LegacyAnthropicKey, secrets, () => Save(settings, null)))
        {
            // 本次运行照样用迁移后的设置；保存设置时保留旧字段，下次启动再迁移。
            AppLog.Error("settings: migration not finished, will retry on next launch");
            return false;
        }
        AppLog.Info($"settings: migrated engine={legacy.Engine} to {settings.DisplayName(settings.TranslationModel)}, {settings.Endpoints.Count} model services");
        return true;
    }

    /// <summary>保存并通知。</summary>
    public void Commit(SettingsChange change)
    {
        Save(Current, unmigratedJson);
        Changed?.Invoke(change);
    }

    /// <param name="unmigrated">旧版设置还没迁移成时启动时读到的设置文件：保存后仍是旧格式（见 <see cref="unmigratedJson"/>）。</param>
    private static bool Save(AppSettings settings, string? unmigrated)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var json = JsonSerializer.Serialize(settings, Json);
            if (unmigrated is not null) json = LegacyModelSettings.KeepUnmigrated(json, unmigrated);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("settings: save failed", e);
            return false;
        }
    }
}

/// <summary>设置改动的种类。</summary>
[Flags]
public enum SettingsChange
{
    None = 0,
    /// <summary>字幕外观（字号、显示内容、位置等），只需重画。</summary>
    Appearance = 1,
    /// <summary>翻译模型、模型服务、语言、自动纠正：需要换翻译会话。</summary>
    Translation = 2,
    /// <summary>音源、麦克风降噪、讲话语言、识别模型：需要重启识别。</summary>
    Recognition = 4,
    All = Appearance | Translation | Recognition,
}

/// <summary>
/// API 密钥：用 Windows 数据保护（DPAPI，仅当前用户可解密）加密后保存在 %APPDATA%\LiveSub\secrets.bin，
/// 不写入设置文件，也不写进日志。
/// </summary>
public sealed class SecretStore : ISecretTable
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LiveSub.Windows.Secrets.v1");
    private static string FilePath => Path.Combine(SettingsService.Folder, "secrets.bin");

    /// <summary>Windows 0.1 的 Anthropic API 密钥；启动时迁移到“Anthropic”服务名下（见 <see cref="SettingsService"/>）。</summary>
    public const string LegacyAnthropicKey = "anthropic";

    /// <summary>模型服务的密钥。沿用 0.1 版 OpenAI 兼容服务的名字，迁移过来的服务 Id 不变，密钥不用搬。</summary>
    public static string EndpointKey(string endpointId) => "provider:" + endpointId;

    private readonly Dictionary<string, string> secrets;
    private bool refusalLogged;

    /// <summary>
    /// secrets.bin 存在却读不出来（解密失败、文件损坏或被别的程序占着）。这次运行不再写它，
    /// 免得用几乎是空的表盖掉原来的密钥；也不迁移旧版设置。重新启动、文件能读出来时一切照旧。
    /// </summary>
    public bool Unreadable { get; }

    public SecretStore()
    {
        secrets = Load(out bool unreadable);
        Unreadable = unreadable;
    }

    public string? Get(string name) => secrets.TryGetValue(name, out var v) && v.Length > 0 ? v : null;

    public bool Has(string name) => Get(name) is not null;

    /// <summary>改一个密钥（空白表示删除）并写进 secrets.bin。</summary>
    /// <returns>确实写进了文件。文件读不出来或写入失败时为 false，本次运行仍然用改过的值。</returns>
    public bool Set(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) secrets.Remove(name);
        else secrets[name] = value.Trim();
        if (Unreadable)
        {
            if (!refusalLogged) AppLog.Error("secrets: not saved, secrets.bin could not be read at launch");
            refusalLogged = true;
            return false;
        }
        return Save();
    }

    /// <param name="unreadable">文件存在却读不出来。</param>
    private static Dictionary<string, string> Load(out bool unreadable)
    {
        unreadable = false;
        try
        {
            if (!File.Exists(FilePath)) return [];
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("secrets: load failed, secrets.bin will not be written this run", e);
            unreadable = true;
            return [];
        }
    }

    private bool Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.Folder);
            var data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(secrets), Entropy, DataProtectionScope.CurrentUser);
            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("secrets: save failed", e);
            return false;
        }
    }
}
