# 交接说明

下次对话从这里开始。更新于 2026-10-20。开发说明、自检命令和踩过的坑见 [CLAUDE.md](CLAUDE.md)，长期计划见 [ROADMAP.md](ROADMAP.md)。

## 现在的状态

- macOS 版可以日常使用，已安装在 `~/Applications/LiveSub.app`，中文名“实时翻译助手”。
  - 最低 macOS 14.2：macOS 26 用 SpeechAnalyzer，更早的系统用 SFSpeechRecognizer。
  - 界面有中文和英文。
  - 翻译模型分三档：基础（Apple 本地翻译）、云端模型（Claude Code 或自己的 API）、本地模型（Ollama、LM Studio）。
- 单元测试 72 个，全部通过（`scripts/test.sh`）。
- Windows 版 0.1 在 `windows/`，用 C#、WPF 写成，能编译，Core 测试 94 个通过。还没在 Windows 电脑上运行过。
- 代码在 GitHub 私有仓库 `ShuoYang-cs/LiveSub`（2026-10-20 第一次提交）。Windows 电脑上要 clone 到 `G:\LiveSub`；两边的项目都关联为 `livesub`，用于 Agent Sync 跨电脑同步对话。

## 待办

### 1. Windows 版实机测试（用户自己测）

- 用户在自己的 Windows 电脑上按 [windows/README.md](windows/README.md) 的“测试清单”逐项测试，共 42 项。日志在 `%LOCALAPPDATA%\LiveSub\logs\livesub.log`。
- 下次对话要做的：根据用户反馈的结果修复。写 Windows 版的子代理的上下文已经没有了，开始前先读 `windows/README.md` 和 `windows/src/` 的代码。
- 在 Mac 上编译和测试：
  - `~/.dotnet/dotnet build windows/LiveSub.Windows.sln`
  - `~/.dotnet/dotnet test windows/tests/LiveSub.Core.Tests`
  - 界面和音频只能在 Windows 上验证。
- 已定的默认值：
  - 过滤中文说话声默认关闭；
  - Whisper 默认用 base 模型；
  - 英文界面下，记录文件的开头也用英文；
  - 模型出错时不自动换别的服务；
  - 全局快捷键默认 Win+Alt+L，可以在设置里改。

### 2. 多模型选择的调整（还没做）

现有实现：

- 接口格式和服务商模板：`Sources/LiveSubCore/ChatAPI.swift`
- HTTP 会话、钥匙串和工厂：`Sources/LiveSub/Translation/APIChatSession.swift`
- “模型服务”窗口：`Sources/LiveSub/UI/ModelServicesView.swift`
- 模型选择：`Sources/LiveSub/App/AppSettings.swift` 里的 `ModelChoice` 和 `modelGroups`

待做：

1. **首次启动引导**：让新用户选翻译档位，并自动检测本机条件：
   - Claude Code：用 `ClaudeCLI.locate`，再判断是否已登录；
   - Ollama：`http://localhost:11434/v1/models`；
   - LM Studio：`http://localhost:1234/v1/models`。

   现在新用户要自己去设置里找。
2. **各档位的功能提示**：基础档（Apple 翻译）没有自动纠正口音、AI 助手和笔记，界面上要说明白。
3. **其他模型的质量和延迟测试**：测 DeepSeek、GPT、本地模型等。
   - 命令：`--translate "..." --engine api --format openai --base-url ... --model ...`，密钥放在环境变量 `LIVESUB_API_KEY`；
   - 用 `--export-test 音频 out.srt` 加 `LIVESUB_DEBUG_LATENCY=1` 看延迟；
   - 检查三点：输出格式对不对，纠正口音时的 `@@src:` 行对不对，遇到问句时只翻译、不回答。
   - 本机只有 16GB 内存，不要在本地跑大模型，用云端 API 测。
4. **先问清楚用户还想怎么调整**。用户只说了“对多模型选择的调整还没做”，开始前确认具体要改什么。

### 3. 开源发布前（用户还没要求开始，做之前先问）

- **第一次提交**：先确认哪些文件公开。
  - `CLAUDE.md` 里有用户的个人偏好，比如 16GB 内存；
  - 本文件只是交接用；
  - `windows/` 下的 `bin`、`obj`、`publish` 已经由 `windows/.gitignore` 排除。
- README 加英文版、截图和演示动图。
- GitHub Actions：在 macOS 14 和 15 的云端机器上编译、跑单元测试、检查能否启动。这可以弥补没有老款 Mac 的问题，但语音识别在云端测不了。
- 贡献指南、Issue 模板。
- 核对 Windows 版依赖的许可证：NAudio、Whisper.net、Anthropic C# SDK、Whisper 模型。
- 分发方式由用户决定：只提供源码让用户自己构建，或加入 Apple 开发者计划（每年 99 美元）签名公证后发布。

### 4. 已经改过、只能在真实使用中确认的

- **字幕窗口大小固定、AI 助手可单独拖动**（2026-10-20）：
  - 字幕窗口不再按内容自动调整高度。高度默认按“显示最近 N 句”算，拖过上下边框就用拖出来的高度。原来“自动高度时往上滚动展开历史”的功能去掉了，往回看就在框里滚动。
  - AI 助手不再跟着字幕窗口移动。按住标题栏可以拖动，位置会被记住。
  - 离屏压力测试里窗口高度始终不变；用模拟鼠标在真实窗口里拖过助手标题栏和字幕窗口，各自独立移动。用户还没在实际使用中确认，默认高度是否合适也要看反馈：句子折成多行时，最上面一句会被挤出去一部分。
  - Windows 版同样改了：字幕始终在固定高度的滚动区里，高度默认按“显示句数”算，系统滚动条换成往上翻看时才出现的细位置条，滚动区不拿焦点，以免按住字幕时拖不动窗口。只编译和跑了 Core 测试，界面要在 Windows 上按测试清单第 23、28 项确认。Windows 版还没有 AI 助手面板。
  - 新版已安装到 `~/Applications/LiveSub.app`。
- **手动滚动字幕**：之前出现过彩球卡死，原因是主线程卡在“滚到底 → 动画结束 → 再滚”的循环里，已经修复。修复后还没有用真实的触控板手势验证。再出现时看 `~/Library/Logs/LiveSub/hang-*.txt`。
- **自动纠正口音**：现在默认关闭，打开后才会纠正。还没在口音重的真实讲课里测过，测试用的合成语音太清晰，触发不了。
- **macOS 14.2–15**：从没在真机上跑过。旧版识别器只在 macOS 26 上用 `LIVESUB_LEGACY_SPEECH=1` 模拟测过。

## 用户偏好和已定的决定（不要改回去）

- 用简体中文回复；措辞正式，少用括号注释；说“锁定”，不说“鼠标穿透”。
- 这是面向所有人的通用翻译工具，不只是上课用：不要课程、术语表这类功能。
- 设置要简洁，不常用的放进“更多”；用户改过的设置必须记住，比如选过 Opus、改过的开关。
- 已经删掉、不要再加回来的：说话时的草稿译文、开机自动启动、手动选口音。
- 识别困难时，由程序自动判断要不要纠正；用户只负责开关“自动纠正口音”（默认关闭），打开后下面出现“纠正时保留识别原文”（也默认关闭）。
- 字幕窗口大小只随设置变化，不随内容忽高忽低；AI 助手独立于字幕窗口，可以拖动，位置记住。
- 字幕上的快捷按钮只留开始/停止、锁定、“⋯”菜单、收起。“⋯”菜单里是显示内容、字幕记录…、AI 助手、设置…。往回看靠在字幕上滚动。
- 许可证 MIT，署名 Shuo Yang（杨朔）。
- 用户的 Claude Code 是用 `~/.zshrc` 里的 `CLAUDE_CODE_OAUTH_TOKEN` 登录的。App 会读取终端环境里的这个变量，不要打印令牌。
- 不提交代码，除非用户明确要求；测试生成的记录文件和测试用 API 密钥用完要删除。
