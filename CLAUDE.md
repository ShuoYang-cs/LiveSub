# LiveSub 开发说明

**新对话先读 [HANDOFF.md](HANDOFF.md)**：未完成的工作、用户已定的决定和偏好。

macOS 14.2+ 应用（没有 Dock 和菜单栏图标，所有操作在悬浮字幕上）：麦克风/系统声音 → 本地语音识别（macOS 26 用 SpeechAnalyzer，更早用 SFSpeechRecognizer）→ 所选模型翻译（新用户默认 Google 翻译、英语 → 简体中文；也支持 Apple 本地翻译、Claude CLI、API 和本地模型）→ 悬浮双语字幕。详见 README.md。2026-10-22 从同学 Haotian Zhu 的 ICT（基于 LiveSub 改写，经他同意）搬回了 Google 翻译、长句换气处提前确认、新的快捷按钮和外观。

## 用户偏好
- 本机 16GB 内存，**不要在本地跑大模型**；重计算交给 Claude（通过本机 `claude` CLI，用户额度很高）或系统自带框架。

## 工具链限制（只有 Command Line Tools，没有 Xcode）
- SwiftUI 宏插件不可用：**不能用 `@State`、`@Entry`、`#Preview`**。视图状态用 `@Observable` 小对象。`@Observable`、`@Bindable` 可用。
- 测试用 Swift Testing，必须走 `scripts/test.sh`（补上宏插件路径）。
- `Package.swift` 通过 `LIVESUB_SDK_VERSION` 写入链接 SDK 版本；去掉会让 Translation 框架报 notInstalled。

## 构建与验证
- 发布 macOS 版：`scripts/package-release.sh` 生成 Apple 芯片和 Intel 通用的 dmg 和 zip（`build-app.sh --universal`；`--dmg-only` 用现成的 build/LiveSub.app 只做 dmg），上传到 GitHub Releases（tag 用 `macos-版本号`）。签名必须用这台电脑登录钥匙串里的“LiveSub Local Signing”：换了证书，用户的系统会把新版当成另一个 App，要重新授权。这个 SwiftPM 版本不管编哪种芯片，结果都放在同一个输出目录（`.build/out/Products/Release`），后编的覆盖先编的。这台电脑没装 Rosetta，Intel 版只能编译，不能在这里运行。
- `swift build`；`scripts/test.sh`；`scripts/build-app.sh [--install]` 生成 .app（用登录钥匙串里的 “LiveSub Local Signing” 证书签名，见 `scripts/make-signing-cert.sh`；不要改回 ad-hoc，否则每次重装都会重新弹麦克风权限）。
- 不用界面的自检：`.build/debug/LiveSub --transcribe-file x.aiff [--guard zh-CN] [--realtime]`、`--translate "..." --engine claude|google|apple [--accent ...]`、`--render-window out.png [--hover] [--hover-tool record] [--idle] [--light|--dark]`（真实窗口截图）、`--render-settings out.png [--height 1100] [--dark]`（设置窗口截图；命令行版设置在 `defaults` 的 `LiveSub` 域。字幕都在 ScrollView 里，`ImageRenderer` 画不出来，所以没有纯 SwiftUI 的截图命令）。
- 测识别时间/延迟要加 `--realtime`：不加时整个文件瞬间喂完，临时结果的时间范围会失真。
- SpeechTranscriber 的临时结果没有逐词时间（整段一个 run），只有确认结果有；哨兵过滤只能靠确认结果。`LIVESUB_DEBUG_GUARD=1` 打印每句等待哨兵的时间。
- 用文件代替真实声音跑整个界面：`LIVESUB_TEST_AUDIO_FILE=... LIVESUB_AUTOSTART=1 .build/debug/LiveSub`。
- 在 Claude Code 的 Bash 里调用 `claude`、语音/翻译框架需要关闭沙箱。
- 从“应用程序”打开的 App 不读 `~/.zshrc`。在终端里用环境变量登录 Claude Code（例如 `~/.zshrc` 里的 `CLAUDE_CODE_OAUTH_TOKEN`）时，App 启动的 `claude` 会显示未登录，所以 `ClaudeCLI.shellEnvironment()` 会跑一次登录交互 shell，把 Claude、Anthropic、AWS、代理相关变量补给 `claude`（`ShellEnvironment`）。模拟 App 的干净环境：`/usr/bin/env -i HOME=$HOME USER=$USER SHELL=/bin/zsh PATH=/usr/bin:/bin .build/debug/LiveSub --translate "Hi." --engine claude`。判断翻译是不是真的走了 Claude，要看日志里有没有 `translate failed`，导出的字幕本身看不出来。
- 旧版识别器（macOS 14.2–15 的路径）在 macOS 26 上用 `LIVESUB_LEGACY_SPEECH=1` 测。它要“语音识别”权限，命令行直接跑会把权限记到终端上，要通过 App 包启动：`open -n --stdout out.txt --stderr out.txt --env LIVESUB_LEGACY_SPEECH=1 build/LiveSub.app --args --transcribe-file x.aiff --realtime`。实测（2026-10-20）：本机识别的确认结果是空的，临时结果没有逐词时间和置信度，说完后还会反复改写标点，所以停顿判断（`PauseDetector`）看声音和字数增长，连续说话时由 `PartialCommitter` 提前确认稳定的句子。

- 运行日志（卡住、没字幕时先看）：`/usr/bin/log show --last 1h --predicate 'subsystem == "com.livesub.app"'`（zsh 里裸写 `log` 是 shell 内置命令，什么也查不到）。主线程卡住超过 5 秒时，`MainThreadWatchdog` 会把调用栈存到 `~/Library/Logs/LiveSub/hang-*.txt`。
- 换翻译器不能留空档：打开“自动纠正口音”时，识别状态每切换一次就要换一个翻译进程，而触发切换的那句马上就要翻译。以前先置空、再异步准备新进程，结果每次切换都有一句“翻译失败”（2026-10-21 实测：失败的句子全部在切换的同一秒，一节课几十句）。现在 `updateQuality` 先同步建好新会话再替换，旧进程 30 秒后才关（`retire`）。字幕记录里只有原文、没有译文的句子就是失败的句子，可以拿来和日志里 `recognition difficult/clear` 的时间对照。
- `LIVESUB_DEBUG_CLAUDE=1`：把发给 Claude 的原文和原始回复打到 stderr（排查译文异常时用），还有每轮的原始 result 事件，里面是 token 用量和折算费用（费用和耗时是这个进程的累计值）。2026-10-22 实测 Haiku 翻 40 句真实字幕（一个进程）：缓存读入约 10.5 万、缓存写入约 4200、输出约 1300 token，折算约 0.0025 美元；讲课每小时约 440–720 句。`LIVESUB_DEBUG_LATENCY=1` 配合 `--export-test` 打印每句各阶段耗时（说完→入队、开始→首字、总计）。
- 延迟实测（2026-10-19）：识别器每句说完后 0.6–1.0 秒确认，Claude Haiku 首字 0.7–0.9 秒，合计约 1.5–1.9 秒。（2026-10-22 起 macOS 26 的临时结果带逗号了，长句提前确认靠它，见下面 `BreathCutter`。）中文哨兵的等待最多 1.5 秒，只在汉字占比 ≥0.3 且这句有低置信度词时才等。`--export-test 音频 out.srt` 跑整条链路并导出字幕。
- `.fastResults`（识别器偏向速度）保留，不要去掉。`LIVESUB_NO_FAST_RESULTS=1` 可以去掉它来对比。2026-10-22 实测：用 `say` 合成 12 句讲课录音，有标准美音、印度口音、中文口音、加噪声几种，`--transcribe-file --realtime`。去掉后词错误率只降 0–4 个百分点（中文口音 13.8%→9.9%，强噪声下反而 14.4%→15.5%）。但说完到确认结果从 0.5–0.9 秒变成 1.7–3.1 秒，第一个灰色临时字幕也从约 0.8 秒变成 2–3 秒才出来。
- 字幕窗口高度只随设置变化（`OverlayView.height`：拖出来的高度；设置里不再有“显示最近 N 句”，2026-10-23 用户决定去掉，只有旧设置里 `overlayHeight` 为 0 的才按存下的 `visibleLines` 算；双击上下边框恢复默认高度），不随内容变。不要改回按内容自动高度：出字幕时窗口会忽高忽低（用户提出过）。
- AI 助手面板不是字幕窗口的子窗口，也不跟着字幕窗口移动；按住标题栏（`WindowDragArea`，`performDrag`）单独拖动，用户拖过的位置存在 `assistantOrigin`。
- 离屏压力测试字幕窗口：`.build/debug/LiveSub --stress-overlay [--streaming]`（报告主线程最长卡顿、窗口高度范围和最后离底部多远）；`--test-watchdog` 验证卡顿采样。离屏窗口没有真实的滚动阶段事件，手动滚动相关的逻辑只能在真实窗口里验证。
- 真实窗口测试的坑（2026-10-22）：
  - 发过带 ⌘ 的键盘事件后，用 `CGEventSource(stateID: .hidSystemState)` 生成的鼠标事件会带上 ⌘，变成 ⌘-点击，点后台窗口时不激活，要给鼠标事件设 `flags = []`。
  - 命令行程序往剪贴板写文件 URL 会静默失败，剪贴板是空的。测粘贴文件时用 Finder：`open -R 文件` 后发 ⌘C。
  - 选文件对话框由系统的另一个进程绘制，模拟输入进“前往文件夹”框不可靠，用鼠标在分栏里点。
  - LiveSub 是菜单栏程序，它在前台时菜单栏仍然显示上一个普通程序的菜单，不能靠菜单栏判断前台是谁。
- 用 CGEvent 模拟鼠标测拖动时：命令行自检路径（`--stress-overlay` 这类）没有跑 `NSApplication` 的事件循环，窗口收不到鼠标事件，要自己 `nextEvent` + `sendEvent`。用户常常同时开着已安装的 App，先用 `CGWindowListCopyWindowInfo` 确认点击位置没有它的窗口，也不要发键盘事件。
- 字幕边框的缩放指针：字幕面板不成为 key 窗口，macOS 默认只让活动程序改指针，所以启动时用私有接口 `SetsCursorInBackground`（`BackgroundCursor.enable()`）。验证办法：用 CGEvent 把鼠标移到边框上，再读 `NSCursor.currentSystem` 的大小（普通箭头 28×40，上下 18×28，左右 24×18，斜角 22×22）；窗口用 `CGWindowListCopyWindowInfo` 按所有者“实时翻译助手”查找。
- 字幕滚动区（`FollowBottom`）：不要在 `onScrollPhaseChange` 的 idle（程序动画结束）里再 `scrollTo`——离底部的距离和系统认为的底部差几个点时会“滚到底 → idle → 再滚”无限循环，主线程卡死（2026-10-20 发生过）。只在用户手动滚动结束时对齐一次。
- 重装 App 时用 `osascript -e 'with timeout of 10 seconds' -e 'quit app "LiveSub"' -e 'end timeout'` 再 `pkill -x LiveSub` 兜底（macOS 没有 `timeout` 命令）。
- 没有菜单栏图标（2026-10-22 跟 ICT 一样去掉）：快捷按钮是开始/停止、录音、锁定、音源、AI 助手、设置、退出（ICT 的“置顶”开关去掉了：字幕一直在 .statusBar 层；“不置顶 + 锁定”时字幕被盖住又点不到，没法叫回来，用户同意去掉，2026-10-22）；收起用 ⌃⌥L，或再打开一次 App（`applicationShouldHandleReopen`）。原来菜单里的导出字幕、开始新的记录、打开记录文件夹挪到字幕记录窗口，“字幕移回原位”并进设置里的“恢复默认”。字幕记录不放快捷按钮（用户决定），在设置 › 记录。
- 长句提前确认（`LiveSubCore/BreathCutter.swift`，接在 `TranscriptionEngine`）：讲者一口气说很长时，从音量找换气的回落（≥0.15 秒、低于说话音量 30%），这段已有 ≥12 个词、≥3 秒时切。不能让识别器 finalize 到中途（ICT 实测之后的识别会丢词），只能从临时文字里确认：等覆盖换气时刻那块（识别器约 0.9 秒一块）的最后一次临时文字，确认到最后一个从句标点为止，标点后最多留 4 个词给下一段（`BreathCutter.clause`）。ICT 原来要求最后一个字就是标点，2026-10-22 实测换气后的一两个词常在同一块里（“…edges, and why”），全被放过，所以改了。`LIVESUB_DEBUG_CUT=1` 打印每次切分，`LIVESUB_DEBUG_VOLATILE=1` 打印每次临时/确认结果的时间范围。用 `say` 合成的 22 kHz 音频测不出来：`FileCapture` 每块 4096 帧就有 0.19 秒，0.25 秒的停顿被拆进两块、音量降不下去，先 `afconvert -d LEI16@48000` 转成 48 kHz。测试里一个 55 个词的长句，前半句从说完到确认由约 7 秒降到不到 2 秒。
- Google 翻译（`LiveSubCore/GoogleTranslate.swift`、`Translation/GoogleTranslator.swift`，来自 ICT）：两个免费网页接口轮换，限流时换另一个；`LIVESUB_DEBUG_GOOGLE_FAIL=gtx|dictionaryExtension|all` 假装接口被限流。Google、Apple 翻译时最多两句并行（`runWorker`）；识别困难且打开了自动纠正时临时换成 Claude Sonnet 会话（`correctionModel`），清楚了换回 `basicTranslator`。
- Claude 在模型列表里只有一项（`modelGroups(for:)` 按用途放 `claudeTranslationModel` 或 `claudeAssistantModel`），型号和 Claude 路径在“模型服务”的 Claude Code 区选；改型号时正在用 Claude 的翻译/助手跟着换（`AppSettings` 的 didSet）。2026-10-22 实测 `--translate --engine claude`：Haiku 每句 0.86–1.16 秒，Sonnet 1.10–3.29 秒（有一轮 Haiku 前三句各 8 秒，像是服务临时变慢）。
- 默认列出的云端服务（`ChatEndpointPreset.builtIn`：OpenAI、通义千问、DeepSeek、Kimi、Grok，2026-10-22 用户要求）：固定 UUID（存钥匙串密钥、判断删没删过），`AppSettings.withBuiltInServices` 每次启动补上缺的；用户删掉的记在 `dismissedBuiltIns`，自己加过同一地址的不补。选了没填密钥的服务（或没下载的混元翻译）时 `AppSettings.onModelNeedsSetup` 让 AppDelegate 打开“模型服务”并滚到它。模型列表每家只列一项 `.endpoint(id, 这家为翻译/AI 助手选的模型)`；在“模型服务”里换了模型，`endpoints` 的 didSet 把正在用这家的 `translationModel`/`assistantModel` 跟着换（只换模型不再弹密钥提示）。模型名 2026-10-22 按各家官方文档选的快、不思考的模型；`ChatAPI.openAILatencySettings` 给 DeepSeek、Kimi（只有 kimi-k2.6 能关）发 `thinking: disabled`、给千问发 `enable_thinking: false`，别的服务不能发（OpenAI 收到不认识的参数返回 400）。Grok 的官方文档现在只写 Responses API，`chat/completions` 能不能用没核实。
- AI 助手面板的按钮说明（`InstantTip`）：鼠标停上去立刻在按钮下方（输入栏的在上方）画说明，不用系统 `.help`（要等一会儿，面板不是活动窗口时常常不出来）。用 `.offset` 定位：`alignmentGuide` 在 overlay 里没生效（2026-10-22 截图确认）。
- 录音记录窗口（`UI/RecordingsView.swift`）：记录文件夹在“文稿”里时，第一次列目录系统会弹“想访问文稿文件夹”的权限窗口；列目录放在后台，否则主线程一直卡到用户回答（2026-10-22 实测）。
- 录音（`Audio/AudioRecorder.swift`）：采集回调里复制 buffer，串行队列上写 AAC m4a；每路音源一个 `RecorderSlot`，运行中途可以开关。文件名由字幕记录的文件名把“字幕记录”换成“录音”。

- 混元翻译（本机，`Translation/LocalTranslator.swift`）：`llama-server` 通过 `/bin/sh` 守护脚本启动（LiveSub 崩溃时 2 秒内跟着退出），监听 127.0.0.1 的随机端口，`-np 2` 对应同时翻两句；没有翻译器在用或闲置 10 分钟就关。日志 `~/Library/Logs/LiveSub/llama-server.log`。运行程序固定用 llama.cpp b11829（`LocalTranslationModel.runtime`），换版本要重新测、更新 SHA-256。提示词用模型官方模板（涉及中文用中文模板，带前两句原文作“背景信息”），没有系统提示词。2026-10-22 实测：M 系列每句 0.1–0.3 秒，内存约 1.4 GB，冷启动 7 秒、再次启动不到 1 秒。测试：`--translate "..." --engine local`。启动失败（`TranslatorError.localModelUnavailable`）时记住一分钟不再重启（进程一启动就退出时退出回调会先清掉 `starting`，所以不管它在不在都记失败），这次运行的基础翻译器换成 Google（`SubtitleController.fallBackFromLocalModel`）；模拟办法：临时把 `llama-server` 换成 `exit 1` 的脚本跑 `--export-test`，测完换回。
- 字幕记录文件按字幕顺序写（`SubtitleController.persistedCount`）：Google、Apple、混元两句同时翻译时后一句常先翻完，前面的都翻完或失败了才写。
- AI 助手附件的总大小（`LiveSubCore/ChatAttachment.swift` 的 `ChatAttachmentBudget`）：Claude 一次请求最多 32 MB，图片和 PDF 按 base64 发送；这一问超了时 PDF 从大到小改成只发文字（提示用户），HTTP 会话的历史超了时从最早的提问开始把附件换成文字。

- 自动纠正（2026-10-26 用户决定）：只在翻译模型是云端模型（`isLanguageModel`）时生效；换到 Google、Apple、混元翻译时 `translationModel` 的 didSet 把 `autoCorrect` 关掉，设置里开关置灰。不再临时换成 Claude Sonnet（以前手动切换模型看不出变化：纠正期间仍是 Claude 在翻）。开着自动纠正又保留识别原文时“仅译文”不能选（系统分段 Picker 不能单独置灰一段，`DisplayModeSegments` 自己画）；打开自动纠正时“仅译文”自动切“双语”。纠正过的句子在悬浮字幕里原文下面用淡色小字显示识别原文。
- 自动判断主题（`SubtitleController.noteFinishedLine/updateTopic`，`Prompts.topicPrompt`，`Translation/TopicNote.swift`）：开着自动纠正且用云端模型时，翻完 4 句后让模型读最近 30 句字幕猜主题和术语，之后每 20 句更新；主题作为 `[Topic]` 块只发给“按发音纠正”的会话（别的提示词里没讲这个块，发过去会被当成要翻译的内容）。用户提出：术语表上课前没法写。2026-10-26 只用 Haiku 单次调用试过（“RJCC list”那几句能推出 adjacency list），没在真实课堂上量过效果。
- 麦克风降噪（系统语音处理）和系统音频采集同时开会冲突：日志里反复 `VoiceProcessor ... failed to process downlink voice proc`、`number of buffers (2) does not match`，字幕完全停止（2026-10-26 用户在“两者”模式开了降噪，13:11 起没字幕）。所以“两者”模式下降噪不生效、设置里置灰。`LiveSub --mic-compare --seconds 40` 用真实麦克风各录一次降噪关和开（要放同一段声音）；文件输入绕过了降噪，测不出来。空房间实测：开降噪后底噪约被放大 30 dB（自动增益）。
- 状态条“收音中/无声音”：音量按分贝显示（-60 dB 为 0，-20 dB 满格），引擎里的音量按每块 ×0.85 慢慢回落（`level = max(rms, level*0.85)`）。以前线性映射，RMS 低于约 0.006（-44 dB）就显示“无声音”，坐第二排听课常常整节都是；状态条每 0.25 秒只看最后一块，会落在字与字的空隙里。
- 识别器输出的“Okay, okay, okay, okay.”“to, to, to, to”这类同一个词连着 3 次以上：`Segmenter.collapseRepeats` 只留前 2 个（多人讨论、Teams 音质差时常见，翻出来是“好，好，好，好”）。听错的词（“Bye, son”“mommy”）靠自动纠正，不靠这个。

## 结构
- `Sources/LiveSubCore`：纯逻辑（分句、stream-json 解析、提示词），改动要配单元测试。
- `Sources/LiveSub/App/SubtitleController.swift`：整条链路与翻译队列（积压合并、Claude 失败自动 Apple 兜底）。
- 系统录音权限被拒绝时，系统不报错，只送来静音，用户以为程序没反应。现在用非公开接口 `TCCAccessPreflight("kTCCServiceAudioCapture")` 查（0 允许、1 拒绝、2 没问过；`SystemAudioCapture.permissionDenied`），拒绝时字幕下方提示并给“打开系统设置”。不要改成“收到静音、同时有别的程序在输出声音”来猜：LiveSub 自己录系统声音建的聚合设备也算“在输出”，会议软件、网页常常开着输出却不出声，都会误报（2026-10-22 试过）。
- 设置窗口按住空白处或文字也能拖动（`WindowBackgroundDrag`，macOS 15 起用 `WindowDragGesture`）。不要改成在 `sendEvent` 里遇到“不是 NSControl 的地方”就 `performDrag`：“管理…”“恢复默认”这些按钮是 SwiftUI 自己画的，不是 NSControl，会被当成空白，按钮就点不了了。`isMovableByWindowBackground`、`WindowDragGesture` 都依赖真实鼠标，程序内部发模拟事件测不出效果（2026-10-22 试过）。辅助功能的 `accessibilityHitTest` 在离屏窗口里只返回整个视图，也分不出控件。带标题栏的窗口放到屏幕外，`orderFront` 时会被系统挪回屏幕上，探测时会在用户屏幕上闪一下。
- 系统对话框（选文件夹、导出字幕）一律用 `FilePanel.present`：字幕和 AI 助手在 `.statusBar` 层，比对话框高；用 `runModal` 时对话框的按钮被面板挡住，面板又拖不开，用户以为程序卡死了（2026-10-21 发生过）。 层级：字幕和 AI 助手 `.statusBar`，设置、模型服务、字幕记录窗口在用 LiveSub 时 `.statusBar + 1`（切到别的程序时回到普通层，`AppDelegate.setWindowLevels`；2026-10-22 用户提出设置被字幕挡住），对话框 `.statusBar + 2`。
- AI 助手存文件：模型没有写文件的工具（Claude Code 以 `--tools ""` 运行，API 和本地模型也没有工具），系统提示让它在回答末尾用 `@@save: 文件名 … @@end` 块写出文件，App 解析（`LiveSubCore/Assistant.swift` 的 `AssistantFiles`）后只存进记录文件夹下的“AI 助手”文件夹，不覆盖已有文件。用户要求面板里不提示这个功能，只写在 README 里。
- AI 助手面板是不激活应用的面板，只有点输入框时才成为 key 窗口。这时要激活本应用（`AssistantPanel.becomeKey`）：不激活的话，原来那个程序不知道自己失去了键盘，它的输入框还在闪光标，两边同时闪（2026-10-22 用户提出）。点按钮、拖动面板时仍然不激活。判断要不要激活要看 `NSWorkspace` 的前台程序，不能看 `NSApp.isActive`：面板成为 key 窗口时它已经是 true，系统的前台程序却还是原来那个。2026-10-22 真实窗口里测过，从 Finder、VS Code 点进来都会切到 LiveSub，用 `NSApp.activate()` 就够了。
- 菜单里带对勾的选项（模型菜单）用 `Picker` 加 `.pickerStyle(.inline)` 放进 `Menu`，不要用“按钮 + checkmark 图标”：那样菜单宽度不算对勾，带对勾的那项会被截断（2026-10-22 真实窗口里“Claude Sonnet”显示成“Claud…onnet”）。
- AI 助手的附件：`AttachmentLoader` 读文件。图片长边缩到 1568 像素，PNG 太大时改存 JPEG；PDF 交给 Claude（Claude Code、Anthropic 格式）时发原文件，交给 OpenAI 兼容格式时发 PDFKit 提取出的文字；Word、RTF、纯文本读出文字。组装消息在 `LiveSubCore/ChatAttachment.swift`。Claude Code 的 stream-json 输入收 image、document 块，和 Anthropic API 格式一样（2026-10-22 实测能读）。端到端自检：`--assistant-test 记录.md --ask "…" --attach 文件`。
- 没有主菜单时，⌘C、⌘V、⌘A、⌘Z 都到不了输入框，只能右键粘贴（2026-10-22 用测试程序确认过），所以 `AppDelegate.setUpEditMenu` 装了一个不显示的“编辑”菜单。按键先到 key 窗口、再到菜单，`AssistantPanel.performKeyEquivalent` 因此能抢先把 ⌘V 的图片和文件当附件。
- `Sources/LiveSub/App/AssistantSession.swift` + `UI/AssistantWindow.swift`：AI 助手。复用 `ClaudeTranslator.request(newSessionMessage:ongoingMessage:)` 的长驻进程；提问时才把新增字幕带上（不要改成每句都发，会每句触发一次回答）。用户点“开始”时新对话，设置引起的自动重启不清空。
