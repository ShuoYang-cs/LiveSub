# LiveSub 开发说明

**新对话先读 [HANDOFF.md](HANDOFF.md)**：未完成的工作、用户已定的决定和偏好。

macOS 14.2+ 菜单栏应用：麦克风/系统声音 → 本地语音识别（macOS 26 用 SpeechAnalyzer，更早用 SFSpeechRecognizer）→ 所选大模型翻译（默认 Claude CLI，也支持 API 和本地模型）→ 悬浮双语字幕。详见 README.md。

## 用户偏好
- 本机 16GB 内存，**不要在本地跑大模型**；重计算交给 Claude（通过本机 `claude` CLI，用户额度很高）或系统自带框架。

## 工具链限制（只有 Command Line Tools，没有 Xcode）
- SwiftUI 宏插件不可用：**不能用 `@State`、`@Entry`、`#Preview`**。视图状态用 `@Observable` 小对象。`@Observable`、`@Bindable` 可用。
- 测试用 Swift Testing，必须走 `scripts/test.sh`（补上宏插件路径）。
- `Package.swift` 通过 `LIVESUB_SDK_VERSION` 写入链接 SDK 版本；去掉会让 Translation 框架报 notInstalled。

## 构建与验证
- 发布 macOS 版：`scripts/package-release.sh` 生成 Apple 芯片和 Intel 通用的 dmg 和 zip（`build-app.sh --universal`；`--dmg-only` 用现成的 build/LiveSub.app 只做 dmg），上传到 GitHub Releases（tag 用 `macos-版本号`）。签名必须用这台电脑登录钥匙串里的“LiveSub Local Signing”：换了证书，用户的系统会把新版当成另一个 App，要重新授权。这个 SwiftPM 版本不管编哪种芯片，结果都放在同一个输出目录（`.build/out/Products/Release`），后编的覆盖先编的。这台电脑没装 Rosetta，Intel 版只能编译，不能在这里运行。
- `swift build`；`scripts/test.sh`；`scripts/build-app.sh [--install]` 生成 .app（用登录钥匙串里的 “LiveSub Local Signing” 证书签名，见 `scripts/make-signing-cert.sh`；不要改回 ad-hoc，否则每次重装都会重新弹麦克风权限）。
- 不用界面的自检：`.build/debug/LiveSub --transcribe-file x.aiff [--guard zh-CN] [--realtime]`、`--translate "..." --engine claude|apple [--accent ...]`、`--render-window out.png`（真实窗口截图；命令行版设置在 `defaults` 的 `LiveSub` 域。字幕都在 ScrollView 里，`ImageRenderer` 画不出来，所以没有纯 SwiftUI 的截图命令）。
- 测识别时间/延迟要加 `--realtime`：不加时整个文件瞬间喂完，临时结果的时间范围会失真。
- SpeechTranscriber 的临时结果没有逐词时间（整段一个 run），只有确认结果有；哨兵过滤只能靠确认结果。`LIVESUB_DEBUG_GUARD=1` 打印每句等待哨兵的时间。
- 用文件代替真实声音跑整个界面：`LIVESUB_TEST_AUDIO_FILE=... LIVESUB_AUTOSTART=1 .build/debug/LiveSub`。
- 在 Claude Code 的 Bash 里调用 `claude`、语音/翻译框架需要关闭沙箱。
- 从“应用程序”打开的 App 不读 `~/.zshrc`。在终端里用环境变量登录 Claude Code（例如 `~/.zshrc` 里的 `CLAUDE_CODE_OAUTH_TOKEN`）时，App 启动的 `claude` 会显示未登录，所以 `ClaudeCLI.shellEnvironment()` 会跑一次登录交互 shell，把 Claude、Anthropic、AWS、代理相关变量补给 `claude`（`ShellEnvironment`）。模拟 App 的干净环境：`/usr/bin/env -i HOME=$HOME USER=$USER SHELL=/bin/zsh PATH=/usr/bin:/bin .build/debug/LiveSub --translate "Hi." --engine claude`。判断翻译是不是真的走了 Claude，要看日志里有没有 `translate failed`，导出的字幕本身看不出来。
- 旧版识别器（macOS 14.2–15 的路径）在 macOS 26 上用 `LIVESUB_LEGACY_SPEECH=1` 测。它要“语音识别”权限，命令行直接跑会把权限记到终端上，要通过 App 包启动：`open -n --stdout out.txt --stderr out.txt --env LIVESUB_LEGACY_SPEECH=1 build/LiveSub.app --args --transcribe-file x.aiff --realtime`。实测（2026-10-20）：本机识别的确认结果是空的，临时结果没有逐词时间和置信度，说完后还会反复改写标点，所以停顿判断（`PauseDetector`）看声音和字数增长，连续说话时由 `PartialCommitter` 提前确认稳定的句子。

- 运行日志（卡住、没字幕时先看）：`/usr/bin/log show --last 1h --predicate 'subsystem == "com.livesub.app"'`（zsh 里裸写 `log` 是 shell 内置命令，什么也查不到）。主线程卡住超过 5 秒时，`MainThreadWatchdog` 会把调用栈存到 `~/Library/Logs/LiveSub/hang-*.txt`。
- 换翻译器不能留空档：打开“自动纠正口音”时，识别状态每切换一次就要换一个翻译进程，而触发切换的那句马上就要翻译。以前先置空、再异步准备新进程，结果每次切换都有一句“翻译失败”（2026-10-21 实测：失败的句子全部在切换的同一秒，一节课几十句）。现在 `updateQuality` 先同步建好新会话再替换，旧进程 30 秒后才关（`retire`）。字幕记录里只有原文、没有译文的句子就是失败的句子，可以拿来和日志里 `recognition difficult/clear` 的时间对照。
- `LIVESUB_DEBUG_CLAUDE=1`：把发给 Claude 的原文和原始回复打到 stderr（排查译文异常时用），还有每轮的原始 result 事件，里面是 token 用量和折算费用（费用和耗时是这个进程的累计值）。2026-10-22 实测 Haiku 翻 40 句真实字幕（一个进程）：缓存读入约 10.5 万、缓存写入约 4200、输出约 1300 token，折算约 0.0025 美元；讲课每小时约 440–720 句。`LIVESUB_DEBUG_LATENCY=1` 配合 `--export-test` 打印每句各阶段耗时（说完→入队、开始→首字、总计）。
- 延迟实测（2026-10-19）：识别器每句说完后 0.6–1.0 秒确认，Claude Haiku 首字 0.7–0.9 秒，合计约 1.5–1.9 秒。临时结果没有标点，不能靠“临时文本里的句号”提前确认（试过，永远不触发）。中文哨兵的等待最多 1.5 秒，只在汉字占比 ≥0.3 且这句有低置信度词时才等。`--export-test 音频 out.srt` 跑整条链路并导出字幕。
- `.fastResults`（识别器偏向速度）保留，不要去掉。`LIVESUB_NO_FAST_RESULTS=1` 可以去掉它来对比。2026-10-22 实测：用 `say` 合成 12 句讲课录音，有标准美音、印度口音、中文口音、加噪声几种，`--transcribe-file --realtime`。去掉后词错误率只降 0–4 个百分点（中文口音 13.8%→9.9%，强噪声下反而 14.4%→15.5%）。但说完到确认结果从 0.5–0.9 秒变成 1.7–3.1 秒，第一个灰色临时字幕也从约 0.8 秒变成 2–3 秒才出来。
- 字幕窗口高度只随设置变化（`OverlayView.height`：拖出来的高度，或按“显示最近 N 句”和字号算），不随内容变。不要改回按内容自动高度：出字幕时窗口会忽高忽低（用户提出过）。
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
- macOS 26 的菜单栏图标由“控制中心”统一托管；图标多时排在左边的会被刘海挡住。`AppDelegate.setUpStatusItem` 用 `NSStatusItem Preferred Position LiveSub` 把图标首次放在右侧。

## 结构
- `Sources/LiveSubCore`：纯逻辑（分句、stream-json 解析、提示词），改动要配单元测试。
- `Sources/LiveSub/App/SubtitleController.swift`：整条链路与翻译队列（积压合并、Claude 失败自动 Apple 兜底）。
- 系统录音权限被拒绝时，系统不报错，只送来静音，用户以为程序没反应。现在用非公开接口 `TCCAccessPreflight("kTCCServiceAudioCapture")` 查（0 允许、1 拒绝、2 没问过；`SystemAudioCapture.permissionDenied`），拒绝时字幕下方提示并给“打开系统设置”。不要改成“收到静音、同时有别的程序在输出声音”来猜：LiveSub 自己录系统声音建的聚合设备也算“在输出”，会议软件、网页常常开着输出却不出声，都会误报（2026-10-22 试过）。
- 设置窗口按住空白处或文字也能拖动（`WindowBackgroundDrag`，macOS 15 起用 `WindowDragGesture`）。不要改成在 `sendEvent` 里遇到“不是 NSControl 的地方”就 `performDrag`：“管理…”“恢复默认”这些按钮是 SwiftUI 自己画的，不是 NSControl，会被当成空白，按钮就点不了了。`isMovableByWindowBackground`、`WindowDragGesture` 都依赖真实鼠标，程序内部发模拟事件测不出效果（2026-10-22 试过）。辅助功能的 `accessibilityHitTest` 在离屏窗口里只返回整个视图，也分不出控件。带标题栏的窗口放到屏幕外，`orderFront` 时会被系统挪回屏幕上，探测时会在用户屏幕上闪一下。
- 系统对话框（选文件夹、导出字幕）一律用 `FilePanel.present`：字幕和 AI 助手在 `.statusBar` 层，比对话框高；用 `runModal` 时对话框的按钮被面板挡住，面板又拖不开，用户以为程序卡死了（2026-10-21 发生过）。
- AI 助手存文件：模型没有写文件的工具（Claude Code 以 `--tools ""` 运行，API 和本地模型也没有工具），系统提示让它在回答末尾用 `@@save: 文件名 … @@end` 块写出文件，App 解析（`LiveSubCore/Assistant.swift` 的 `AssistantFiles`）后只存进记录文件夹下的“AI 助手”文件夹，不覆盖已有文件。用户要求面板里不提示这个功能，只写在 README 里。
- AI 助手面板是不激活应用的面板，只有点输入框时才成为 key 窗口。这时要激活本应用（`AssistantPanel.becomeKey`）：不激活的话，原来那个程序不知道自己失去了键盘，它的输入框还在闪光标，两边同时闪（2026-10-22 用户提出）。点按钮、拖动面板时仍然不激活。判断要不要激活要看 `NSWorkspace` 的前台程序，不能看 `NSApp.isActive`：面板成为 key 窗口时它已经是 true，系统的前台程序却还是原来那个。2026-10-22 真实窗口里测过，从 Finder、VS Code 点进来都会切到 LiveSub，用 `NSApp.activate()` 就够了。
- 菜单里带对勾的选项（模型菜单）用 `Picker` 加 `.pickerStyle(.inline)` 放进 `Menu`，不要用“按钮 + checkmark 图标”：那样菜单宽度不算对勾，带对勾的那项会被截断（2026-10-22 真实窗口里“Claude Sonnet”显示成“Claud…onnet”）。
- AI 助手的附件：`AttachmentLoader` 读文件。图片长边缩到 1568 像素，PNG 太大时改存 JPEG；PDF 交给 Claude（Claude Code、Anthropic 格式）时发原文件，交给 OpenAI 兼容格式时发 PDFKit 提取出的文字；Word、RTF、纯文本读出文字。组装消息在 `LiveSubCore/ChatAttachment.swift`。Claude Code 的 stream-json 输入收 image、document 块，和 Anthropic API 格式一样（2026-10-22 实测能读）。端到端自检：`--assistant-test 记录.md --ask "…" --attach 文件`。
- 没有主菜单时，⌘C、⌘V、⌘A、⌘Z 都到不了输入框，只能右键粘贴（2026-10-22 用测试程序确认过），所以 `AppDelegate.setUpEditMenu` 装了一个不显示的“编辑”菜单。按键先到 key 窗口、再到菜单，`AssistantPanel.performKeyEquivalent` 因此能抢先把 ⌘V 的图片和文件当附件。
- `Sources/LiveSub/App/AssistantSession.swift` + `UI/AssistantWindow.swift`：AI 助手。复用 `ClaudeTranslator.request(newSessionMessage:ongoingMessage:)` 的长驻进程；提问时才把新增字幕带上（不要改成每句都发，会每句触发一次回答）。用户点“开始”时新对话，设置引起的自动重启不清空。
