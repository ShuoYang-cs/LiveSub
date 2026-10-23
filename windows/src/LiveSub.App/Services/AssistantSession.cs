using System.Security;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiveSub.App.Localization;
using LiveSub.App.Translation;
using LiveSub.Core;
using LiveSub.Core.Chat;
using LiveSub.Core.Subtitles;

namespace LiveSub.App.Services;

/// <summary>
/// AI 助手对话（与 macOS 版 AssistantSession 相同），用 AI 助手模型的长驻会话。
/// - 用户点“开始”、“开始新的记录”或“新对话”时开始新对话；
/// - 提问时把上次提问以来新增的字幕（和正在说的半句）放在问题前面一起发，模型手上一直有完整的字幕记录；
/// - 会话换新（换了模型、改了模型服务或语言、进程退出）时，自动把本次对话以来的全部字幕重新带上；
/// - 提问可以带图片和文件（粘贴、拖进面板、点回形针选择；与 macOS 相同），读好之后显示在输入框上方，随下一个问题一起发。
/// 带哪些字幕的规则在 <see cref="AssistantConversation"/> 里。只在界面线程上使用。
/// </summary>
public sealed class AssistantSession
{
    /// <summary>输入框上方等着随下一个问题发送的附件。</summary>
    public sealed record PendingAttachment(Guid Id, ChatAttachment Content);

    private readonly SettingsService settingsService;
    private readonly SecretStore secrets;
    private readonly Dispatcher dispatcher;
    /// <summary>由控制器提供：当前的字幕和正在说的半句。</summary>
    private readonly Func<(IReadOnlyList<SubtitleLine> Lines, string Speaking)> transcript;
    private readonly AssistantConversation conversation = new(DateTime.UtcNow);
    private IChatSession? session;
    /// <summary>建会话时的设置（<see cref="SessionKey"/>）。</summary>
    private string? sessionKey;

    /// <summary>问答、输入框或“正在回答”变了。</summary>
    public event Action? Changed;

    public IReadOnlyList<AssistantMessage> Messages => conversation.Messages;
    public bool IsAnswering => conversation.IsAnswering;
    /// <summary>输入框里的文字。</summary>
    public string Draft { get; set; } = "";
    public IReadOnlyList<PendingAttachment> Attachments => attachments;
    /// <summary>还在读取的附件个数：读完之前不能发送。</summary>
    public int LoadingAttachments { get; private set; }
    /// <summary>附件读取失败等提示，显示在输入框上方；下次添加或发送时清掉。</summary>
    public string? AttachmentNotice { get; private set; }
    /// <summary>能不能发送：有文字或附件，附件都读完了，而且不在回答中。</summary>
    public bool CanSend => !IsAnswering && LoadingAttachments == 0 && (Draft.Trim().Length > 0 || attachments.Count > 0);
    /// <summary>快捷问题能不能点（附件读完之前不能）。</summary>
    public bool CanAsk => !IsAnswering && LoadingAttachments == 0;

    private readonly List<PendingAttachment> attachments = [];
    /// <summary>每次新对话加一：新对话之前开始读的附件，读完后不再加进来。</summary>
    private int attachmentGeneration;

    private AppSettings Settings => settingsService.Current;

    public AssistantSession(SettingsService settingsService, SecretStore secrets, Dispatcher dispatcher,
                            Func<(IReadOnlyList<SubtitleLine> Lines, string Speaking)> transcript)
    {
        this.settingsService = settingsService;
        this.secrets = secrets;
        this.dispatcher = dispatcher;
        this.transcript = transcript;
        settingsService.Changed += OnSettingsChanged;
    }

    /// <summary>新对话：清空问答，之后只带上从现在开始的字幕。</summary>
    public void NewConversation()
    {
        CloseSession();
        conversation.Reset(DateTime.UtcNow);
        attachments.Clear();
        AttachmentNotice = null;
        attachmentGeneration++;
        AppLog.Info("assistant: new conversation");
        RaiseChanged();
    }

    /// <summary>提前启动会话（Claude Code 的进程），第一次提问不用等。面板打开时才调用，不用就不占资源。</summary>
    public void WarmUp()
    {
        // 正在回答时不换会话（换了模型也等这一问答完）。
        if (IsAnswering) return;
        try
        {
            MakeSessionIfNeeded();
        }
        catch (ChatProviderException e)
        {
            // 提问时会再试一次，并把原因显示在面板里。
            AppLog.Error("assistant: warm-up failed", e);
        }
    }

    /// <summary>发送输入框里的问题（正在回答时按回车：问题留在输入框里，不清掉）。</summary>
    public void Send()
    {
        if (!CanSend) return;
        var question = Draft.Trim();
        Draft = "";
        Ask(question);
    }

    // MARK: - 附件

    /// <summary>选择的、拖进来的、粘贴的文件（资源管理器里复制的文件）。</summary>
    public void AddFiles(IEnumerable<string> paths) =>
        Load(paths.Select(path => (Func<ChatAttachment>)(() => AttachmentLoader.Load(path))).ToList());

    /// <summary>粘贴的、从网页拖进来的图片。</summary>
    public void AddImage(byte[] data, string name) => Load([() => AttachmentLoader.Image(data, name)]);

    /// <summary>剪贴板里只有系统位图（没有 PNG）的截图：已经冻结，编码在后台做（大截图编码要几百毫秒）。</summary>
    public void AddImage(BitmapSource bitmap, string name) => Load([() => AttachmentLoader.Image(bitmap, name)]);

    public void RemoveAttachment(Guid id)
    {
        attachments.RemoveAll(a => a.Id == id);
        AttachmentNotice = null;
        RaiseChanged();
    }

    /// <summary>在后台读取，读完按顺序加到输入框上方；超过上限的、读不了的给出提示（与 macOS 相同）。</summary>
    private async void Load(List<Func<ChatAttachment>> loaders)
    {
        if (loaders.Count == 0) return;
        AttachmentNotice = null;
        LoadingAttachments += loaders.Count;
        int gen = attachmentGeneration;
        RaiseChanged();
        var results = await Task.Run(() => loaders.Select(loader =>
        {
            try
            {
                return (Content: loader(), Error: (string?)null);
            }
            catch (AttachmentLoader.Failure e)
            {
                return (Content: (ChatAttachment?)null, Error: e.Message);
            }
            catch (Exception e)
            {
                AppLog.Error("assistant: attachment failed", e);
                return (Content: (ChatAttachment?)null, Error: e.Message);
            }
        }).ToList());
        LoadingAttachments -= loaders.Count;
        if (gen != attachmentGeneration)
        {
            RaiseChanged();
            return;
        }
        var notices = new List<string>();
        foreach (var (content, error) in results)
        {
            if (error is not null)
            {
                notices.Add(error);
                continue;
            }
            if (content is null) continue;
            if (attachments.Count >= AttachmentLimits.MaxAttachments)
            {
                notices.Add(Strings.Format(Strings.Assistant_TooManyAttachments, AttachmentLimits.MaxAttachments));
                break;
            }
            attachments.Add(new PendingAttachment(Guid.NewGuid(), content));
        }
        AttachmentNotice = notices.Count == 0 ? null : string.Join("\n", notices.Distinct());
        AppLog.Info($"assistant: {results.Count} attachments loaded, {notices.Count} problems");
        RaiseChanged();
    }

    // MARK: - 提问

    public async void Ask(string question)
    {
        if (!CanAsk) return;
        // 几个大 PDF 加起来可能超过接口一次请求的上限，整个请求被拒：从大到小改成只发文字（与 macOS 相同）。
        var (sent, textOnly) = ChatAttachmentBudget.Fit(attachments.Select(a => a.Content).ToList());
        var labels = attachments.Select(a => a.Content.Label).ToList();
        attachments.Clear();
        AttachmentNotice = textOnly.Count == 0 ? null : Strings.Format(Strings.Assistant_TextOnly, string.Join(Strings.ListSeparator, textOnly));
        // 只放了附件、没写问题：替用户问一句。
        var prompt = question.Length == 0 ? Strings.Assistant_SeeAttachments : question;
        IChatSession current;
        try
        {
            current = MakeSessionIfNeeded();
        }
        catch (ChatProviderException e)
        {
            AppLog.Error("assistant: unavailable", e);
            conversation.AddFailure(question, TranslationEngines.Describe(e), labels);
            RaiseChanged();
            return;
        }
        var (lines, speaking) = transcript();
        if (conversation.Begin(question, lines, speaking, labels, prompt) is not { } pending) return;
        RaiseChanged();
        IReadOnlyList<AssistantFiles.File> files = [];
        try
        {
            // 在后台线程上发：新会话的第一问带着本次对话以来的全部字幕，Claude Code 要等进程读走才写得完，
            // 在界面线程上写会卡住界面。回答的片段送回界面线程；await 之后也回到界面线程。
            var reply = await Task.Run(() => current.RequestAsync(pending.NewSessionMessage, pending.OngoingMessage, partial =>
                dispatcher.BeginInvoke(() =>
                {
                    if (conversation.UpdateAnswer(pending, partial)) RaiseChanged();
                }), CancellationToken.None, sent.Count > 0 ? sent : null));
            if (conversation.Complete(pending, reply)) files = conversation.FilesToSave(pending);
        }
        catch (Exception e)
        {
            // 对话已经清空（“新对话”关掉了会话）时，这一问的结果不再显示。
            if (conversation.Fail(pending, TranslationEngines.Describe(e)))
            {
                AppLog.Error("assistant failed", e);
                // 未登录、密钥无效等重试没用的错误：下次提问按当前设置重新建会话（例如用户刚改好密钥）。
                if (e is ChatProviderException { IsPersistent: true } && ReferenceEquals(session, current)) CloseSession();
            }
        }
        // 回答期间改了模型服务、语言或模型：这一问答完了，关掉按旧设置建的会话，下一问开新会话。
        if (ReferenceEquals(session, current) && SessionOutdated) CloseSession();
        RaiseChanged();
        if (files.Count > 0) await SaveFiles(pending, files);
    }

    /// <summary>
    /// 回答里要保存的文件写进记录文件夹下的“AI 助手”文件夹（模型自己不能写文件），不覆盖已有文件。
    /// 在后台线程上写（记录文件夹可能在网络驱动器上），写完回到界面线程显示结果；其间“新对话”清空了问答时不再显示。
    /// </summary>
    private async Task SaveFiles(AssistantQuestion question, IReadOnlyList<AssistantFiles.File> files)
    {
        var transcriptFolder = Settings.TranscriptFolder;
        var folderName = Strings.Assistant_FolderName;
        var saved = await Task.Run(() => Save(files, transcriptFolder, folderName, DateTime.Now));
        AppLog.Info($"assistant: saved {saved.Count(f => f.Path is not null)}/{files.Count} files");
        if (conversation.FilesSaved(question, saved)) RaiseChanged();
    }

    private static List<AssistantSavedFile> Save(IReadOnlyList<AssistantFiles.File> files, string transcriptFolder, string folderName,
                                                 DateTime now)
    {
        var stamp = TranscriptFormat.FileStamp(now);
        string folder;
        try
        {
            folder = FilesFolder(transcriptFolder, folderName);
        }
        catch (Exception e) when (IsFileError(e))
        {
            AppLog.Error("assistant: cannot create files folder", e);
            return files.Select(f => new AssistantSavedFile(f.Name, Error: e.Message)).ToList();
        }
        var saved = new List<AssistantSavedFile>();
        foreach (var file in files)
        {
            var name = AssistantFiles.UniqueName(AssistantFiles.FileName(file.Name, stamp, folderName),
                n => File.Exists(Path.Combine(folder, n)));
            var path = Path.Combine(folder, name);
            try
            {
                // CreateNew：万一同时出现了同名文件也不覆盖。
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(file.Content + "\n");
                saved.Add(new AssistantSavedFile(name, path));
            }
            catch (Exception e) when (IsFileError(e))
            {
                saved.Add(new AssistantSavedFile(name, Error: e.Message));
            }
        }
        return saved;
    }

    /// <summary>记录文件夹下的“AI 助手”文件夹；记录文件夹写不进去时放到 %LOCALAPPDATA%\LiveSub 里（和字幕记录一样）。</summary>
    private static string FilesFolder(string transcriptFolder, string folderName)
    {
        var preferred = Environment.ExpandEnvironmentVariables(transcriptFolder);
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            try
            {
                return Directory.CreateDirectory(Path.Combine(preferred, folderName)).FullName;
            }
            catch (Exception e) when (IsFileError(e))
            {
                AppLog.Error("assistant: cannot write to the transcript folder", e);
            }
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveSub", folderName);
        return Directory.CreateDirectory(fallback).FullName;
    }

    private static bool IsFileError(Exception e) =>
        e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or SecurityException;

    /// <summary>
    /// 改了翻译设置（模型服务的地址、接口格式、密钥，语言等）：会话是按旧设置建的就关掉，下一问按新设置开新会话
    /// （同一个对话，问答记录不变，全部字幕重新带上）。正在回答时不关，等这一问答完（见 <see cref="Ask"/>）。
    /// </summary>
    private void OnSettingsChanged(SettingsChange change)
    {
        if (!change.HasFlag(SettingsChange.Translation) || IsAnswering || !SessionOutdated) return;
        AppLog.Info("assistant: settings changed, session closed");
        CloseSession();
    }

    /// <summary>有会话，但它是按已经变了的设置建的。</summary>
    private bool SessionOutdated => session is not null && sessionKey != SessionKey();

    /// <summary>会话用到的设置：模型、语言（系统提示词），以及 claude 的路径或模型服务的地址、接口格式和密钥。</summary>
    private string SessionKey()
    {
        var model = Settings.AssistantModel;
        var endpoint = model.Kind == ModelKind.Endpoint ? Settings.Endpoint(model.EndpointId) : null;
        return string.Join('\u0001', model, AssistantPrompt.System(Settings.Source, Settings.Target),
            model.Kind == ModelKind.ClaudeCode ? Settings.ClaudePath : "",
            endpoint?.Format, endpoint?.BaseUrl, endpoint is null ? null : secrets.Get(SecretStore.EndpointKey(endpoint.Id)));
    }

    private IChatSession MakeSessionIfNeeded()
    {
        // 换了模型或改了它用到的设置：开新会话（同一个对话，下一问会把全部字幕重新带上）。
        if (SessionOutdated) CloseSession();
        if (session is not null) return session;
        var key = SessionKey();
        var created = TranslationEngines.Create(Settings.AssistantModel, Settings, secrets,
            AssistantPrompt.System(Settings.Source, Settings.Target), maxTurns: 10_000, timeout: TimeSpan.FromSeconds(180));
        created.WarmUp();
        session = created;
        sessionKey = key;
        AppLog.Info($"assistant: {created.Name}");
        return created;
    }

    private void CloseSession()
    {
        session?.Dispose();
        session = null;
        sessionKey = null;
    }

    /// <summary>退出程序时关闭会话（不留下 claude 进程）。</summary>
    public void Shutdown() => CloseSession();

    private void RaiseChanged() => Changed?.Invoke();
}
