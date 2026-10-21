using System.Globalization;
using System.Resources;

namespace LiveSub.App.Localization;

/// <summary>
/// 界面文字。英文在 Strings.resx，简体中文在 Strings.zh-Hans.resx；按系统界面语言选择（中文系统一律用简体中文）。
/// 新增文字时三个文件一起改。
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Resources = new("LiveSub.App.Localization.Strings", typeof(Strings).Assembly);

    /// <summary>界面使用的语言。</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.InvariantCulture;

    public static bool IsChinese => Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>按系统界面语言选择：中文（任何地区）用简体中文，其他用英文。</summary>
    public static void Initialize(CultureInfo uiCulture)
    {
        Culture = uiCulture.TwoLetterISOLanguageName == "zh" ? new CultureInfo("zh-Hans") : CultureInfo.InvariantCulture;
    }

    public static string Get(string key) => Resources.GetString(key, Culture) ?? key;

    public static string Format(string template, params object?[] args) => string.Format(CultureInfo.CurrentCulture, template, args);

    public static string AppName => Get(nameof(AppName));
    public static string Menu_Start => Get(nameof(Menu_Start));
    public static string Menu_Stop => Get(nameof(Menu_Stop));
    public static string Menu_AudioSource => Get(nameof(Menu_AudioSource));
    public static string Source_Microphone => Get(nameof(Source_Microphone));
    public static string Source_System => Get(nameof(Source_System));
    public static string Menu_Model => Get(nameof(Menu_Model));
    public static string Menu_Display => Get(nameof(Menu_Display));
    public static string Display_Bilingual => Get(nameof(Display_Bilingual));
    public static string Display_TranslationOnly => Get(nameof(Display_TranslationOnly));
    public static string Display_SourceOnly => Get(nameof(Display_SourceOnly));
    public static string Menu_ShowOverlay => Get(nameof(Menu_ShowOverlay));
    public static string Menu_HideOverlay => Get(nameof(Menu_HideOverlay));
    public static string Menu_Lock => Get(nameof(Menu_Lock));
    public static string Menu_ResetPosition => Get(nameof(Menu_ResetPosition));
    public static string Menu_Export => Get(nameof(Menu_Export));
    public static string Menu_History => Get(nameof(Menu_History));
    public static string Menu_NewSession => Get(nameof(Menu_NewSession));
    public static string Menu_OpenFolder => Get(nameof(Menu_OpenFolder));
    public static string Menu_Settings => Get(nameof(Menu_Settings));
    public static string Menu_Quit => Get(nameof(Menu_Quit));
    public static string Group_ClaudeCode => Get(nameof(Group_ClaudeCode));
    public static string Group_Anthropic => Get(nameof(Group_Anthropic));
    public static string Model_NotConfigured => Get(nameof(Model_NotConfigured));
    public static string Model_NoKey => Get(nameof(Model_NoKey));
    public static string Status_NotStarted => Get(nameof(Status_NotStarted));
    public static string Status_Preparing => Get(nameof(Status_Preparing));
    public static string Status_DownloadingModel => Get(nameof(Status_DownloadingModel));
    public static string Status_LoadingModel => Get(nameof(Status_LoadingModel));
    public static string Status_Listening => Get(nameof(Status_Listening));
    public static string Status_Stopped => Get(nameof(Status_Stopped));
    public static string Status_NewSession => Get(nameof(Status_NewSession));
    public static string Status_Filtered => Get(nameof(Status_Filtered));
    public static string Status_Exported => Get(nameof(Status_Exported));
    public static string Status_StartFailed => Get(nameof(Status_StartFailed));
    public static string Overlay_Listening => Get(nameof(Overlay_Listening));
    public static string Overlay_Ready => Get(nameof(Overlay_Ready));
    public static string Overlay_TranslationFailed => Get(nameof(Overlay_TranslationFailed));
    public static string Overlay_NoInput => Get(nameof(Overlay_NoInput));
    public static string Overlay_Receiving => Get(nameof(Overlay_Receiving));
    public static string Overlay_Quiet => Get(nameof(Overlay_Quiet));
    public static string Overlay_RecognitionHard => Get(nameof(Overlay_RecognitionHard));
    public static string Overlay_RecognitionClear => Get(nameof(Overlay_RecognitionClear));
    public static string Tip_RecognitionHard => Get(nameof(Tip_RecognitionHard));
    public static string Tip_RecognitionClear => Get(nameof(Tip_RecognitionClear));
    public static string Overlay_Backlog => Get(nameof(Overlay_Backlog));
    public static string Overlay_BackToLive => Get(nameof(Overlay_BackToLive));
    public static string Tip_ModelMenu => Get(nameof(Tip_ModelMenu));
    public static string Tool_Start => Get(nameof(Tool_Start));
    public static string Tool_Stop => Get(nameof(Tool_Stop));
    public static string Tool_CycleDisplay => Get(nameof(Tool_CycleDisplay));
    public static string Tool_Lock => Get(nameof(Tool_Lock));
    public static string Tool_History => Get(nameof(Tool_History));
    public static string Tool_Settings => Get(nameof(Tool_Settings));
    public static string Tool_Hide => Get(nameof(Tool_Hide));
    public static string Tool_HideNoKey => Get(nameof(Tool_HideNoKey));
    public static string Tip_Tray => Get(nameof(Tip_Tray));
    public static string Error_ClaudeNotFound => Get(nameof(Error_ClaudeNotFound));
    public static string Error_NotLoggedIn => Get(nameof(Error_NotLoggedIn));
    public static string Error_Unauthorized => Get(nameof(Error_Unauthorized));
    public static string Error_MissingKey => Get(nameof(Error_MissingKey));
    public static string Error_MissingModel => Get(nameof(Error_MissingModel));
    public static string Error_Timeout => Get(nameof(Error_Timeout));
    public static string Error_ProcessExited => Get(nameof(Error_ProcessExited));
    public static string Error_NoOutput => Get(nameof(Error_NoOutput));
    public static string Error_Http => Get(nameof(Error_Http));
    public static string Error_Network => Get(nameof(Error_Network));
    public static string Error_Refused => Get(nameof(Error_Refused));
    public static string Error_Other => Get(nameof(Error_Other));
    public static string Error_Microphone => Get(nameof(Error_Microphone));
    public static string Error_SystemAudio => Get(nameof(Error_SystemAudio));
    public static string Error_AudioStopped => Get(nameof(Error_AudioStopped));
    public static string Error_ModelDownload => Get(nameof(Error_ModelDownload));
    public static string Error_Recognizer => Get(nameof(Error_Recognizer));
    public static string Error_ExportFailed => Get(nameof(Error_ExportFailed));
    public static string Error_HotKey => Get(nameof(Error_HotKey));
    public static string Error_Busy => Get(nameof(Error_Busy));
    public static string Info_DeviceChanged => Get(nameof(Info_DeviceChanged));
    public static string Settings_Title => Get(nameof(Settings_Title));
    public static string Section_Translation => Get(nameof(Section_Translation));
    public static string Label_SourceLanguage => Get(nameof(Label_SourceLanguage));
    public static string Label_TargetLanguage => Get(nameof(Label_TargetLanguage));
    public static string Label_AudioSource => Get(nameof(Label_AudioSource));
    public static string Label_Engine => Get(nameof(Label_Engine));
    public static string Label_Model => Get(nameof(Label_Model));
    public static string Label_ClaudePath => Get(nameof(Label_ClaudePath));
    public static string Tip_ClaudePath => Get(nameof(Tip_ClaudePath));
    public static string Status_ClaudeFound => Get(nameof(Status_ClaudeFound));
    public static string Status_ClaudeMissing => Get(nameof(Status_ClaudeMissing));
    public static string Button_Browse => Get(nameof(Button_Browse));
    public static string Label_ApiKey => Get(nameof(Label_ApiKey));
    public static string Tip_ApiKey => Get(nameof(Tip_ApiKey));
    public static string Status_KeySaved => Get(nameof(Status_KeySaved));
    public static string Button_FetchModels => Get(nameof(Button_FetchModels));
    public static string Section_Providers => Get(nameof(Section_Providers));
    public static string Button_AddProvider => Get(nameof(Button_AddProvider));
    public static string Button_RemoveProvider => Get(nameof(Button_RemoveProvider));
    public static string Preset_Custom => Get(nameof(Preset_Custom));
    public static string Label_ProviderName => Get(nameof(Label_ProviderName));
    public static string Label_BaseUrl => Get(nameof(Label_BaseUrl));
    public static string Tip_BaseUrl => Get(nameof(Tip_BaseUrl));
    public static string Button_Test => Get(nameof(Button_Test));
    public static string Status_Testing => Get(nameof(Status_Testing));
    public static string Status_TestOk => Get(nameof(Status_TestOk));
    public static string Status_FetchedModels => Get(nameof(Status_FetchedModels));
    public static string Status_Fetching => Get(nameof(Status_Fetching));
    public static string Section_Recognition => Get(nameof(Section_Recognition));
    public static string Label_WhisperModel => Get(nameof(Label_WhisperModel));
    public static string Tip_WhisperModel => Get(nameof(Tip_WhisperModel));
    public static string Model_Downloaded => Get(nameof(Model_Downloaded));
    public static string Model_NotDownloaded => Get(nameof(Model_NotDownloaded));
    public static string Button_Download => Get(nameof(Button_Download));
    public static string Label_UseGpu => Get(nameof(Label_UseGpu));
    public static string Tip_UseGpu => Get(nameof(Tip_UseGpu));
    public static string Label_LivePreview => Get(nameof(Label_LivePreview));
    public static string Tip_LivePreview => Get(nameof(Tip_LivePreview));
    public static string Label_FilterChinese => Get(nameof(Label_FilterChinese));
    public static string Tip_FilterChinese => Get(nameof(Tip_FilterChinese));
    public static string Label_ModelMirror => Get(nameof(Label_ModelMirror));
    public static string Tip_ModelMirror => Get(nameof(Tip_ModelMirror));
    public static string Section_Overlay => Get(nameof(Section_Overlay));
    public static string Label_DisplayMode => Get(nameof(Label_DisplayMode));
    public static string Label_TranslationFontSize => Get(nameof(Label_TranslationFontSize));
    public static string Label_SourceFontSize => Get(nameof(Label_SourceFontSize));
    public static string Label_Opacity => Get(nameof(Label_Opacity));
    public static string Label_VisibleLines => Get(nameof(Label_VisibleLines));
    public static string Label_ShowStatusBar => Get(nameof(Label_ShowStatusBar));
    public static string Tip_ShowStatusBar => Get(nameof(Tip_ShowStatusBar));
    public static string Label_Lock => Get(nameof(Label_Lock));
    public static string Label_HotKey => Get(nameof(Label_HotKey));
    public static string Tip_HotKey => Get(nameof(Tip_HotKey));
    public static string HotKey_None => Get(nameof(HotKey_None));
    public static string Tip_Lock => Get(nameof(Tip_Lock));
    public static string Button_ResetOverlay => Get(nameof(Button_ResetOverlay));
    public static string Section_Records => Get(nameof(Section_Records));
    public static string Label_TranscriptFolder => Get(nameof(Label_TranscriptFolder));
    public static string Button_Open => Get(nameof(Button_Open));
    public static string Label_KeepOriginal => Get(nameof(Label_KeepOriginal));
    public static string Tip_KeepOriginal => Get(nameof(Tip_KeepOriginal));
    public static string History_Title => Get(nameof(History_Title));
    public static string History_Empty => Get(nameof(History_Empty));
    public static string Button_OpenTranscript => Get(nameof(Button_OpenTranscript));
    public static string Export_FilterSrt => Get(nameof(Export_FilterSrt));
    public static string Export_FilterVtt => Get(nameof(Export_FilterVtt));
    public static string Export_DefaultName => Get(nameof(Export_DefaultName));
    public static string Transcript_Name => Get(nameof(Transcript_Name));
}
