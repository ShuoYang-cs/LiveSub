using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveSub.App.Localization;
using LiveSub.Core;
using LiveSub.Core.Chat;
using LiveSub.Core.Speech;
using LiveSub.Core.Subtitles;
using LiveSub.App.Translation;

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

/// <summary>外观：跟随系统（默认）、浅色、深色。字幕、AI 助手和各个窗口一起变（与 macOS 的 AppearanceMode 相同）。</summary>
public enum AppearanceMode { System, Light, Dark }

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
    /// <summary>新用户默认“两者”（2026-10-22 用户决定，与 macOS 相同）；用过的用户的设置文件里有这一项，保留自己的选择。</summary>
    public AudioSourceMode AudioSource { get; set; } = AudioSourceMode.Both;

    /// <summary>
    /// 字幕翻译使用的模型。新用户默认 Google 翻译（不需要配置，与 macOS 相同）；用过的用户保留自己的选择：
    /// 之后版本的设置文件里都有这一项，Windows 0.1 的设置由 <see cref="ModelMigration"/> 迁移过来。
    /// </summary>
    public ModelChoice TranslationModel { get; set; } = ModelCatalog.DefaultTranslation;
    /// <summary>AI 助手和笔记使用的模型（必须是大模型，在 AI 助手面板的标题栏里选）。</summary>
    public ModelChoice AssistantModel { get; set; } = ModelCatalog.DefaultAssistant;
    /// <summary>模型服务：默认列出的云端服务和用户添加的服务。密钥在 <see cref="SecretStore"/> 里。</summary>
    public List<ModelEndpoint> Endpoints { get; set; } = [];
    /// <summary>
    /// 选 Claude 时翻译、AI 助手各用哪个型号（在“模型服务”里改；模型列表里 Claude 只有一项，与 macOS 相同）。
    /// 旧的设置文件里没有这两项（读进来是空的）：以前直接选过某个 Claude 型号的接着用它，否则翻译用 Haiku、AI 助手用 Sonnet。
    /// </summary>
    public string ClaudeTranslationModel { get; set; } = "";
    public string ClaudeAssistantModel { get; set; } = "";
    /// <summary>用户删掉过的默认服务（不再自动补上）。</summary>
    public List<string> DismissedBuiltIns { get; set; } = [];
    public string ClaudePath { get; set; } = "";
    /// <summary>
    /// 点过“开始”：第一次启动时开始按钮下面多一行提示，之后不再显示（与 macOS 相同）。
    /// 空表示设置文件里没有这一项：设置文件已经存在的老用户记为 true，全新安装记为 false（见 Load）。
    /// </summary>
    public bool? HasStartedOnce { get; set; }
    /// <summary>自动检查更新（默认开）。</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>用户选了“跳过此版本”的版本号。</summary>
    public string SkippedVersion { get; set; } = "";
    public DateTime? LastUpdateSuccess { get; set; }
    public DateTime? LastUpdateAttempt { get; set; }
    /// <summary>已经替用户加进模型服务的本机服务地址（Ollama、LM Studio）：用户删掉后不再自动加回来。</summary>
    public List<string> OfferedLocalServices { get; set; } = [];
    /// <summary>本次启动时检测到的：本机装了 Claude Code、正在运行的本机服务地址。不保存。</summary>
    [JsonIgnore] public bool DetectedClaude { get; set; }
    [JsonIgnore] public HashSet<string> RunningLocalServices { get; } = [];

    /// <summary>菜单里的名字：本机检测到可用、又不是当前选中的，后面标“已检测到”。</summary>
    public string MenuTitle(ModelChoice choice, ModelChoice selected)
    {
        var name = ServiceName(choice);
        if (choice == selected) return name;
        bool detected = choice.Kind switch
        {
            ModelKind.ClaudeCode => DetectedClaude,
            ModelKind.Endpoint => Endpoint(choice.EndpointId) is { } e && RunningLocalServices.Contains(e.BaseUrl),
            _ => false,
        };
        return detected ? Strings.Format(Strings.Menu_Detected, name) : name;
    }

    /// <summary>
    /// 检测到正在运行、还没加过的本机服务（Ollama、LM Studio）：加进模型服务，放在“本地模型”里。
    /// 用户删掉后不再加回来；用户自己已经加过同一地址的也不加。返回有没有加。
    /// </summary>
    public bool AddDetectedLocalService(string name, string baseUrl, List<string> models)
    {
        if (models.Count == 0 || OfferedLocalServices.Contains(baseUrl) || Endpoints.Any(e => e.BaseUrl == baseUrl)) return false;
        OfferedLocalServices.Add(baseUrl);
        Endpoints.Add(new ModelEndpoint { Name = name, Format = ChatApiFormat.OpenAI, BaseUrl = baseUrl, Models = models });
        return true;
    }

    public string WhisperModel { get; set; } = Core.Speech.WhisperModel.Default.Id;
    public bool UseGpu { get; set; } = true;
    public bool LivePreview { get; set; } = true;
    /// <summary>过滤周围的中文交谈（默认关闭）。只在中文界面里出现，见 <see cref="FilterChineseActive"/>。</summary>
    public bool FilterChinese { get; set; }
    /// <summary>模型下载地址（为空时用 https://huggingface.co）。</summary>
    public string ModelMirror { get; set; } = "";

    /// <summary>识别困难（口音重、语速快、噪音大）时让大模型按发音和上下文纠正识别错误。默认关闭。</summary>
    public bool AutoCorrect { get; set; }
    /// <summary>纠正改过原文时，在记录里同时保留识别器原来给出的文字。</summary>
    public bool KeepOriginalText { get; set; }
    /// <summary>开着自动纠正又保留识别原文时，“仅译文”不能选（看不到原文就没有意义；与 macOS 的 translationOnlyLocked 相同）。</summary>
    [JsonIgnore] public bool TranslationOnlyLocked => AutoCorrect && KeepOriginalText;
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
    /// <summary>外观：字幕、AI 助手和设置等窗口一起变。旧的设置文件里没有这一项，读进来是“跟随系统”。</summary>
    public AppearanceMode Appearance { get; set; } = AppearanceMode.System;
    /// <summary>新用户的字号：译文 18、原文 11（与 macOS 相同）；用过的用户保留自己的字号。</summary>
    public double TranslationFontSize { get; set; } = 18;
    public double SourceFontSize { get; set; } = 11;
    public double BackgroundOpacity { get; set; } = 0.62;
    public int VisibleLines { get; set; } = 2;
    /// <summary>新用户的字幕大小：面积是屏幕的 1/16，宽高比 3:1（<see cref="OverlayLayout.DefaultSize"/>）。</summary>
    public double OverlayWidth { get; set; } = DefaultOverlaySize.Width;
    /// <summary>大于 0 = 用户拖上下边框定的高度（新用户、恢复默认、双击上下边框时是默认大小的高度）；0 = 旧版设置里按句数（<see cref="VisibleLines"/>，设置里已经没有这一项）算的高度。两种情况字幕都在框里滚动，窗口不随内容变。</summary>
    public double OverlayHeight { get; set; } = DefaultOverlaySize.Height;
    /// <summary>字幕左边与底边的位置（与设备无关的像素）；null 表示放在屏幕右上角。</summary>
    public double? OverlayLeft { get; set; }
    public double? OverlayBottom { get; set; }
    public bool ClickThrough { get; set; }
    /// <summary>录音：字幕运行期间把听到的声音存进记录文件夹（快捷按钮里的录音按钮）。默认关闭。</summary>
    public bool RecordAudio { get; set; }
    public HotKeyChoice HotKey { get; set; } = HotKeyChoice.WinAlt;

    public string TranscriptFolder { get; set; } = DefaultTranscriptFolder;

    private static string DefaultTranscriptFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LiveSub");

    /// <summary>字幕窗口的默认大小：按主屏幕（与设备无关的像素）算，面积是屏幕的 1/16、宽高比 3:1。</summary>
    public static (double Width, double Height) DefaultOverlaySize =>
        OverlayLayout.DefaultSize(System.Windows.SystemParameters.PrimaryScreenWidth, System.Windows.SystemParameters.PrimaryScreenHeight);

    /// <summary>“恢复默认”：字幕回到默认大小、移回右上角，AI 助手回到字幕旁边（与 macOS 的 resetOverlaySize 相同）。</summary>
    public void ResetOverlay()
    {
        (OverlayWidth, OverlayHeight) = DefaultOverlaySize;
        OverlayLeft = null;
        OverlayBottom = null;
        AssistantLeft = null;
        AssistantTop = null;
    }

    /// <summary>
    /// 这一项是给听外语课的中国学生用的：只在中文界面里显示和生效，其他语言的界面里看不到，也不过滤
    /// （2026-10-23 用户决定：Windows 保留开关，非中文用户看到“过滤中文”会莫名其妙）。讲话语言是中文时没有意义。
    /// </summary>
    [JsonIgnore] public bool FilterChineseOffered => Localization.Strings.IsChinese && !SourceLanguage.StartsWith("zh", StringComparison.Ordinal);
    [JsonIgnore] public bool FilterChineseActive => FilterChinese && FilterChineseOffered;

    [JsonIgnore] public LanguageOption Source => LanguageOption.Source(SourceLanguage);
    [JsonIgnore] public LanguageOption Target => LanguageOption.Target(TargetLanguage);

    public ModelEndpoint? Endpoint(string? id) => id is null ? null : Endpoints.FirstOrDefault(e => e.Id == id);

    /// <summary>Google 翻译、混元翻译在界面上的名字。</summary>
    private static BasicEngineNames EngineNames => new(Strings.Engine_Google, Strings.Engine_LocalModel);

    /// <summary>完整名称“服务 · 模型”（“Claude · Haiku”“通义千问 · qwen-flash”）：日志、笔记进度、状态里用。</summary>
    public string DisplayName(ModelChoice choice) => ModelCatalog.DisplayName(choice, Endpoints, EngineNames);

    /// <summary>模型列表和菜单上显示的名字：只写服务名（“Claude”“通义千问”），具体模型在“模型服务”里看和改。</summary>
    public string ServiceName(ModelChoice choice) => ModelCatalog.ServiceName(choice, Endpoints, EngineNames);

    /// <summary>这个用途选的 Claude 型号。</summary>
    public string ClaudeModel(ModelPurpose purpose) => purpose == ModelPurpose.Translation ? ClaudeTranslationModel : ClaudeAssistantModel;

    /// <summary>可选的模型：翻译有基础、云端模型、本地模型三组，AI 助手只有后两组；每家服务只列一项。</summary>
    public List<ModelGroup> ModelGroups(ModelPurpose purpose) => ModelCatalog.Groups(purpose, Endpoints, ClaudeModel(purpose));

    /// <summary>删除一个模型服务：正在用它的翻译换回 Google 翻译，AI 助手换回 Claude。密钥由调用方删除。</summary>
    public void RemoveEndpoint(ModelEndpoint endpoint)
    {
        Endpoints.Remove(endpoint);
        // 删掉的默认服务不再自动加回来（想用时可以从“添加模型服务”里再加）。
        if (ModelCatalog.IsBuiltIn(endpoint.Id) && !DismissedBuiltIns.Contains(endpoint.Id)) DismissedBuiltIns.Add(endpoint.Id);
        TranslationModel = ModelCatalog.WithoutEndpoint(TranslationModel, endpoint.Id, ModelCatalog.DefaultTranslation);
        AssistantModel = ModelCatalog.WithoutEndpoint(AssistantModel, endpoint.Id, ModelChoice.ClaudeCode(ClaudeAssistantModel));
    }

    /// <summary>
    /// 正在用的模型跟着“模型服务”里的选择走：Claude 换了型号、某家换了“翻译用”“AI 助手用”、或删掉了正在用的模型时，
    /// 翻译和 AI 助手跟着换。macOS 在设置的 didSet 里做，这里在每次保存前做（<see cref="SettingsService.Commit"/>）。
    /// </summary>
    internal void SyncModels()
    {
        TranslationModel = Synced(TranslationModel, ModelPurpose.Translation);
        AssistantModel = Synced(AssistantModel, ModelPurpose.Assistant);
        // 自动纠正只对云端模型有效：换到别的模型时关掉，开关也置灰（macOS 在 translationModel 的 didSet 里做；灰着却是开的容易误解）。
        if (AutoCorrect && !TranslationModel.IsLanguageModel) AutoCorrect = false;
        // 保留识别原文时“仅译文”不能选。
        if (TranslationOnlyLocked && DisplayMode == DisplayMode.TranslationOnly) DisplayMode = DisplayMode.Bilingual;
    }

    private ModelChoice Synced(ModelChoice choice, ModelPurpose purpose)
    {
        if (choice.Kind == ModelKind.ClaudeCode)
        {
            var model = ClaudeModel(purpose);
            return choice.Model == model ? choice : ModelChoice.ClaudeCode(model);
        }
        return ModelCatalog.Synced(choice, purpose, Endpoints) is { } synced && synced != choice ? synced : choice;
    }

    /// <summary>
    /// 读进来的设置可能缺字段或被手改坏：补上默认值；补上默认列出的云端服务；
    /// 旧设置里直接选过的具体模型（Claude 的型号、某家服务的某个模型）记成 Claude 的型号、这家的“翻译用”“AI 助手用”，接着用原来的模型。
    /// 可以重复调用。
    /// </summary>
    internal void Normalize()
    {
        TranslationModel = Canonical(TranslationModel) ?? ModelCatalog.DefaultTranslation;
        // AI 助手、笔记只能用大模型。
        AssistantModel = Canonical(AssistantModel) is { IsLanguageModel: true } assistant ? assistant : ModelCatalog.DefaultAssistant;
        Endpoints = (Endpoints ?? []).Where(e => e is not null).ToList();
        foreach (var e in Endpoints)
        {
            if (string.IsNullOrWhiteSpace(e.Id)) e.Id = Guid.NewGuid().ToString("N");
            e.Name ??= "";
            e.BaseUrl ??= "";
            e.Models = (e.Models ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
            if (string.IsNullOrWhiteSpace(e.TranslationModel)) e.TranslationModel = null;
            if (string.IsNullOrWhiteSpace(e.AssistantModel)) e.AssistantModel = null;
        }
        DismissedBuiltIns = (DismissedBuiltIns ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        Endpoints = ModelCatalog.WithBuiltInServices(Endpoints, DismissedBuiltIns, p => Strings.IsChinese ? p.ChineseName : p.Name);
        ClaudeTranslationModel = ModelCatalog.AdoptClaudeModel(ClaudeTranslationModel, TranslationModel, ModelPurpose.Translation);
        ClaudeAssistantModel = ModelCatalog.AdoptClaudeModel(ClaudeAssistantModel, AssistantModel, ModelPurpose.Assistant);
        ModelCatalog.Adopt(TranslationModel, ModelPurpose.Translation, Endpoints);
        ModelCatalog.Adopt(AssistantModel, ModelPurpose.Assistant, Endpoints);
        SyncModels();
        ClaudePath ??= "";
        OfferedLocalServices = (OfferedLocalServices ?? []).Distinct().ToList();
        ModelMirror ??= "";
        if (!Enum.IsDefined(Appearance)) Appearance = AppearanceMode.System;
        if (OverlayWidth <= 0 || double.IsNaN(OverlayWidth)) OverlayWidth = DefaultOverlaySize.Width;
        if (string.IsNullOrWhiteSpace(TranscriptFolder)) TranscriptFolder = DefaultTranscriptFolder;
    }

    /// <summary>有效的选择（Google 翻译、混元翻译换成共用的实例）；缺字段或被手改坏时为 null。</summary>
    private static ModelChoice? Canonical(ModelChoice? choice) => choice is null ? null : choice.Kind switch
    {
        ModelKind.Google => ModelChoice.Google,
        ModelKind.LocalModel => ModelChoice.LocalModel,
        ModelKind.ClaudeCode when ModelCatalog.ClaudeCodeModels.Contains(choice.Model ?? "") => choice,
        ModelKind.Endpoint when !string.IsNullOrWhiteSpace(choice.Model) && !string.IsNullOrEmpty(choice.EndpointId) => choice,
        _ => null,
    };
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
    /// 选了还没填 API 密钥的服务（比如默认列出的 OpenAI）、或还没下载的混元翻译：由 AppShell 打开“模型服务”，定位到这家
    /// （参数是服务的 Id，混元翻译是 <see cref="LocalModelFiles.ServiceId"/>；与 macOS 的 onModelNeedsSetup 相同）。
    /// </summary>
    public event Action<string>? ModelNeedsSetup;

    private readonly SecretStore secrets;
    /// <summary>上次保存时的翻译、AI 助手模型：换了服务才检查要不要先去“模型服务”里设置。</summary>
    private ModelChoice lastTranslation;
    private ModelChoice lastAssistant;

    /// <summary>
    /// 旧版设置还没迁移成（密钥文件读不出来，或者搬密钥、保存失败）时，启动时读到的设置文件：之后保存设置时不写新的模型字段，
    /// 换回其中的旧字段，文件仍是旧格式，下次启动再迁移（<see cref="LegacyModelSettings.KeepUnmigrated"/>）。迁移成了或不用迁移时为 null。
    /// </summary>
    private readonly string? unmigratedJson;

    /// <param name="secrets">迁移旧版设置时，旧的 Anthropic API 密钥要搬到新的“Anthropic”服务名下。</param>
    public SettingsService(SecretStore secrets)
    {
        this.secrets = secrets;
        Current = Load(secrets, out unmigratedJson);
        lastTranslation = Current.TranslationModel;
        lastAssistant = Current.AssistantModel;
    }

    private static AppSettings Load(SecretStore secrets, out string? unmigrated)
    {
        unmigrated = null;
        string? json = null;
        AppSettings settings;
        try
        {
            if (File.Exists(FilePath)) json = File.ReadAllText(FilePath, Encoding.UTF8);
            settings = json is null ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("settings: load failed, using defaults", e);
            json = null;
            settings = new AppSettings();
        }
        settings.HasStartedOnce ??= json is not null;
        if (json is not null && LegacyModelSettings.TryRead(json) is { } legacy)
        {
            if (!Migrate(settings, legacy, secrets)) unmigrated = json;
            return settings;
        }
        var before = json is null ? null : JsonSerializer.Serialize(settings, Json);
        settings.Normalize();
        // 补上了默认列出的服务、记下了从旧设置迁移来的具体模型：马上存起来（之后换到别家服务，回来时还是这个模型）。
        if (before is not null && JsonSerializer.Serialize(settings, Json) != before && Save(settings, null))
        {
            AppLog.Info("settings: updated model services");
        }
        return settings;
    }

    /// <summary>
    /// Windows 0.1 的“翻译服务 + 模型”迁移到“翻译模型 + 模型服务”，迁移后马上保存。
    /// 密钥先复制（确实写进文件）、设置保存之后再删旧的（<see cref="ModelMigration.Commit"/>）：
    /// 中途失败时下次启动会再迁移一遍（结果相同），密钥不会丢。
    /// 密钥文件读不出来时不迁移：不知道有没有旧的 Anthropic 密钥，也写不了密钥。这次运行仍在内存里套用迁移结果
    /// （当作没有旧的 Anthropic 密钥），否则旧设置里没有的字段（例如 Claude 型号）是空的，翻译用不了。
    /// </summary>
    /// <returns>迁移完成（新格式的设置已经保存）。</returns>
    private static bool Migrate(AppSettings settings, LegacyModelSettings legacy, SecretStore secrets)
    {
        if (secrets.Unreadable)
        {
            AppLog.Error("settings: migration postponed, secrets.bin could not be read");
            var interim = ModelMigration.Migrate(legacy, hasAnthropicKey: false);
            settings.TranslationModel = interim.Translation;
            settings.Endpoints = interim.Endpoints;
            settings.Normalize();
            return false;
        }
        var result = ModelMigration.Migrate(legacy, secrets.Has(SecretStore.LegacyAnthropicKey));
        settings.TranslationModel = result.Translation;
        settings.Endpoints = result.Endpoints;
        // 补上默认列出的服务，原来选的 Claude 型号、服务里的模型记成“翻译用”。
        settings.Normalize();
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

    /// <summary>保存并通知。保存前让正在用的模型跟着“模型服务”里的选择走（<see cref="AppSettings.SyncModels"/>）。</summary>
    public void Commit(SettingsChange change)
    {
        Current.SyncModels();
        Save(Current, unmigratedJson);
        var setup = ServiceNeedingSetup();
        Changed?.Invoke(change);
        if (setup is not null) ModelNeedsSetup?.Invoke(setup);
    }

    /// <summary>请求打开“模型服务”并定位到这家（例如选了混元翻译、开始字幕时发现还没下载）。</summary>
    public void RequestSetup(string serviceId) => ModelNeedsSetup?.Invoke(serviceId);

    /// <summary>
    /// 这个模型所在的服务需要 API 密钥但还没填：返回服务的 Id。
    /// </summary>
    public string? EndpointMissingKey(ModelChoice choice) =>
        choice.Kind == ModelKind.Endpoint && Current.Endpoint(choice.EndpointId) is { IsLocal: false } endpoint
        && !secrets.Has(SecretStore.EndpointKey(endpoint.Id))
            ? endpoint.Id
            : null;

    /// <summary>
    /// 刚换到的翻译或 AI 助手模型还不能用：所在服务没填密钥，或者混元翻译还没下载。同一家服务只是换了模型时不提示。
    /// </summary>
    private string? ServiceNeedingSetup()
    {
        var s = Current;
        string? id = null;
        if (Switched(lastTranslation, s.TranslationModel))
        {
            id = s.TranslationModel.Kind == ModelKind.LocalModel && !LocalModelFiles.IsInstalled
                ? LocalModelFiles.ServiceId
                : EndpointMissingKey(s.TranslationModel);
        }
        if (id is null && Switched(lastAssistant, s.AssistantModel)) id = EndpointMissingKey(s.AssistantModel);
        lastTranslation = s.TranslationModel;
        lastAssistant = s.AssistantModel;
        return id;
    }

    private static bool Switched(ModelChoice old, ModelChoice now) => old != now && !ModelCatalog.OnlyModelChanged(old, now);

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
    /// <summary>翻译模型、模型服务、语言、自动纠正、混元翻译下载好了：需要换翻译会话。</summary>
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
