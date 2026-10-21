# 实时翻译助手 Windows 版 · LiveSub for Windows

[中文](#中文) · [English](#english)

版本 0.1.0（第一个 Windows 版本，尚未在实体 Windows 电脑上运行过，请按文末的测试清单逐项确认）。
Version 0.1.0 — the first Windows build. It has not yet been run on a real Windows PC; please go through the test checklist.

MIT License · © 2026 Shuo Yang

---

## 中文

实时监听**系统正在播放的声音**（视频、网课、线上会议）或**麦克风**（讲座、会议、对话），在本机识别成文字，交给你选择的大模型翻译，以**悬浮双语字幕**显示在屏幕上，并自动保存文字记录、导出 SRT / WebVTT 字幕。

| 环节 | 实现 | 说明 |
|---|---|---|
| 系统声音 | WASAPI 环回采集（NAudio） | 不需要虚拟声卡 |
| 麦克风 | WASAPI 采集（NAudio） | 默认录音设备变化时自动重新开始采集 |
| 语音识别 | Whisper.net（whisper.cpp），本地运行 | 首次使用时下载模型；音频不离开本机 |
| 翻译 | Claude Code 命令行 / Anthropic API / OpenAI 兼容接口 | 三选一，可随时切换 |
| 界面 | WPF 悬浮字幕 + 托盘图标 | 置顶、不抢焦点、可锁定为点击穿透 |

### 运行要求

- Windows 10 或 Windows 11，64 位（x64）。
- [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)（x64 的 “.NET Desktop Runtime”）；使用“独立发布”构建时不需要安装。
- 处理器：识别在处理器上运行。支持 AVX2 的处理器更快；base 模型在近几年的笔记本上可以跟上正常语速。
- 首次使用需要联网下载识别模型（tiny 约 75 MB，base 约 142 MB，small 约 466 MB）。
- 以下翻译服务任选其一：
  - 已登录的 Claude Code 命令行；
  - Anthropic API 密钥；
  - OpenAI 兼容服务的 API 密钥（OpenAI、DeepSeek、通义千问、Gemini、OpenRouter 等），或本机运行的 Ollama / LM Studio。

### 构建与运行

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。在 `windows` 目录下：

```powershell
dotnet build LiveSub.Windows.sln -c Release          # 构建
dotnet test tests/LiveSub.Core.Tests                 # 单元测试（不依赖 Windows，macOS 上也能跑）
dotnet run --project src/LiveSub.App -c Release      # 运行

# 独立发布：目标电脑不需要安装 .NET，输出 publish\LiveSub.exe
dotnet publish src/LiveSub.App -c Release -r win-x64 --self-contained -o publish

# 可选：带 Vulkan 显卡运行库（体积更大；设置里打开“显卡加速”）
dotnet publish src/LiveSub.App -c Release -r win-x64 --self-contained -p:LiveSubGpu=vulkan -o publish-vulkan
# NVIDIA 显卡也可以用 -p:LiveSubGpu=cuda（需要本机安装对应的 CUDA 运行库）
```

程序没有代码签名，第一次运行时 SmartScreen 可能提示“未知发布者”，选择“仍要运行”。

开发时在 macOS 上也能构建（`EnableWindowsTargeting`），但不能运行界面。

### 首次使用

1. 运行 `LiveSub.exe`。任务栏右侧的通知区域出现字幕图标（可能在“^”折叠区里，可以拖到任务栏上），屏幕底部出现字幕面板“实时翻译助手 已就绪”。
2. 右键托盘图标 → **设置…**，在“翻译服务”里选择要用的服务并完成配置（见下一节），点 **测试翻译** 确认可用。
3. 右键托盘图标 → **开始字幕**。第一次会下载识别模型，字幕面板上显示下载进度；模型保存在 `%LOCALAPPDATA%\LiveSub\models`。
   无法访问 huggingface.co 时，在设置 → 语音识别 → **模型下载地址** 填镜像站（例如 `https://hf-mirror.com`），或者手动把 `ggml-base.bin` 放进上面的文件夹。

### 配置翻译服务

**Claude Code 命令行**（用你的 Claude Code 登录额度，与 macOS 版相同）

1. 安装 Claude Code（PowerShell 运行 `irm https://claude.ai/install.ps1 | iex`，或 `npm install -g @anthropic-ai/claude-code`）。
2. 在终端运行 `claude`，输入 `/login` 登录一次。
3. 设置 → Claude Code → 模型选 Haiku（最快）、Sonnet 或 Opus。`claude` 路径留空时自动查找：PATH、`%USERPROFILE%\.local\bin\claude.exe`、`%APPDATA%\npm\claude.cmd`（npm 安装的会改用 node 直接运行，以便传递多行提示词）。
4. 程序保持一个长驻的 `claude -p --input-format stream-json …` 进程，参数与环境变量与 macOS 版一致；每 40 句换新进程，新进程第一句带最近 6 句作为上下文。

**Anthropic API**

1. 在 Anthropic Console 创建 API 密钥，填到设置 → Anthropic API → API 密钥。
2. 模型：`claude-haiku-5-5`（默认，最快）、`claude-sonnet-5-5`、`claude-opus-5-5`；“获取模型”可以列出账号可用的全部模型。
3. 低延迟设置：Haiku 关闭思考；Sonnet 与 Opus 不能关闭思考，改为 effort = low。

**OpenAI 兼容接口**（一个实现覆盖多家）

设置 → OpenAI 兼容接口 → 选预设 → **添加**，填 API 密钥，点 **获取模型** 选一个模型，然后在“翻译服务”里选它。

| 预设 | 接口地址 | 密钥 |
|---|---|---|
| OpenAI | `https://api.openai.com/v1` | 需要 |
| DeepSeek | `https://api.deepseek.com/v1` | 需要 |
| 通义千问（兼容模式） | `https://dashscope.aliyuncs.com/compatible-mode/v1` | 需要 |
| Gemini（OpenAI 兼容接口） | `https://generativelanguage.googleapis.com/v1beta/openai` | 需要 |
| OpenRouter | `https://openrouter.ai/api/v1` | 需要 |
| Ollama | `http://localhost:11434/v1` | 不需要 |
| LM Studio | `http://localhost:1234/v1` | 不需要 |
| 自定义 | 任意兼容 `/chat/completions` 的地址 | 本机和局域网地址不需要 |

请求为 `POST {接口地址}/chat/completions`，流式输出；模型列表来自 `GET {接口地址}/models`。推理模型（例如 DeepSeek R1）的思考内容不会显示，但会增加延迟，翻译建议选非推理模型。

所有服务都使用与 macOS 版相同的提示词、上下文格式（`[Context]…[Translate]`）和译文清理规则。

### 使用

- **托盘图标**：左键单击显示或隐藏字幕；右键打开菜单：开始/停止字幕、音源（麦克风 / 系统音频）、翻译模型、显示内容、显示/隐藏字幕、锁定字幕、字幕移回屏幕底部、导出字幕（SRT / WebVTT）、历史记录、开始新的记录、打开记录文件夹、设置、退出。图标实心表示正在听。
- **快捷键 Win+Alt+L**（默认）：显示或隐藏字幕。可在设置 → 字幕 → 快捷键 改成 Ctrl+Alt+L 或关闭。默认不用 Ctrl+Alt+L，是因为波兰语等键盘布局里 Ctrl+Alt 等同于 AltGr，会占用 AltGr+L 输入的字符（如“ł”）。
- **悬浮字幕**：按住拖动；鼠标移上去出现快捷按钮（开始/停止、切换显示内容、锁定、历史记录、设置、隐藏）。窗口大小只随设置变化，出字幕时不会忽高忽低：新字幕在框里从下往上滚动，高度默认正好放下“显示句数”句（每句按一行原文、一行译文算）。拖左右边框改宽度；拖上下边框改成自己想要的高度；双击上下边框恢复按句数算的高度。在字幕上往上滚动可以翻看历史，这时右边出现细的位置条和“返回实时”；停在底部时自动跟随最新一句。
- **锁定字幕**：字幕不再响应鼠标，可以点到下面的窗口。在托盘菜单里解锁。
- **状态条**（字幕底部一小行）：音量、识别是否清晰、当前模型（点击可切换）、待翻译句数、最近的错误。
- **记录**：每句实时写入 `文档\LiveSub\<时间> 字幕记录.md`，格式与 macOS 版相同（英文界面下文件名和文件头为英文：`<时间> Transcript.md`、“Started”“Language”和英文语言名）。没来得及翻译的句子在“开始新的记录”或退出时只写原文，不会丢句子。
- **导出字幕**：每句记录了它在音频里的起止时间（相对本次记录开始录音的时刻）；文字按当前显示内容（双语/仅译文/仅原文）。SRT 带 UTF-8 BOM，方便 Windows 播放器识别中文。
- **界面语言**：中文系统显示中文（简体），其他系统显示英文。测试时可以用环境变量 `LIVESUB_UI_LANGUAGE=en` 或 `zh-Hans` 指定。

### 隐私

- 语音识别完全在本机运行，音频不会上传。
- 识别出的文字会发送给你选择的翻译服务（Claude Code / Anthropic / 所选 OpenAI 兼容服务）；使用 Ollama、LM Studio 时文字也留在本机。
- API 密钥用 Windows 数据保护（DPAPI，仅当前 Windows 用户可解密）加密，保存在 `%APPDATA%\LiveSub\secrets.bin`，不写入 `settings.json`，不写入日志。
- 设置：`%APPDATA%\LiveSub\settings.json`。日志：`%LOCALAPPDATA%\LiveSub\logs\livesub.log`，只记录事件和错误；设置环境变量 `LIVESUB_DEBUG_LATENCY=1` 时才会记录字幕文字（延迟分析用）。
- 记录文件保存在本机 `文档\LiveSub`，不会上传。

### 已知限制

- **字幕比 macOS 版晚**：Whisper 不是流式识别，要等讲者停顿约 0.6 秒（或一段满 8 秒）才出确认结果。“实时预览”会先显示正在说的内容，但会多用处理器；机器较慢时程序会自动跳过预览。
- **没有本地兜底翻译**：macOS 版有 Apple 本地翻译做兜底；Windows 版翻译服务出错时这一句显示“翻译失败”，状态条显示原因。
- **过滤中文说话声**：可选，Windows 版默认关闭（macOS 版默认开启）。打开后每段多做一次 Whisper 语言判断（只在“讲话语言 / 中文”之间判断），处理器开销约增加一半。
- **自动纠正识别错误**：用 Whisper 的逐词概率判断“难识别”。阈值沿用 macOS（0.80 进入、0.86 退出），但 Whisper 的概率刻度不同，可能需要按实测调整（日志里有平均置信度）。
- 一次只能用一个音源（麦克风或系统音频），v0.1 没有“两者同时”。
- 中途换默认输出设备或默认录音设备（插耳机、蓝牙）时会自动重新开始采集；新设备连续 3 次打不开时停止字幕并说明原因。需要实测。
- “显卡加速”需要用 `-p:LiveSubGpu=vulkan` 或 `cuda` 构建，改动在重新启动程序后生效。
- Claude Code 在 Windows 上可能要求安装 Git for Windows；未登录时字幕会提示登录。

### 计划中（v0.1 未包含）

- AI 助手面板（边听边问）。
- 用模型生成笔记。
- 麦克风降噪。
- 历史记录搜索。

### 测试清单

请在实体 Windows 电脑上逐项确认，记下结果（通过 / 不通过 + 现象）。日志在 `%LOCALAPPDATA%\LiveSub\logs\livesub.log`。

**安装与启动**

1. 按“构建与运行”构建并启动：托盘出现图标，屏幕底部居中出现字幕面板；面板不出现在任务栏和 Alt+Tab 里。
2. 再启动一次程序：不会出现第二个托盘图标，已运行的程序把字幕显示出来。
3. 打开设置，改几项后退出并重新启动：设置保留（`%APPDATA%\LiveSub\settings.json`）。

**识别模型**

4. 第一次点“开始字幕”：字幕面板显示下载进度，完成后开始识别；`%LOCALAPPDATA%\LiveSub\models\ggml-base.bin` 约 142 MB。
5. 下载过程中点“停止字幕”：下载取消，再次开始能重新下载。在设置里换成 tiny / small，点“下载”，状态变为“已下载”。
6. （可选）填镜像地址后下载。

**音源**

7. 系统音频：播放一段英语视频，开始字幕，确认原文和译文出现，记录大致延迟（说完到译文出现）。
8. **系统静音时的系统音频**：分别试 (a) 音量调到 0，(b) 点静音按钮，(c) 视频播放器自身静音。哪种情况下还有字幕？
9. 系统 30 秒不播放任何声音后再播放：状态条显示“无声音”而不是“无音频输入”，之后的句子正常识别。
10. 播放中切换输出设备（插拔耳机、连接蓝牙耳机、在声音设置里换默认输出）：字幕继续，状态条提示“音频设备已切换”。
11. 麦克风：音源切到麦克风，对着麦克风说话，确认出字幕。
12. 在“隐私和安全性 › 麦克风”里关闭桌面应用访问麦克风：开始字幕时给出明确提示（或记下实际现象）。
13. 使用麦克风时切换默认录音设备：字幕继续。

**翻译服务**

14. Claude Code（已登录）：选 Haiku，开始字幕，状态条显示 “Claude Haiku”，译文逐字出现；任务管理器里只有一个常驻的 claude 进程。
15. Claude Code 未登录（或临时改名 claude.exe）：状态条和字幕给出“未登录 / 找不到 claude”的提示，不会卡住。
16. （如果用 npm 安装）`claude.cmd` 能被找到并正常翻译；设置里显示找到的路径。
17. Anthropic API：填密钥 → 测试翻译成功；分别试 Haiku、Sonnet、Opus；填错密钥时提示“API 密钥无效”。
18. OpenAI 兼容服务：对手上有的服务（DeepSeek、通义千问、OpenAI、Gemini、OpenRouter 任选），添加预设 → 填密钥 → 获取模型 → 选模型 → 测试翻译 → 开始字幕，确认译文逐字出现。
19. Ollama 或 LM Studio（本机）：不填密钥，获取模型、测试翻译、开始字幕都正常。
20. 运行中从托盘菜单和状态条（点模型名）切换模型：下一句起使用新模型。
21. 翻译 40 句以上：Claude Code 进程会换新的一个（旧进程退出，不会越积越多）。

**悬浮字幕**

22. 拖动字幕到其他位置，重新启动后位置保留；“字幕移回屏幕底部”有效。
23. 拖左右边框改宽度；拖上下边框改高度，重新启动后保留；双击上下边框恢复按“显示句数”算的高度；四个角都能拖。在字幕上往上滚动，出现细的位置条和“返回实时”，点击回到最新。按住字幕文字也能拖动窗口。
24. 不抢焦点：在记事本里打字时点击、拖动字幕，记事本仍保持输入焦点。
25. 锁定：鼠标可以点到字幕下面的窗口，快捷按钮不再出现；从托盘菜单解锁。
26. 快捷键 Win+Alt+L 显示/隐藏字幕；在设置里改成 Ctrl+Alt+L 或“无”后立即生效；快捷按钮里的“隐藏”有效。（波兰语等布局下，选 Ctrl+Alt+L 时 AltGr+L 不能再输入“ł”，这是预期的。）
27. 全屏：浏览器全屏播放视频（F11 或视频全屏按钮）时字幕仍在最上层。
28. 大小固定：讲得快、句子长短不一时，字幕窗口的高度一直不变，新句在框里往上滚，出现时有淡入；改字号、显示句数、显示内容时高度跟着变，底边不动。
29. 显示内容（双语 / 仅译文 / 仅原文）、字号、背景不透明度、显示句数、状态条开关都即时生效。
30. 讲得快、翻译跟不上时，状态条显示“待翻译 N 句”，积压的句子合并翻译，延迟不会越来越长。
31. 有口音或嘈杂的讲者：状态条可能变为“识别较难 · 已自动纠正”；记下日志里 `recognition difficult/clear (avg confidence …)` 的数值，用来校准阈值。
32. 实时预览：讲话过程中出现斜体的“正在说”一行；关闭“实时预览”后不再出现。
33. 过滤中文说话声：讲话语言为英语时打开此项，播放英语视频的同时有人说中文，中文部分不出字幕，状态显示“已过滤中文说话 N 段”。

**记录与导出**

34. `文档\LiveSub\<时间> 字幕记录.md` 实时写入，格式与 macOS 版相同；“开始新的记录”后新建文件；讲话过程中直接退出或开始新的记录，最后几句（含未翻译的原文）也在记录里。英文界面下文件名为 `<时间> Transcript.md`，文件头为英文。
35. 导出 SRT 和 WebVTT：用 VLC 或系统“媒体播放器”配合同一段视频（或同时录的屏幕录像）播放，时间轴基本对齐，中文不乱码。
36. 历史记录窗口：列出本次全部字幕，往上翻看时不会被拉回底部；“打开记录文件”有效。

**界面语言与其他**

37. 中文 Windows 上界面为中文、程序名“实时翻译助手”；设置环境变量 `LIVESUB_UI_LANGUAGE=en` 后启动为英文、程序名 “LiveSub”。检查有没有文字被截断。
38. 用测试音频跑完整链路：`$env:LIVESUB_TEST_AUDIO_FILE="C:\path\talk.wav"; $env:LIVESUB_AUTOSTART="1"; .\LiveSub.exe`。
39. 连续运行 1 小时：在任务管理器里记下内存与处理器占用，确认没有持续增长。
40. 退出：托盘图标消失，任务管理器里没有残留的 LiveSub、claude 或 node 进程。
41. 日志里没有 API 密钥。
42. （可选）用 `-p:LiveSubGpu=vulkan` 构建并打开“显卡加速”：日志 `whisper: loaded … runtime …` 显示使用的运行库，比较识别速度。

---

## English

LiveSub listens to **what your PC is playing** (videos, online classes, meetings) or **your microphone** (lectures, conversations), transcribes it locally, translates it with the model of your choice, and shows **floating bilingual subtitles**. It also saves a live transcript and exports SRT / WebVTT.

| Stage | Implementation | Notes |
|---|---|---|
| System audio | WASAPI loopback (NAudio) | No virtual audio device needed |
| Microphone | WASAPI capture (NAudio) | Capture restarts when the default recording device changes |
| Speech recognition | Whisper.net (whisper.cpp), local | Model downloaded on first use; audio never leaves the PC |
| Translation | Claude Code CLI / Anthropic API / OpenAI-compatible APIs | Pick one, switch any time |
| UI | WPF overlay + tray icon | Topmost, never steals focus, optional click-through |

### Requirements

- Windows 10 or 11, 64-bit (x64).
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64), unless you use a self-contained build.
- Recognition runs on the CPU; AVX2 CPUs are faster. The `base` model keeps up with normal speech on recent laptops.
- Internet access on first use to download the speech model (tiny ≈ 75 MB, base ≈ 142 MB, small ≈ 466 MB).
- One translation service: a signed-in Claude Code CLI, an Anthropic API key, an API key for an OpenAI-compatible service (OpenAI, DeepSeek, Qwen, Gemini, OpenRouter, …), or a local Ollama / LM Studio.

### Build and run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). From the `windows` folder:

```powershell
dotnet build LiveSub.Windows.sln -c Release
dotnet test tests/LiveSub.Core.Tests                 # unit tests (platform-independent; also run on macOS)
dotnet run --project src/LiveSub.App -c Release

# Self-contained build: no .NET install needed on the target PC; produces publish\LiveSub.exe
dotnet publish src/LiveSub.App -c Release -r win-x64 --self-contained -o publish

# Optional GPU runtime (larger; enable "GPU Acceleration" in Settings)
dotnet publish src/LiveSub.App -c Release -r win-x64 --self-contained -p:LiveSubGpu=vulkan -o publish-vulkan
# NVIDIA: -p:LiveSubGpu=cuda (requires a matching CUDA runtime)
```

The executable is not code-signed; SmartScreen may warn about an unknown publisher on first launch.

### First run

1. Start `LiveSub.exe`. A subtitle icon appears in the notification area (possibly under “^”), and the overlay appears at the bottom of the screen.
2. Right-click the tray icon → **Settings…**, choose a **Translation Service**, configure it (below), and click **Test Translation**.
3. Right-click → **Start Subtitles**. The first start downloads the speech model with progress shown in the overlay, into `%LOCALAPPDATA%\LiveSub\models`. If huggingface.co is unreachable, set a mirror under Settings → Speech Recognition → **Model Download Host**, or copy `ggml-base.bin` into that folder.

### Translation services

**Claude Code CLI** — uses your Claude Code subscription, exactly like the macOS app. Install Claude Code (`irm https://claude.ai/install.ps1 | iex` or npm), run `claude` once and `/login`. Models: Haiku (fastest), Sonnet, Opus. LiveSub finds `claude` on PATH, in `%USERPROFILE%\.local\bin\claude.exe` or `%APPDATA%\npm\claude.cmd` (npm shims are run through node directly so the multi-line system prompt survives). One long-lived `claude -p --input-format stream-json …` process with the same arguments and environment as macOS; a fresh process every 40 lines, seeded with the last 6 lines.

**Anthropic API** — paste an API key. Models: `claude-haiku-5-5` (default, fastest), `claude-sonnet-5-5`, `claude-opus-5-5`; **Fetch Models** lists everything your key can use. For low latency, Haiku runs with thinking disabled; Sonnet and Opus cannot disable thinking, so they run with effort = low.

**OpenAI-compatible services** — Settings → OpenAI-Compatible Services → pick a preset → **Add**, enter the key, **Fetch Models**, pick one, then select it as the Translation Service. Presets: OpenAI `https://api.openai.com/v1`, DeepSeek `https://api.deepseek.com/v1`, Qwen (DashScope compatible mode) `https://dashscope.aliyuncs.com/compatible-mode/v1`, Gemini `https://generativelanguage.googleapis.com/v1beta/openai`, OpenRouter `https://openrouter.ai/api/v1`, Ollama `http://localhost:11434/v1`, LM Studio `http://localhost:1234/v1`, or any custom endpoint that implements `/chat/completions`. Local and LAN addresses need no key. Reasoning models add latency; prefer non-reasoning models for subtitles.

All services share the macOS prompts, the `[Context]…[Translate]` context format and the output sanitizer.

### Usage

- **Tray icon**: left-click shows or hides the subtitles; right-click opens the menu (start/stop, audio source, translation model, display, show/hide, lock, move to bottom, export SRT/WebVTT, history, new transcript, open folder, settings, quit). A filled icon means LiveSub is listening.
- **Win+Alt+L** (default) shows or hides the subtitles; change it to Ctrl+Alt+L or turn it off under Settings → Subtitles → Shortcut. Ctrl+Alt+L is not the default because Ctrl+Alt equals AltGr on layouts such as Polish, where it would block characters like “ł”.
- **Overlay**: drag to move; hover for quick buttons. The window size changes only with settings, never with the text: new lines scroll up inside it, and the default height fits “Lines Shown” lines (one source line plus one translation line each). Drag the left/right edges to change the width and the top/bottom edges to set your own height; double-click the top/bottom edge to go back to the height from Lines Shown. Scroll up to read earlier lines; a thin position bar and “Back to Live” appear.
- **Lock** makes the overlay click-through; unlock from the tray menu.
- **Status bar**: input level, recognition state, current model (click to switch), lines waiting for translation, last error.
- **Transcripts**: written live to `Documents\LiveSub\<time> 字幕记录.md` (same format as macOS; under the English UI the file is `<time> Transcript.md` with “Started”, “Language” and English language names). Lines still waiting for translation are written source-only on New Transcript or Quit. **Export** SRT / WebVTT uses the audio times of each line; text follows the current display mode.
- **UI language**: Chinese (Simplified) on Chinese Windows, English otherwise. Override with `LIVESUB_UI_LANGUAGE=en` or `zh-Hans`.

### Privacy

- Speech recognition runs entirely on your PC; audio is never uploaded.
- Recognized text is sent to the translation service you choose; with Ollama or LM Studio it stays on your PC.
- API keys are encrypted with Windows Data Protection (DPAPI, current user only) in `%APPDATA%\LiveSub\secrets.bin`, never written to `settings.json` or the log.
- Settings: `%APPDATA%\LiveSub\settings.json`. Log: `%LOCALAPPDATA%\LiveSub\logs\livesub.log` (events and errors; subtitle text only when `LIVESUB_DEBUG_LATENCY=1`).

### Known limitations

- Subtitles arrive later than on macOS: Whisper is not streaming, so a segment is recognized after a ~0.6 s pause or 8 s of continuous speech. Live Preview shows a draft of the current segment at extra CPU cost and is skipped automatically on slow machines.
- No offline fallback translator (macOS has Apple Translation). When the service fails, the line shows “Translation failed” and the status bar shows why.
- Filter Chinese Speech is optional and off by default on Windows (on by default on macOS); it adds one Whisper language check per segment (limited to the spoken language vs. Chinese).
- Auto-correction uses Whisper token probabilities with the macOS thresholds (0.80 / 0.86); Whisper's scale differs, so the thresholds may need calibration (the log records the average confidence).
- One audio source at a time (no “both” mode in v0.1).
- When the default output or recording device changes, capture restarts automatically; if the new device fails to open three times, subtitles stop with the reason shown.
- GPU acceleration requires a `-p:LiveSubGpu=vulkan` or `cuda` build and takes effect after restart.
- Claude Code on Windows may require Git for Windows.

### Planned (not in v0.1)

AI assistant panel, notes generation, microphone noise reduction, history search.

### Test checklist

Record pass / fail and what you saw. The log is at `%LOCALAPPDATA%\LiveSub\logs\livesub.log`.

1. Build and launch: tray icon appears; overlay at the bottom center; overlay is not in the taskbar or Alt+Tab.
2. Launch a second time: no second tray icon; the running instance shows the overlay.
3. Change settings, quit, relaunch: settings persist.
4. First Start downloads the model with progress; `ggml-base.bin` ≈ 142 MB lands in `%LOCALAPPDATA%\LiveSub\models`.
5. Stop during download cancels it; Start again resumes from scratch. Download tiny/small from Settings.
6. (Optional) Download through a mirror host.
7. System audio: play an English video and confirm subtitles and translations; note the latency.
8. **System audio while muted**: try (a) volume 0, (b) the mute button, (c) muting inside the player. Which cases still produce subtitles?
9. Silence for 30 s, then play again: status shows “Silent”, not “No audio input”; recognition resumes.
10. Switch the output device while playing (headphones, Bluetooth, default device): subtitles continue; “Audio device changed” appears.
11. Microphone: switch the source and speak.
12. Disable desktop-app microphone access in Privacy settings: Start shows a clear message (or note what happens).
13. Change the default recording device while using the microphone: subtitles continue.
14. Claude Code (signed in), Haiku: status bar shows “Claude Haiku”; text streams; only one resident claude process.
15. Claude Code signed out or missing: a clear message, no hang.
16. (npm install) `claude.cmd` is found and works; Settings shows the path.
17. Anthropic API: Test Translation succeeds; try Haiku, Sonnet, Opus; a wrong key shows “The API key was rejected”.
18. OpenAI-compatible: for each service you have, add preset → key → Fetch Models → pick → Test → Start; text streams.
19. Ollama or LM Studio: no key; fetch models, test, start.
20. Switch models from the tray menu and from the status bar while running.
21. After 40+ lines the Claude process is replaced (old one exits; processes do not pile up).
22. Drag the overlay; position persists across restarts; “Move Subtitles to Bottom” works.
23. Resize from all edges and corners; a dragged height persists across restarts; double-click the top/bottom edge restores the Lines Shown height; scrolling up shows the position bar and “Back to Live”; dragging on the text moves the window.
24. No focus stealing: type in Notepad while clicking/dragging the overlay.
25. Lock: clicks pass through; quick buttons hidden; unlock from the tray.
26. Win+Alt+L toggles the overlay; switching to Ctrl+Alt+L or None in Settings takes effect immediately; the Hide button works.
27. Fullscreen browser video: the overlay stays on top.
28. Fixed size: with fast speech and lines of varying length the overlay height never changes; new lines scroll up and fade in; changing font size, Lines Shown or display mode resizes it with the bottom edge fixed.
29. Display mode, font sizes, opacity, lines shown, status bar apply immediately.
30. Fast speech: “N waiting” appears; backlog is merged; latency does not keep growing.
31. Accented/noisy speaker: status may switch to “Hard to recognize”; note `avg confidence` values in the log.
32. Live Preview shows an italic in-progress line; disabling it removes the line.
33. Filter Chinese Speech: Chinese chatter over an English video is dropped; the status counts filtered segments.
34. Transcript file is written live in the macOS format; “New Transcript” starts a new file; quitting or starting a new transcript mid-speech still records the last lines (untranslated ones source-only). Under the English UI the file is `<time> Transcript.md` with an English header.
35. Export SRT and WebVTT; play with VLC or Media Player next to the video; timing roughly aligned; Chinese displays correctly.
36. History window lists all lines; scrolling up is not interrupted; “Open Transcript” works.
37. Chinese Windows shows Chinese UI and “实时翻译助手”; `LIVESUB_UI_LANGUAGE=en` shows English and “LiveSub”; check for clipped text.
38. Full pipeline from a file: `$env:LIVESUB_TEST_AUDIO_FILE="C:\path\talk.wav"; $env:LIVESUB_AUTOSTART="1"; .\LiveSub.exe`.
39. Run for one hour; memory and CPU stay flat in Task Manager.
40. Quit: tray icon disappears; no leftover LiveSub, claude or node processes.
41. The log contains no API keys.
42. (Optional) Vulkan build with GPU Acceleration: the log line `whisper: loaded … runtime …` shows the runtime used; compare speed.

### Project layout

```
LiveSub.Windows.sln
src/LiveSub.Core/         platform-independent logic (ports of macOS LiveSubCore + chat/SSE/audio chunking), net10.0
src/LiveSub.App/          WPF tray app (net10.0-windows): audio, Whisper, providers, overlay, settings
tests/LiveSub.Core.Tests/ xUnit tests (same inputs/outputs as the Swift tests, plus Windows-specific pieces)
```
