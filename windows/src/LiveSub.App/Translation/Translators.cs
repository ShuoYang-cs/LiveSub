using LiveSub.App.Localization;
using LiveSub.Core;
using LiveSub.Core.Chat;

namespace LiveSub.App.Translation;

/// <summary>
/// 字幕翻译器（与 macOS 的 Translator 相同）：大模型会话（<see cref="ChatTranslator"/>）、Google 翻译、混元翻译都实现它。
/// 大模型会话带着上下文，要按顺序一句一句翻；Google 翻译、混元翻译每句独立翻译，可以同时翻两句（见 SubtitleController.RunWorker）。
/// </summary>
public interface ITranslator : IDisposable
{
    /// <summary>状态里显示的完整名称，例如“Claude · Haiku”“Google 翻译”。</summary>
    string Name { get; }

    /// <summary>提前做好准备（启动进程、建立连接、加载模型），第一句就不用等。</summary>
    void WarmUp();

    /// <param name="context">前面几句的原文和译文。</param>
    /// <param name="onPartial">收到逐步变长的完整译文（不是增量）；不是流式输出的翻译器不调用它。</param>
    Task<string> TranslateAsync(string text, IReadOnlyList<(string Source, string Translation)> context, Action<string>? onPartial,
                                CancellationToken cancellationToken);
}

/// <summary>用大模型会话翻译：新会话时把前几句的原文和译文一起发过去，之后只发这一句（与 macOS 的 ClaudeTranslator.translate 相同）。</summary>
public sealed class ChatTranslator(IChatSession session) : ITranslator
{
    public string Name => session.Name;

    public void WarmUp() => session.WarmUp();

    public Task<string> TranslateAsync(string text, IReadOnlyList<(string Source, string Translation)> context, Action<string>? onPartial,
                                       CancellationToken cancellationToken) =>
        session.RequestAsync(Prompts.TranslationMessage(text, context), text, onPartial, cancellationToken);

    public void Dispose() => session.Dispose();
}

/// <summary>
/// Google 翻译、混元翻译出错。Message 已经是给用户看的说明（“Google 翻译出错：…”）；
/// 这类错误重试有可能成功（限流、断网、服务还在启动），不算设置问题。
/// </summary>
public class TranslatorException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 混元翻译的服务启动不了：没下载、文件不全、启动失败、超时、没有端口（与 macOS 的 localModelUnavailable 相同）。
/// SubtitleController 收到它时这次改用 Google 翻译；服务起来以后单句请求出错（HTTP 500 等）仍是普通的 <see cref="TranslatorException"/>，不换。
/// </summary>
public sealed class LocalModelUnavailableException(string detail, Exception? inner = null)
    : TranslatorException(Strings.Format(Strings.Error_LocalModelStart, detail), inner);
