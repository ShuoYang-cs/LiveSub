# 实时翻译助手 Windows 版 · LiveSub for Windows

[中文](#中文) · [English](#english)

版本 0.1.0（第一个 Windows 版本，尚未在实体 Windows 电脑上运行过，请按文末的测试清单逐项确认）。
Version 0.1.0 — the first Windows build. It has not yet been run on a real Windows PC; please go through the test checklist.

MIT License · © 2026 Shuo Yang

---

## 中文

实时监听**系统正在播放的声音**（视频、网课、线上会议）、**麦克风**（讲座、会议、对话），或两者同时，在本机识别成文字，交给你选择的大模型翻译，以**悬浮双语字幕**显示在屏幕上，并自动保存文字记录、导出 SRT / WebVTT 字幕。听的过程中可以随时向 **AI 助手**提问，结束后可以把字幕整理成**笔记**。

| 环节 | 实现 | 说明 |
|---|---|---|
| 系统声音 | WASAPI 环回采集（NAudio） | 不需要虚拟声卡 |
| 麦克风 | WASAPI 采集（NAudio） | 默认录音设备变化时自动重新开始采集；可选降噪（Windows 通话语音处理） |
| 语音识别 | Whisper.net（whisper.cpp），本地运行 | 首次使用时下载模型；音频不离开本机 |
| 翻译 | Claude Code 命令行，或自己添加的模型服务（Anthropic、OpenAI 兼容接口、本机的 Ollama / LM Studio） | 随时切换 |
| 界面 | WPF 悬浮字幕 + 托盘图标 | 置顶、不抢焦点、可锁定为点击穿透 |

### 运行要求

- Windows 10 或 Windows 11，64 位（x64）。
- [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)（x64 的 “.NET Desktop Runtime”）；使用“独立发布”构建时不需要安装。
- 处理器：识别在处理器上运行。支持 AVX2 的处理器更快；base 模型在近几年的笔记本上可以跟上正常语速。
- 首次使用需要联网下载识别模型（tiny 约 75 MB，base 约 142 MB，small 约 466 MB）。
- 以下翻译模型任选其一：
  - 已登录的 Claude Code 命令行；
  - 自己的 API 密钥：Anthropic，或 OpenAI 兼容的服务（OpenAI、DeepSeek、通义千问、Gemini、OpenRouter 等）；
  - 本机运行的 Ollama / LM Studio。

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
2. 右键托盘图标 → **设置…**，在“翻译模型”里选择模型。用 Claude Code 不需要别的设置；用自己的 API 或本地模型时，先在“模型服务”一行点 **管理…** 添加服务，点 **测试** 确认可用（见下一节）。
3. 右键托盘图标 → **开始字幕**。第一次会下载识别模型，字幕面板上显示下载进度；模型保存在 `%LOCALAPPDATA%\LiveSub\models`。
   无法访问 huggingface.co 时，在设置 → 更多 → **模型下载地址** 填镜像站（例如 `https://hf-mirror.com`），或者手动把 `ggml-base.bin` 放进上面的文件夹。

### 翻译模型与模型服务

与 macOS 版相同：设置里的“翻译模型”分为“云端模型”（Claude Code 和自己的 API）和“本地模型”（本机或局域网里的服务）两组；托盘菜单和状态条（点模型名）里也能切换。

**Claude Code 命令行**（用你的 Claude Code 登录额度，与 macOS 版相同）

1. 安装 Claude Code（PowerShell 运行 `irm https://claude.ai/install.ps1 | iex`，或 `npm install -g @anthropic-ai/claude-code`）。
2. 在终端运行 `claude`，输入 `/login` 登录一次。
3. 翻译模型选 Claude Haiku（最快，默认）、Claude Sonnet 或 Claude Opus（最准确但最慢）。设置 → 更多 → **Claude 路径**留空时自动查找：PATH、`%USERPROFILE%\.local\bin\claude.exe`、`%APPDATA%\npm\claude.cmd`（npm 安装的会改用 node 直接运行，以便传递多行提示词）。
4. 程序保持一个长驻的 `claude -p --input-format stream-json …` 进程，参数与环境变量与 macOS 版一致；每 40 句换新进程，新进程第一句带最近 6 句作为上下文。

**模型服务**（自己的 API 或本地模型）

设置 → 模型服务 → **管理…**（托盘菜单和状态条的模型菜单里也有“管理模型服务…”）。点 **添加模型服务** 选一个模板，填 API 密钥，点 **获取列表** 取得模型后删去不需要的（多个模型用逗号分隔），点 **测试** 用第一个模型翻译一句话并显示用时。服务里的模型随即出现在“翻译模型”中。删除服务时密钥一并删除，正在用它的翻译模型换回 Claude Haiku。

| 模板 | 接口格式 | 接口地址 | 密钥 |
|---|---|---|---|
| Anthropic | Anthropic | `https://api.anthropic.com` | 需要 |
| OpenAI | OpenAI 兼容 | `https://api.openai.com/v1` | 需要 |
| DeepSeek | OpenAI 兼容 | `https://api.deepseek.com/v1` | 需要 |
| 通义千问（兼容模式） | OpenAI 兼容 | `https://dashscope.aliyuncs.com/compatible-mode/v1` | 需要 |
| Gemini（OpenAI 兼容接口） | OpenAI 兼容 | `https://generativelanguage.googleapis.com/v1beta/openai` | 需要 |
| OpenRouter | OpenAI 兼容 | `https://openrouter.ai/api/v1` | 需要 |
| Ollama | OpenAI 兼容 | `http://localhost:11434/v1` | 不需要 |
| LM Studio | OpenAI 兼容 | `http://localhost:1234/v1` | 不需要 |
| 自定义 | 任选 | 任意兼容的地址 | 本机和局域网地址不需要 |

- OpenAI 兼容：`POST {接口地址}/chat/completions`，流式输出；模型列表来自 `GET {接口地址}/models`。推理模型（例如 DeepSeek R1）的思考内容不会显示，但会增加延迟，翻译建议选非推理模型。
- Anthropic：官方 C# SDK，`POST {接口地址}/v1/messages`；模型列表来自 `GET {接口地址}/v1/models`。低延迟设置：Haiku 关闭思考；Sonnet 与 Opus 不能关闭思考，改为 effort = low；模型名不是 `claude-` 开头时不加这些参数。
- 从之前的版本升级：原来的 Anthropic API 设置变成名为“Anthropic”的模型服务，原来的 OpenAI 兼容服务变成同名的模型服务，密钥和所选模型都保留。

所有模型都使用与 macOS 版相同的提示词、上下文格式（`[Context]…[Translate]`）和译文清理规则。

### 使用

- **托盘图标**：与 macOS 的菜单栏图标相同。字幕收起时，左键单击把字幕（和打开着的 AI 助手）显示出来；字幕显示着时，左键单击和右键一样打开菜单。菜单顺序与 macOS 相同：
  - 状态（麦克风权限被关闭时下面还有“打开麦克风设置”）；
  - 开始字幕 / 停止字幕、音源（麦克风 / 系统音频 / 麦克风和系统音频）、显示（双语 / 仅译文 / 仅原文）、翻译模型（最后一项是“管理模型服务…”）；
  - 收起字幕 / 显示字幕（右边是快捷键）、AI 助手、锁定字幕、字幕移回屏幕底部；
  - 字幕记录…、生成笔记、导出字幕（SRT / WebVTT）、开始新的记录、打开记录文件夹（已有记录文件时选中它）；
  - 设置…、退出。

  字幕收起时从菜单点“开始字幕”，字幕会显示出来。图标实心表示正在听。
- **设置**：布局与 macOS 版相同。“翻译”一组是语言、音源（麦克风 / 系统音频 / 两者）、麦克风降噪（音源不是“系统音频”时出现）、翻译模型、模型服务、过滤中文说话声；“字幕”一组是显示内容、字号（一个滑块同时调译文和原文）、背景、显示句数、大小；不常用的在默认收起的“更多”里：显示状态条、自动纠正口音、锁定字幕、记录文件夹、Claude 路径，以及 Windows 版特有的识别模型、显卡加速、实时预览、模型下载地址、快捷键。在托盘菜单或字幕上改了设置，打开着的设置窗口会跟着变。
- **快捷键 Win+Alt+L**（默认）：显示或隐藏字幕。可在设置 → 更多 → 快捷键 改成 Ctrl+Alt+L 或关闭。默认不用 Ctrl+Alt+L，是因为波兰语等键盘布局里 Ctrl+Alt 等同于 AltGr，会占用 AltGr+L 输入的字符（如“ł”）。
- **悬浮字幕**：按住拖动。窗口大小只随设置变化，出字幕时不会忽高忽低：新字幕在框里从下往上滚动，高度默认正好放下“显示句数”句（每句按一行原文、一行译文算），再加下面“正在说”的一行。拖左右边框改宽度；拖上下边框改成自己想要的高度；双击上下边框恢复按句数算的高度。在字幕上往上滚动可以翻看之前的字幕，这时右边出现细的位置条和“返回实时”；停在底部时自动跟随最新一句。
  - **快捷按钮**：鼠标移到字幕上，右上角出现四个按钮，与 macOS 相同：开始/停止、锁定、“⋯”（更多操作）、收起。
  - **“⋯”菜单**：显示（双语 / 仅译文 / 仅原文）、字幕记录…、显示 AI 助手（面板开着时是“隐藏 AI 助手”）、设置…。菜单开着时快捷按钮不会消失；打开菜单不会抢走正在用的程序的焦点，点到别的程序上时菜单关上。
  - **正在说**：还没分好句的内容显示在字幕下方固定的一行里（斜体、较淡）。超过一行时只显示最新的部分，前面是“…”，有空格的语言不会从单词中间截断。没有内容时这一行也留着，上面的字幕不会跳动。音源为“两者”、两边同时在说时是两行，各带音源标签；窗口高度不变，上面的字幕区让出一行。
- **锁定字幕**：字幕不再响应鼠标，可以点到下面的窗口。快捷按钮里的锁在没锁定时是开口的；锁定后鼠标移到字幕上，右上角出现一把闭合的锁，点它解锁，字幕的其他地方仍然点不到。也可以在托盘菜单里解锁。
- **音源“两者”**（设置里的“两者”，托盘菜单里的“麦克风和系统音频”）：与 macOS 版相同，同时识别麦克风和系统正在播放的声音，例如线上会议里自己说的和对方说的。
  - 每句原文前面有小标签“麦克风”或“系统音频”；“正在说”每个音源一行，也带标签。字幕记录窗口里标签在时间后面，记录文件里写成 `**[时间] 麦克风** 原文`。只用一个音源时没有标签。
  - 两路共用一个识别模型，语音段按先后识别；翻译跟不上时只合并同一音源的积压句子。状态条的音量取两路中较大的，两路都没有声音时才显示“无音频输入”。
  - 默认输出设备变了只重新开始系统音频，默认录音设备变了只重新开始麦克风。开始时一路打不开、中途断开或新设备打不开时，另一路照常工作，那一路每隔几秒重新打开；两路都打不开才算启动失败。
  - 外放声音时麦克风会把扬声器的声音再录一遍，可能出现重复的字幕：戴耳机，或打开“麦克风降噪”（含回声消除）。
- **麦克风降噪**（设置 → 翻译，音源为“麦克风”或“两者”时出现，默认关闭）：用 Windows 的通话语音处理打开麦克风，降低环境噪音并消除回声（与 macOS 版的系统语音处理相对应）。具体效果取决于声卡驱动，较新的 Windows 11 上还有“语音清晰度”。讲者离麦克风较远时可能反而降低识别效果。程序会让 Windows 不因此调低其他声音的音量；驱动不支持时照常录音，日志里有 `audio: noise reduction unavailable`。
- **麦克风权限**：Windows 隐私设置关闭了麦克风时，字幕不会停止，字幕下方提示去哪里打开，并有“打开麦克风设置”按钮；字幕锁定时托盘菜单里也有这一项。打开权限后几秒内自动继续。音源为“两者”时只有麦克风这一路等待，系统音频照常识别。
- **状态条**（字幕底部一小行）：音量、识别是否清晰、当前模型（名字后面有向下的小箭头，点击可切换或管理模型服务）、待翻译句数、最近的错误。
- **自动纠正口音**（设置 → 更多，默认关闭）：讲者口音重、识别不准时，让大模型按发音和上下文纠正识别错误，状态条显示“识别较难 · 已自动纠正”。关闭时只在状态条上显示“识别较难”，不改原文，也不换翻译会话。打开后下面出现“纠正时保留识别原文”，选中时记录里同时保留识别器原来的文字。
- **AI 助手**（托盘菜单 → AI 助手，或字幕上的“⋯”菜单 → 显示 AI 助手）：边听边提问，与 macOS 版相同。
  - 助手可以读取本次点“开始”以来的全部字幕。提问时才把上次提问以来新增的字幕和正在说的半句一起发给模型，不会每句都发。
  - 标题栏里选回答用的模型，分组与翻译模型相同，默认 Claude Sonnet；换模型后，下一问会把全部字幕重新带上。“新对话”清空问答；点“开始”和“开始新的记录”时也会开始新的对话，改设置引起的自动重启不会。
  - 下方是“总结最近的内容”“解释相关概念”“解释上一句”三个快捷问题；输入框按回车发送。回答支持粗体、斜体和行内代码，可以选中复制。
  - **存成文件**：在对话里说“把总结存成文件”“把要点整理到文件夹里”之类的话，助手会把内容写成 Markdown 文件，保存在记录文件夹下的“AI 助手”文件夹里（默认 `文档\LiveSub\AI 助手\`，英文界面下文件夹名为 `AI Assistant`；记录文件夹写不进去时放在 `%LOCALAPPDATA%\LiveSub\AI 助手\`）。文件名前面加日期和时间，同名时不会覆盖。回答里只显示“已保存：文件名”，点一下在资源管理器中选中这个文件。模型本身不能写文件，由 LiveSub 代为保存，而且只会存到这个文件夹。一次可以要求存多个文件。
  - 面板第一次打开时出现在字幕旁边：右边放不下就放左边；字幕在屏幕上半部分时顶边对齐，否则底边对齐。按住标题栏可以拖到别处，拖边框和四角可以改大小，位置和大小都会记住；字幕移动或改大小时面板不跟着动，“字幕移回屏幕底部”会把面板也放回字幕旁边。
  - 面板显示时不抢焦点，点输入框或对话内容时才接收键盘输入。收起字幕时面板一起隐藏，字幕再显示时一起回来；每次启动程序时面板是关着的。
  - 用 Claude Code 时，面板打开后（或面板开着时开始字幕）另有一个 claude 进程，退出程序时结束。
- **生成笔记**（托盘菜单或“字幕记录”窗口）：用 AI 助手的模型把本次全部字幕整理成 Markdown 笔记（总结、主要内容、关键术语、待办事项、可能识别错误的地方），保存在记录文件旁边，文件名为 `<时间> 笔记.md`（英文界面为 `<时间> Notes.md`），完成后用默认程序打开。生成过程中菜单项显示“正在生成笔记…”，进度和结果显示在托盘提示和菜单顶部。
- **字幕记录窗口**（“⋯”菜单或托盘菜单 → 字幕记录…）：与 macOS 版相同，列出本次全部字幕。每句上面一小行是时间（和音源、附注），下面是原文、纠正前的识别原文（打开“纠正时保留识别原文”并且改过时）和译文；文字可以选中复制。停在底部时跟着新字幕滚动，往上翻看时不打扰。下方显示当前状态，右边是“在资源管理器中显示记录”（打开文件夹并选中记录文件）和“生成笔记”。
- **记录**：每句实时写入 `文档\LiveSub\<时间> 字幕记录.md`，格式与 macOS 版相同（英文界面下文件名和文件头为英文：`<时间> Transcript.md`、“Started”“Language”和英文语言名）。没来得及翻译的句子在“开始新的记录”或退出时只写原文，不会丢句子。
- **导出字幕**：每句记录了它在音频里的起止时间（相对本次记录开始录音的时刻）；文字按当前显示内容（双语/仅译文/仅原文）。SRT 带 UTF-8 BOM，方便 Windows 播放器识别中文。
- **界面语言**：中文系统显示中文（简体），其他系统显示英文。测试时可以用环境变量 `LIVESUB_UI_LANGUAGE=en` 或 `zh-Hans` 指定。

### 隐私

- 语音识别完全在本机运行，音频不会上传。
- 识别出的文字会发送给你选择的翻译模型（Claude Code，或所选的模型服务）；使用 Ollama、LM Studio 时文字也留在本机。
- 向 AI 助手提问时，本次的字幕原文会发送给 AI 助手所选的模型；生成笔记时发送原文和译文。
- API 密钥用 Windows 数据保护（DPAPI，仅当前 Windows 用户可解密）加密，保存在 `%APPDATA%\LiveSub\secrets.bin`，不写入 `settings.json`，不写入日志。
- 设置：`%APPDATA%\LiveSub\settings.json`。日志：`%LOCALAPPDATA%\LiveSub\logs\livesub.log`，只记录事件和错误；设置环境变量 `LIVESUB_DEBUG_LATENCY=1` 时才会记录字幕文字（延迟分析用）。
- 记录文件保存在本机 `文档\LiveSub`，不会上传。

### 已知限制

- **字幕比 macOS 版晚**：Whisper 不是流式识别，要等讲者停顿约 0.6 秒（或一段满 8 秒）才出确认结果。“实时预览”会先显示正在说的内容，但会多用处理器；机器较慢时程序会自动跳过预览。
- **没有本地兜底翻译**：macOS 版有 Apple 本地翻译做兜底；Windows 版翻译模型出错时这一句显示“翻译失败”，状态条显示原因。
- **过滤中文说话声**：可选，Windows 版默认关闭（macOS 版默认开启）。打开后每段多做一次 Whisper 语言判断（只在“讲话语言 / 中文”之间判断），处理器开销约增加一半。
- **自动纠正口音**：打开后才纠正。用 Whisper 的逐词概率判断“难识别”，阈值沿用 macOS（最近 60 个词平均低于 0.80 进入；高于 0.86 并且连续一分钟才退出，免得来回切换），但 Whisper 的概率刻度不同，可能需要按实测调整（日志里有平均置信度）。
- 音源为“两者”、两路同时有语音时，识别的工作量加倍：机器较慢时字幕会更晚，实时预览也更少出现。
- “本地模型”一组包括局域网里其他电脑上的服务（macOS 版只算本机）；这些地址也不要求填密钥。
- 中途换默认输出设备或默认录音设备（插耳机、蓝牙）时会自动重新开始采集；新设备连续 3 次打不开时停止字幕并说明原因（音源为“两者”时不停止：另一路照常，打不开的那一路之后每隔几秒重试）。采集中途意外断开时字幕不停，每隔几秒重新打开。需要实测。
- 麦克风降噪做哪些处理（降噪、回声消除、语音清晰度）由声卡驱动和 Windows 版本决定，有的电脑上打开后没有明显变化。
- “显卡加速”需要用 `-p:LiveSubGpu=vulkan` 或 `cuda` 构建，改动在重新启动程序后生效。
- Claude Code 在 Windows 上可能要求安装 Git for Windows；未登录时字幕会提示登录。

### 计划中（v0.1 未包含）

- 字幕记录搜索。

### 测试清单

请在实体 Windows 电脑上逐项确认，记下结果（通过 / 不通过 + 现象）。日志在 `%LOCALAPPDATA%\LiveSub\logs\livesub.log`。

**安装与启动**

1. 按“构建与运行”构建并启动：托盘出现图标，屏幕底部居中出现字幕面板；面板不出现在任务栏和 Alt+Tab 里。
2. 再启动一次程序：不会出现第二个托盘图标，已运行的程序把字幕显示出来。
3. 打开设置，改几项后退出并重新启动：设置保留（`%APPDATA%\LiveSub\settings.json`）。

**识别模型**

4. 第一次点“开始字幕”：字幕面板显示下载进度，完成后开始识别；`%LOCALAPPDATA%\LiveSub\models\ggml-base.bin` 约 142 MB。
5. 下载过程中点“停止字幕”：下载取消，再次开始能重新下载。在设置 → 更多里换成 tiny / small，点“下载”，状态变为“已下载”。
6. （可选）填镜像地址后下载。

**音源**

7. 系统音频：播放一段英语视频，开始字幕，确认原文和译文出现，记录大致延迟（说完到译文出现）。
8. **系统静音时的系统音频**：分别试 (a) 音量调到 0，(b) 点静音按钮，(c) 视频播放器自身静音。哪种情况下还有字幕？
9. 系统 30 秒不播放任何声音后再播放：状态条显示“无声音”而不是“无音频输入”，之后的句子正常识别。
10. 播放中切换输出设备（插拔耳机、连接蓝牙耳机、在声音设置里换默认输出）：字幕继续，状态条提示“音频设备已切换”。在声音设置里改默认输出设备的格式（采样率）：几秒内字幕继续。
11. 麦克风：音源切到麦克风，对着麦克风说话，确认出字幕。
12. 麦克风权限：在“设置 › 隐私和安全性 › 麦克风”里关闭“允许桌面应用访问你的麦克风”，音源选麦克风后开始字幕。
    - 字幕不停止；几秒内字幕下方出现“麦克风权限已关闭……”（读不到隐私开关时是“……可能是麦克风权限已关闭……”）和“打开麦克风设置”按钮，状态条显示“麦克风无法使用”。点按钮打开设置的麦克风页。
    - 重新打开权限：提示在几秒内消失，对着麦克风说话能出字幕，不需要重新开始。
    - 锁定字幕时，托盘菜单里也有“打开麦克风设置”。字幕运行中途关闭权限，也出现同样的提示。
    - 请记下日志里 `audio: microphone privacy device=… apps=… desktop=…` 这一行（三个开关读到的值）。
13. 使用麦克风时切换默认录音设备：字幕继续。

**翻译模型**

14. Claude Code（已登录）：翻译模型选 Claude Haiku，开始字幕，状态条显示 “Claude Haiku”，译文逐字出现；任务管理器里只有一个常驻的 claude 进程（AI 助手面板关着时）。
15. Claude Code 未登录（或临时改名 claude.exe）：状态条和字幕给出“未登录 / 找不到 claude”的提示，不会卡住。
16. （如果用 npm 安装）`claude.cmd` 能被找到并正常翻译；设置里显示找到的路径。
17. Anthropic API：设置 → 模型服务 → 管理… → 添加模型服务 → Anthropic，填密钥，点“测试”显示“claude-haiku-5-5 可用，用时 … 秒：…”；在翻译模型里分别选“Anthropic · claude-haiku-5-5”和“Anthropic · claude-sonnet-5-5”开始字幕；填错密钥时测试提示“API 密钥无效”。
18. OpenAI 兼容服务：对手上有的服务（DeepSeek、通义千问、OpenAI、Gemini、OpenRouter 任选），添加模板 → 填密钥 → 获取列表（提示“获取到 N 个模型”，已填的模型排在前面）→ 删去不需要的模型 → 测试 → 在翻译模型的“云端模型”里选它 → 开始字幕，确认译文逐字出现。删除正在使用的服务：翻译模型换回 Claude Haiku。
19. Ollama 或 LM Studio（本机）：密钥框提示“本地服务无需填写”，服务标题带“· 本地”，模型出现在“本地模型”一组；获取列表、测试、开始字幕都正常。
20. 运行中从托盘菜单和状态条（点模型名或后面的小箭头）切换模型：下一句起使用新模型。两个菜单的最后都有“管理模型服务…”，打开的是同一个窗口（已打开时提到前面）。
21. 翻译 40 句以上：Claude Code 进程会换新的一个（旧进程退出，不会越积越多）。

**悬浮字幕**

22. 拖动字幕到其他位置，重新启动后位置保留；“字幕移回屏幕底部”有效。
23. 拖左右边框改宽度；拖上下边框改高度，重新启动后保留；双击上下边框恢复按“显示句数”算的高度；四个角都能拖。在字幕上往上滚动，出现细的位置条和“返回实时”，点击回到最新。按住字幕文字也能拖动窗口。
24. 不抢焦点：在记事本里打字时点击、拖动字幕，记事本仍保持输入焦点。
25. 锁定：没锁定时快捷按钮里的锁是开口的。锁定后鼠标可以点到字幕下面的窗口，快捷按钮不再出现；鼠标移到字幕上时右上角出现闭合的锁，移开后消失。点这把锁能解锁，点字幕其他地方仍然点到下面的窗口；托盘菜单里也能解锁。
26. 快捷键 Win+Alt+L 显示/收起字幕；在设置 → 更多里改成 Ctrl+Alt+L 或“无”后立即生效；快捷按钮里的“收起”有效，鼠标停在上面时提示里是当前的快捷键（选“无”时提示在托盘菜单中重新显示）。（波兰语等布局下，选 Ctrl+Alt+L 时 AltGr+L 不能再输入“ł”，这是预期的。）
27. 全屏：浏览器全屏播放视频（F11 或视频全屏按钮）时字幕仍在最上层。
28. 大小固定：讲得快、句子长短不一时，字幕窗口的高度一直不变，新句在框里往上滚，出现时有淡入；改字号、显示句数、显示内容时高度跟着变，底边不动。
29. 显示内容（双语 / 仅译文 / 仅原文）、字号（译文和原文一起变）、背景、显示句数、显示状态条都即时生效。
30. 讲得快、翻译跟不上时，状态条显示“待翻译 N 句”，积压的句子合并翻译，延迟不会越来越长。
31. 有口音或嘈杂的讲者：“自动纠正口音”关闭（默认）时，状态条可能变为“识别较难”，翻译不受影响；在设置 → 更多中打开后变为“识别较难 · 已自动纠正”，识别错的词被纠正，切换时没有“翻译失败”。记下日志里 `recognition difficult/clear (avg confidence …)` 的数值，用来校准阈值。
32. 实时预览：讲话过程中，字幕下方固定的一行里出现斜体的“正在说”；超过一行时只显示最新的部分，前面是“…”，英文不会从单词中间截断，中文从字之间截断。这一行出现、变长、消失时，窗口高度和上面的字幕都不动；没拖过上下边框时，窗口默认高度和之前的版本相同。关闭“实时预览”后不再出现。
33. 过滤中文说话声：讲话语言为英语时打开此项，播放英语视频的同时有人说中文，中文部分不出字幕，状态显示“已过滤中文说话 N 段”。

**记录与导出**

34. `文档\LiveSub\<时间> 字幕记录.md` 实时写入，格式与 macOS 版相同；“开始新的记录”后新建文件；讲话过程中直接退出或开始新的记录，最后几句（含未翻译的原文）也在记录里。英文界面下文件名为 `<时间> Transcript.md`，文件头为英文。
35. 导出 SRT 和 WebVTT：用 VLC 或系统“媒体播放器”配合同一段视频（或同时录的屏幕录像）播放，时间轴基本对齐，中文不乱码。
36. 字幕记录窗口（“⋯”菜单或托盘菜单 → 字幕记录…）：
    - 标题为“实时翻译助手 · 字幕记录”（英文界面 “LiveSub · Transcript”）。列出本次全部字幕：每句上面是小字的时间，下面是原文和稍大的译文；还没翻译出来的句子先显示“…”，翻译失败的句子只有原文。打开“纠正时保留识别原文”并发生纠正时，多一行小字“识别原文：…”。
    - 文字可以选中、复制；鼠标停在文字上也能用滚轮滚动。往上翻看时新字幕不会把列表拉回底部，滚回底部后继续跟随。
    - 底部左边是当前状态。“在资源管理器中显示记录”打开记录文件夹并选中记录文件（还没开始过时没有这个按钮）。“生成笔记”在没有字幕时不可点，生成过程中显示“正在生成…”且不可点。

**界面语言与其他**

37. 中文 Windows 上界面为中文、程序名“实时翻译助手”；设置环境变量 `LIVESUB_UI_LANGUAGE=en` 后启动为英文、程序名 “LiveSub”。检查有没有文字被截断。
38. 用测试音频跑完整链路：`$env:LIVESUB_TEST_AUDIO_FILE="C:\path\talk.wav"; $env:LIVESUB_AUTOSTART="1"; .\LiveSub.exe`。
39. 连续运行 1 小时：在任务管理器里记下内存与处理器占用，确认没有持续增长。
40. 退出：托盘图标消失，任务管理器里没有残留的 LiveSub、claude 或 node 进程。
41. 日志里没有 API 密钥。
42. （可选）用 `-p:LiveSubGpu=vulkan` 构建并打开“显卡加速”：日志 `whisper: loaded … runtime …` 显示使用的运行库，比较识别速度。

**设置与模型服务（新）**

43. 从之前的版本升级：保留之前版本的 `%APPDATA%\LiveSub\settings.json` 和 `secrets.bin` 启动新版。原来选的翻译服务和模型仍被选中；原来的 Anthropic API 变成“Anthropic”服务，OpenAI 兼容服务变成同名的服务，点“测试”都能通过（密钥还在）；日志里有 `settings: migrated …`。再重启一次，服务不会多出来。
44. 设置窗口：分为“翻译”“字幕”和默认收起的“更多”。音源有“麦克风”“系统音频”“两者”三项，选“系统音频”时“麦克风降噪”隐藏；讲话语言选普通话时“过滤中文说话声”隐藏；拖过字幕上下边框后“显示最近 N 句”隐藏，点“大小”一行的“恢复默认”后重新出现；翻译模型的“云端模型”里总是列出 Claude Haiku、Claude Sonnet、Claude Opus，没有单独的 Opus 开关；“纠正时保留识别原文”只在打开“自动纠正口音”时出现。设置窗口开着时，从托盘菜单或状态条换模型、锁定字幕，窗口里跟着变。英文界面下长标签折行，不被截断。

**AI 助手与笔记（新）**

45. 打开面板：托盘菜单 → **AI 助手**，字幕旁边出现面板，菜单项打上勾。
    - 位置：在字幕右边，间距约 8 像素；右边放不下时在左边。字幕在屏幕下半部分时底边对齐，在上半部分时顶边对齐。
    - 面板不出现在任务栏和 Alt+Tab 里。在记事本里打字时打开面板、点标题栏、快捷问题、模型选择，记事本仍保持输入焦点。
    - 点输入框后可以打字（包括中文输入法），按回车发送；输入法组字时按回车只确认文字，不发送。
    - 字幕隐藏着时点托盘菜单的“AI 助手”，字幕和面板一起出现。
46. 提问：开始字幕，说几句后点“总结最近的内容”。
    - 先显示“正在回答…”和转圈，回答逐字出现；粗体和行内代码正常显示；回答期间快捷问题和发送按钮不可点。回答可以选中、复制。对话变长时自动滚到底部，滚轮能上下翻看。
    - 问一个和前面字幕有关的问题，回答能对上。在标题栏换一个模型（例如 Claude Haiku 或某个模型服务）再问，回答仍然知道前面的字幕。
    - 点“新对话”后问答清空。停止再开始字幕，或“开始新的记录”后，问答也清空，新的回答只涉及之后的字幕；在设置里换翻译模型等引起的自动重启不清空问答。
    - 把 claude.exe 临时改名，或把正在用的模型服务密钥改错：回答处显示橙色的错误说明，不会卡住。改回来后再问能正常回答。
47. 面板位置与显示：
    - 按住标题栏拖动面板，按在图标和“AI 助手”几个字上也能拖。拖四边和四角改大小，最小约 280×220。重新启动程序后再打开面板，位置和大小保留。
    - 拖动字幕、改字幕大小时面板不动。“字幕移回屏幕底部”后面板回到字幕旁边。
    - 收起字幕（快捷按钮、快捷键、托盘菜单）时面板一起隐藏，再显示字幕（托盘左键、快捷键）时面板一起回来。标题栏的“收起”和 Alt+F4 关闭面板，托盘菜单里的勾去掉。重新启动程序后面板是关着的。
    - 用 Claude Code 时，打开面板（或面板开着时开始字幕）后任务管理器里多一个 claude 进程；从没打开过面板时没有。
48. 生成笔记：
    - 没有字幕时，托盘菜单的“生成笔记”不可点。
    - 有字幕后点它：菜单项变为“正在生成笔记…”且不可点，托盘提示显示“正在用 Claude Sonnet 生成笔记…”（AI 助手所选的模型）。完成后记录文件旁边出现 `<时间> 笔记.md`（英文界面为 `<时间> Notes.md`）并自动打开，托盘提示显示“笔记已保存：…”。
    - 模型不可用时显示“生成笔记失败：…”或原因。

**快捷按钮、菜单和托盘图标（新）**

49. 快捷按钮与“⋯”菜单：
    - 鼠标移到字幕上，右上角出现四个按钮：开始/停止（提示“开始”或“停止”）、开口的锁（“锁定字幕”）、“⋯”（“更多操作”）、收起（“收起字幕。按 Win+Alt+L 重新显示。”）。没有别的按钮。
    - 点“⋯”，按钮下方出现菜单：显示 ▸（双语 / 仅译文 / 仅原文，当前的打勾）、字幕记录…、显示 AI 助手（面板开着时是“隐藏 AI 助手”）、设置…。每一项都有效。
    - 菜单开着时把鼠标移出字幕，快捷按钮不消失；菜单关上后，鼠标不在字幕上时按钮消失。
    - 在记事本里打字时点“⋯”，在菜单里换显示内容，再打字仍进记事本。
    - 菜单开着时点桌面、任务栏或其他程序的窗口，菜单关上。状态条上的模型菜单、AI 助手标题栏的模型菜单也一样。
50. 托盘图标与菜单：
    - 字幕显示着时左键单击托盘图标，弹出菜单（和右键相同）；菜单开着时再点一次图标，菜单关上，不会马上又弹出。字幕收起时左键单击，字幕出现（AI 助手开着时一起出现），不弹菜单。右键总是弹出菜单。点菜单外面时菜单关上。
    - 菜单顺序：状态 →（麦克风被挡住时）打开麦克风设置 → 开始字幕、音源、显示、翻译模型 → 收起字幕（右边是快捷键）、AI 助手、锁定字幕、字幕移回屏幕底部 → 字幕记录…、生成笔记、导出字幕、开始新的记录、打开记录文件夹 → 设置…、退出。
    - 字幕收起时从菜单点“开始字幕”：字幕显示出来并开始。
    - 开始过字幕后点“打开记录文件夹”：资源管理器打开记录文件夹并选中当前的记录文件。

**音源“两者”与麦克风降噪（新）**

51. 两者：设置里音源选“两者”（托盘菜单的音源里是“麦克风和系统音频”，打上勾），播放英语视频的同时对着麦克风说话。
    - 两边的话都出字幕，原文前面有小标签“麦克风”或“系统音频”（小号字、半透明的圆角底）；只选一个音源时没有标签。
    - 两边同时说话时，“正在说”有两行，各带标签；字幕窗口的高度不变。实时预览两边都会出现。
    - 字幕记录窗口里每句时间后面有音源；记录文件里写成 `**[时间] 麦克风** 原文`。
    - 状态条：一边安静、另一边有声音时显示正在接收，不显示“无音频输入”；状态里是“正在听：麦克风和系统音频 · …”。
    - 讲得快、积压合并时，不同音源的句子不会合并成一句。
    - 记下两边都有语音时的处理器占用和延迟，与只用一个音源时比较。
52. 两者模式下的设备和权限：
    - 运行中换默认输出设备：只有系统音频重新开始（日志 `audio: default device changed, restarting loopback capture`），麦克风的字幕不中断。换默认录音设备：只重开麦克风（`restarting microphone capture`）。
    - 关闭“允许桌面应用访问你的麦克风”后开始字幕：系统音频照常出字幕；字幕下方出现麦克风权限提示和“打开麦克风设置”按钮，托盘菜单的状态是“系统音频照常识别；麦克风等待权限，打开后自动继续”。打开权限后几秒内麦克风自动继续，不需要重新开始。
    - 没有麦克风（或拔掉麦克风）时用“两者”：系统音频照常出字幕，状态条短暂提示麦克风的错误，之后每隔几秒重试，不会停止字幕。
53. 麦克风降噪：音源选“麦克风”或“两者”时，设置的“翻译”一组里音源下面出现“麦克风降噪”，鼠标停在上面有说明；选“系统音频”时隐藏。
    - 打开后字幕自动重新开始，日志有 `audio: microphone (noise reduction) … Hz`；驱动不支持时有 `audio: noise reduction unavailable, capturing without it`，字幕照常。
    - 在有风扇、键盘声的环境里对比打开前后的识别效果；讲者离麦克风较远时也比较一次。
    - “控制面板 › 声音 › 通信”保持默认设置（检测到通信活动时降低其他声音的音量），音源选“两者”并打开降噪：播放的视频音量不会被调低，系统音频的字幕照常。日志里没有 `audio: cannot opt out of ducking`。
    - 外放视频（不戴耳机）、音源为“两者”时，比较降噪打开前后“麦克风”标签的重复字幕是否减少。

**AI 助手存文件（新）**

54. 存文件：开始字幕，说几句后在 AI 助手里输入“把总结存成文件”。
    - 先显示“正在回答…”；助手开始写文件后显示“正在保存文件…”和转圈，面板里不出现文件内容。完成后回答下面有一行蓝色的“已保存：2026-10-21 21.32 总结.md”之类的文字（前面是文档图标），块外的回答最多一句话。
    - 鼠标停在这一行上，指针是手形，提示“在资源管理器中显示”；点它打开资源管理器并选中这个文件。文件在 `文档\LiveSub\AI 助手\`（英文界面为 `AI Assistant`），用记事本打开是完整的 Markdown，中文不乱码。
    - 再要求存一个同名的文件：生成“… 2.md”，原来的文件不变。要求“分成两个文件存”：出现两行“已保存”。普通提问（没提保存）不生成文件。
    - 设置里把记录文件夹换成没有写权限的文件夹（例如 `C:\Windows`）再存一次：文件存到 `%LOCALAPPDATA%\LiveSub\AI 助手\`，仍显示“已保存”，日志里有 `assistant: cannot write to the transcript folder`。测完把记录文件夹改回来。
    - 在标题栏换成某个模型服务（或本地模型）再试一次，同样能存。助手正在写文件（显示“正在保存文件…”）时点“新对话”：问答清空，不报错，这个文件不保存。
    - 日志里有 `assistant: saved 1/1 files` 之类的一行（只有个数）。
55. 系统对话框不被挡住：字幕和 AI 助手都开着，并且把助手拖到屏幕中间。依次打开 设置 › 更多 › 记录文件夹 › 更改…、Claude 路径 › 浏览…、托盘 › 导出字幕 › SRT…：对话框显示在字幕和 AI 助手上面，按钮不被挡住，等几秒也不会被盖住；关掉对话框后，字幕和助手仍然在其他窗口上面。

---

## English

LiveSub listens to **what your PC is playing** (videos, online classes, meetings), **your microphone** (lectures, conversations), or both at once, transcribes it locally, translates it with the model of your choice, and shows **floating bilingual subtitles**. It also saves a live transcript and exports SRT / WebVTT. While listening you can ask the **AI assistant** about what was said, and afterwards turn the subtitles into **notes**.

| Stage | Implementation | Notes |
|---|---|---|
| System audio | WASAPI loopback (NAudio) | No virtual audio device needed |
| Microphone | WASAPI capture (NAudio) | Capture restarts when the default recording device changes; optional noise reduction (Windows communications processing) |
| Speech recognition | Whisper.net (whisper.cpp), local | Model downloaded on first use; audio never leaves the PC |
| Translation | Claude Code CLI, or model services you add (Anthropic, OpenAI-compatible APIs, local Ollama / LM Studio) | Switch any time |
| UI | WPF overlay + tray icon | Topmost, never steals focus, optional click-through |

### Requirements

- Windows 10 or 11, 64-bit (x64).
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64), unless you use a self-contained build.
- Recognition runs on the CPU; AVX2 CPUs are faster. The `base` model keeps up with normal speech on recent laptops.
- Internet access on first use to download the speech model (tiny ≈ 75 MB, base ≈ 142 MB, small ≈ 466 MB).
- One translation model: a signed-in Claude Code CLI; your own API key for Anthropic or an OpenAI-compatible service (OpenAI, DeepSeek, Qwen, Gemini, OpenRouter, …); or a local Ollama / LM Studio.

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
2. Right-click the tray icon → **Settings…** and choose a **Translation Model**. Claude Code needs nothing else; for your own API or a local model, click **Manage…** next to Model Services, add the service and click **Test** (below).
3. Right-click → **Start Subtitles**. The first start downloads the speech model with progress shown in the overlay, into `%LOCALAPPDATA%\LiveSub\models`. If huggingface.co is unreachable, set a mirror under Settings → More → **Model Download Host**, or copy `ggml-base.bin` into that folder.

### Translation models and model services

As on macOS, Settings → **Translation Model** lists **Cloud Models** (Claude Code and your own APIs) and **Local Models** (services on this PC or the local network). The tray menu and the status bar (click the model name) switch models too.

**Claude Code CLI** — uses your Claude Code subscription, exactly like the macOS app. Install Claude Code (`irm https://claude.ai/install.ps1 | iex` or npm), run `claude` once and `/login`. Choose Claude Haiku (fastest, default), Claude Sonnet or Claude Opus (most accurate, slowest). With **Claude Path** (Settings → More) empty, LiveSub finds `claude` on PATH, in `%USERPROFILE%\.local\bin\claude.exe` or `%APPDATA%\npm\claude.cmd` (npm shims are run through node directly so the multi-line system prompt survives). One long-lived `claude -p --input-format stream-json …` process with the same arguments and environment as macOS; a fresh process every 40 lines, seeded with the last 6 lines.

**Model services** — Settings → Model Services → **Manage…** (also **Manage Model Services…** at the end of the tray and status-bar model menus). **Add Model Service** offers templates: Anthropic (Anthropic format, `https://api.anthropic.com`), OpenAI `https://api.openai.com/v1`, DeepSeek `https://api.deepseek.com/v1`, Qwen (DashScope compatible mode) `https://dashscope.aliyuncs.com/compatible-mode/v1`, Gemini `https://generativelanguage.googleapis.com/v1beta/openai`, OpenRouter `https://openrouter.ai/api/v1`, Ollama `http://localhost:11434/v1`, LM Studio `http://localhost:1234/v1`, or Custom. Enter the key (local and LAN addresses need none), **Fetch List** and remove the models you don't need (comma-separated), then **Test** translates one sentence with the first model and shows the time. The models then appear under Translation Model. Deleting a service also deletes its key; a translation model that used it switches back to Claude Haiku.

- OpenAI-compatible: `POST {base}/chat/completions` (streaming), models from `GET {base}/models`. Reasoning models add latency; prefer non-reasoning models for subtitles.
- Anthropic: the official C# SDK, `POST {base}/v1/messages`, models from `GET {base}/v1/models`. For low latency, Haiku runs with thinking disabled; Sonnet and Opus cannot disable thinking, so they run with effort = low; models not named `claude-…` get neither setting.
- Upgrading from an earlier build: the Anthropic API settings become a model service named “Anthropic”, and each OpenAI-compatible service becomes a model service with the same name; keys and the selected model are kept.

All models share the macOS prompts, the `[Context]…[Translate]` context format and the output sanitizer.

### Usage

- **Tray icon**, like the macOS menu bar icon: when the subtitles are hidden, a left click shows them (and the AI assistant if it was open); otherwise a left click opens the menu, as a right click always does. The menu follows the macOS order: status (plus Open Microphone Settings when the microphone is blocked); Start/Stop Subtitles, Audio Source (Microphone / System Audio / Microphone and System Audio), Display, Translation Model (ending with Manage Model Services…); Hide/Show Subtitles (with the shortcut), AI Assistant, Lock Subtitles, Move Subtitles to Bottom; Transcript…, Generate Notes, Export Subtitles (SRT / WebVTT), New Transcript, Open Transcript Folder (selects the transcript file once there is one); Settings…, Quit. Start Subtitles also shows hidden subtitles. A filled icon means LiveSub is listening.
- **Settings** follow the macOS layout: **Translation** (language, audio source — Microphone / System Audio / Both, Microphone Noise Reduction when the source is not System Audio, translation model, model services, Filter Chinese Speech), **Subtitles** (display, one font-size slider for both lines, background, lines shown, size), and a collapsed **More** (status bar, Correct Accented Speech Automatically, lock, transcript folder, Claude path, plus the Windows-only speech model, GPU acceleration, live preview, model download host and shortcut). An open Settings window follows changes made from the tray or the overlay.
- **Win+Alt+L** (default) shows or hides the subtitles; change it to Ctrl+Alt+L or turn it off under Settings → More → Shortcut. Ctrl+Alt+L is not the default because Ctrl+Alt equals AltGr on layouts such as Polish, where it would block characters like “ł”.
- **Overlay**: drag to move. The window size changes only with settings, never with the text: new lines scroll up inside it, and the default height fits “Lines Shown” lines (one source line plus one translation line each) plus the live line below them. Drag the left/right edges to change the width and the top/bottom edges to set your own height; double-click the top/bottom edge to go back to the height from Lines Shown. Scroll up to read earlier lines; a thin position bar and “Back to Live” appear.
  - **Quick buttons** (on hover), as on macOS: Start/Stop, Lock, “⋯” (More) and Hide.
  - **“⋯” menu**: Display (Bilingual / Translation Only / Original Only), Transcript…, Show AI Assistant (Hide AI Assistant while it is open) and Settings…. The quick buttons stay while the menu is open; opening it does not take focus from the app you are using, and clicking another app closes it.
  - **Live line**: speech that is not yet a full sentence appears in one fixed italic line below the subtitles. When it is longer than the line, only the newest words are shown after a leading “…” (spaced languages are cut between words). The line keeps its height when empty, so the subtitles above never jump. With the audio source set to Both and both sides speaking, there are two lines, each with its source tag; the window keeps its height and the subtitle area above gives up one line.
- **Lock** makes the overlay click-through. The lock button shows an open lock while unlocked; when locked, hovering over the overlay shows a closed lock in the top-right corner. Click it to unlock; the rest of the overlay stays click-through. The tray menu can also unlock.
- **Audio source “Both”** (“Microphone and System Audio” in the tray menu), as on macOS: the microphone and what the PC is playing are recognized at the same time, for example your own voice and the other side of an online meeting. Each source line starts with a small “Microphone” or “System Audio” tag, and the live line has one row per source with its tag; the Transcript window shows the source after the time, and the transcript file writes `**[time] Microphone** text`. With a single source there are no tags. Both sources share one speech model and their segments are recognized in arrival order; backlog merging only joins lines from the same source. The status-bar level is the louder of the two, and “No audio input” appears only when both are silent. A default output change restarts only system audio, a default recording change only the microphone; if one source cannot be opened at Start, drops later or its new device cannot be opened, the other keeps running and the failed one is reopened every few seconds; Start fails only when neither opens. When audio plays through speakers the microphone picks it up again and may produce duplicate lines: use headphones or turn on Microphone Noise Reduction (which includes echo cancellation).
- **Microphone Noise Reduction** (Settings → Translation, shown when the source is Microphone or Both, off by default): opens the microphone with Windows communications voice processing to reduce background noise and remove echo (the counterpart of the macOS system voice processing). The effect depends on the audio driver; recent Windows 11 builds add Voice Clarity. Accuracy may drop when the speaker is far from the microphone. LiveSub tells Windows not to turn other sounds down because of it; when the driver does not support it, capture continues normally and the log says `audio: noise reduction unavailable`.
- **Microphone access**: when Windows privacy settings block the microphone, subtitles keep running and a notice above the status bar says where to turn access on, with an “Open Microphone Settings” button (also in the tray menu, for a locked overlay). Capture resumes within seconds after access is turned on. With the source set to Both, only the microphone waits; system audio keeps being recognized.
- **Status bar**: input level, recognition state, current model (with a small down arrow; click to switch or manage model services), lines waiting for translation, last error.
- **Correct Accented Speech Automatically** (Settings → More, off by default): when recognition is unreliable, the model fixes misheard words from pronunciation and context, and the status bar shows “Hard to recognize · Auto-correcting”. When off, the status bar only shows “Hard to recognize”; the text and the translation session stay as they are. **Keep Original Text When Correcting** appears once it is on.
- **AI Assistant** (tray menu → AI Assistant, or Show AI Assistant in the overlay's “⋯” menu), as on macOS: ask questions while listening. The assistant can read every subtitle since you pressed Start; the lines that are new since the last question, plus the sentence still being spoken, are sent only when you ask (never per line). Pick the answer model in the panel header (same groups as Translation Model, Claude Sonnet by default); after switching, the next question carries the whole transcript again. **New Conversation** clears the answers; Start and New Transcript also start a new conversation, automatic restarts after a settings change do not. Three quick questions sit above the input box; Enter sends. Answers show bold, italic and inline code and can be selected and copied. **Saving files**: say something like “save the summary to a file” or “organize the key points into a folder” and the assistant writes Markdown files into the “AI Assistant” subfolder of the transcript folder (`Documents\LiveSub\AI Assistant\` by default, `AI 助手` under the Chinese UI; `%LOCALAPPDATA%\LiveSub\AI Assistant\` when the transcript folder is not writable), named with the date and time and never overwriting an existing file. The answer only shows “Saved: file name”; click it to select the file in File Explorer. The model cannot write files itself: LiveSub saves them, and only into that folder. You can ask for several files at once. The panel first opens beside the overlay (right side, else left; top-aligned when the overlay is in the upper half of the screen, else bottom-aligned); drag the header to move it and its edges or corners to resize it — position and size are remembered, it does not follow the overlay, and Move Subtitles to Bottom puts it back beside the overlay. It never steals focus when shown; clicking the input box or the conversation lets you type. It hides and returns together with the overlay and is closed at every launch. With Claude Code it uses a second claude process, started when the panel opens (or when you start subtitles with the panel open).
- **Generate Notes** (tray menu or the Transcript window): the assistant's model turns all subtitles of this session into Markdown notes (summary, main points, key terms, to-dos, likely recognition errors), saved next to the transcript as `<time> 笔记.md` (`<time> Notes.md` under the English UI) and opened with the default app. While it runs the menu item reads “Generating Notes…”; progress and the result appear in the tray tooltip and at the top of the menu.
- **Transcript window** (“⋯” menu or tray menu → Transcript…), as on macOS: every line of this session with its time (and source and note), the source text, the recognizer's original text when a correction changed it (with Keep Original Text When Correcting on) and the translation; text can be selected and copied. It follows new lines only while scrolled to the bottom. The bar at the bottom shows the status, **Show Transcript in File Explorer** (opens the folder with the file selected) and **Generate Notes**.
- **Transcripts**: written live to `Documents\LiveSub\<time> 字幕记录.md` (same format as macOS; under the English UI the file is `<time> Transcript.md` with “Started”, “Language” and English language names). Lines still waiting for translation are written source-only on New Transcript or Quit. **Export** SRT / WebVTT uses the audio times of each line; text follows the current display mode.
- **UI language**: Chinese (Simplified) on Chinese Windows, English otherwise. Override with `LIVESUB_UI_LANGUAGE=en` or `zh-Hans`.

### Privacy

- Speech recognition runs entirely on your PC; audio is never uploaded.
- Recognized text is sent to the translation model you choose; with Ollama or LM Studio it stays on your PC.
- When you ask the AI assistant, this session's source text is sent to the assistant's model; Generate Notes sends source text and translations.
- API keys are encrypted with Windows Data Protection (DPAPI, current user only) in `%APPDATA%\LiveSub\secrets.bin`, never written to `settings.json` or the log.
- Settings: `%APPDATA%\LiveSub\settings.json`. Log: `%LOCALAPPDATA%\LiveSub\logs\livesub.log` (events and errors; subtitle text only when `LIVESUB_DEBUG_LATENCY=1`).

### Known limitations

- Subtitles arrive later than on macOS: Whisper is not streaming, so a segment is recognized after a ~0.6 s pause or 8 s of continuous speech. Live Preview shows a draft of the current segment at extra CPU cost and is skipped automatically on slow machines.
- No offline fallback translator (macOS has Apple Translation). When the service fails, the line shows “Translation failed” and the status bar shows why.
- Filter Chinese Speech is optional and off by default on Windows (on by default on macOS); it adds one Whisper language check per segment (limited to the spoken language vs. Chinese).
- Auto-correction runs only when turned on. It uses Whisper token probabilities with the macOS thresholds (enter when the last 60 words average below 0.80; leave only after staying above 0.86 for a full minute, to avoid flip-flopping); Whisper's scale differs, so the thresholds may need calibration (the log records the average confidence).
- With the source set to Both and speech on both sides, recognition does twice the work: on slower PCs subtitles arrive later and live previews appear less often.
- **Local Models** also include services on other computers in the local network (macOS counts only this computer); they need no key either.
- When the default output or recording device changes, capture restarts automatically; if the new device fails to open three times, subtitles stop with the reason shown (with the source set to Both they keep running: the other source continues and the failed one is retried every few seconds). If capture drops unexpectedly, subtitles keep running and capture is reopened every few seconds.
- Which processing Microphone Noise Reduction applies (noise suppression, echo cancellation, Voice Clarity) depends on the audio driver and Windows version; on some PCs it makes little difference.
- GPU acceleration requires a `-p:LiveSubGpu=vulkan` or `cuda` build and takes effect after restart.
- Claude Code on Windows may require Git for Windows.

### Planned (not in v0.1)

Transcript search.

### Test checklist

Record pass / fail and what you saw. The log is at `%LOCALAPPDATA%\LiveSub\logs\livesub.log`.

1. Build and launch: tray icon appears; overlay at the bottom center; overlay is not in the taskbar or Alt+Tab.
2. Launch a second time: no second tray icon; the running instance shows the overlay.
3. Change settings, quit, relaunch: settings persist.
4. First Start downloads the model with progress; `ggml-base.bin` ≈ 142 MB lands in `%LOCALAPPDATA%\LiveSub\models`.
5. Stop during download cancels it; Start again resumes from scratch. Download tiny/small from Settings → More.
6. (Optional) Download through a mirror host.
7. System audio: play an English video and confirm subtitles and translations; note the latency.
8. **System audio while muted**: try (a) volume 0, (b) the mute button, (c) muting inside the player. Which cases still produce subtitles?
9. Silence for 30 s, then play again: status shows “Silent”, not “No audio input”; recognition resumes.
10. Switch the output device while playing (headphones, Bluetooth, default device): subtitles continue; “Audio device changed” appears. Change the default output device's format (sample rate) in Sound settings: subtitles continue within seconds.
11. Microphone: switch the source and speak.
12. Turn off “Let desktop apps access your microphone” (Settings › Privacy & security › Microphone), choose the microphone and Start: subtitles keep running; within seconds the notice “Microphone access is turned off…” (or “…may be turned off…” when the privacy switches cannot be read) and an “Open Microphone Settings” button appear, and the status bar shows “Microphone unavailable”. The button opens the Settings page. Turning access back on clears the notice within seconds and speech is recognized without restarting. The tray menu has the same item while the overlay is locked. Turning access off while running shows the same notice. Note the `audio: microphone privacy device=… apps=… desktop=…` line in the log.
13. Change the default recording device while using the microphone: subtitles continue.
14. Claude Code (signed in), Translation Model Claude Haiku: status bar shows “Claude Haiku”; text streams; only one resident claude process (with the AI Assistant panel closed).
15. Claude Code signed out or missing: a clear message, no hang.
16. (npm install) `claude.cmd` is found and works; Settings shows the path.
17. Anthropic API: Settings → Model Services → Manage… → Add Model Service → Anthropic, enter the key; Test shows “claude-haiku-5-5 works, … s: …”; translate with “Anthropic · claude-haiku-5-5” and “Anthropic · claude-sonnet-5-5”; a wrong key shows “The API key was rejected”.
18. OpenAI-compatible: for each service you have, add template → key → Fetch List (“Found N models”, models already listed stay first) → remove unneeded models → Test → pick it under Cloud Models → Start; text streams. Deleting the service in use switches back to Claude Haiku.
19. Ollama or LM Studio: the key box says “Not needed for local services”, the title ends with “· Local”, models appear under Local Models; fetch, test and start work.
20. Switch models from the tray menu and from the status bar (the model name or the small arrow after it) while running. Both menus end with “Manage Model Services…”, which opens (or brings forward) the same window.
21. After 40+ lines the Claude process is replaced (old one exits; processes do not pile up).
22. Drag the overlay; position persists across restarts; “Move Subtitles to Bottom” works.
23. Resize from all edges and corners; a dragged height persists across restarts; double-click the top/bottom edge restores the Lines Shown height; scrolling up shows the position bar and “Back to Live”; dragging on the text moves the window.
24. No focus stealing: type in Notepad while clicking/dragging the overlay.
25. Lock: the lock button shows an open lock while unlocked; when locked, clicks pass through, quick buttons are hidden, and hovering shows a closed lock in the top-right corner that disappears when the mouse leaves. Clicking the lock unlocks; clicking elsewhere on the overlay still reaches the window below; the tray menu also unlocks.
26. Win+Alt+L toggles the overlay; switching to Ctrl+Alt+L or None in Settings → More takes effect immediately; the Hide quick button works and its tooltip names the current shortcut (or the tray menu when there is none).
27. Fullscreen browser video: the overlay stays on top.
28. Fixed size: with fast speech and lines of varying length the overlay height never changes; new lines scroll up and fade in; changing font size, Lines Shown or display mode resizes it with the bottom edge fixed.
29. Display mode, font size (both lines), background, lines shown and status bar apply immediately.
30. Fast speech: “N waiting” appears; backlog is merged; latency does not keep growing.
31. Accented/noisy speaker: with Correct Accented Speech Automatically off (default) the status may show “Hard to recognize” and translation is unaffected; once turned on under Settings → More it shows “Hard to recognize · Auto-correcting”, misheard words are corrected, and switching causes no “Translation failed”. Note `avg confidence` values in the log.
32. Live Preview shows the in-progress speech in one fixed italic line below the subtitles; when it is longer than the line only the newest part is shown after “…” (English is cut between words, Chinese between characters). The window height and the subtitles above do not move when the line appears, grows or clears; without a dragged height the default overlay height is the same as in the previous build. Disabling Live Preview removes the text.
33. Filter Chinese Speech: Chinese chatter over an English video is dropped; the status counts filtered segments.
34. Transcript file is written live in the macOS format; “New Transcript” starts a new file; quitting or starting a new transcript mid-speech still records the last lines (untranslated ones source-only). Under the English UI the file is `<time> Transcript.md` with an English header.
35. Export SRT and WebVTT; play with VLC or Media Player next to the video; timing roughly aligned; Chinese displays correctly.
36. Transcript window (“⋯” or tray menu → Transcript…): titled “LiveSub · Transcript” (“实时翻译助手 · 字幕记录” in Chinese); every line shows a small time, the source text and a slightly larger translation; untranslated lines show “…” first and failed lines only the source; with Keep Original Text When Correcting on, corrected lines add a small “Recognized: …”. Text can be selected and copied, and the wheel scrolls even over text. Scrolling up is not interrupted by new lines; scrolling back to the bottom resumes following. The bottom bar shows the status; “Show Transcript in File Explorer” opens the folder with the transcript selected (hidden before the first start); “Generate Notes” is disabled without lines and reads “Generating…” (disabled) while running.
37. Chinese Windows shows Chinese UI and “实时翻译助手”; `LIVESUB_UI_LANGUAGE=en` shows English and “LiveSub”; check for clipped text.
38. Full pipeline from a file: `$env:LIVESUB_TEST_AUDIO_FILE="C:\path\talk.wav"; $env:LIVESUB_AUTOSTART="1"; .\LiveSub.exe`.
39. Run for one hour; memory and CPU stay flat in Task Manager.
40. Quit: tray icon disappears; no leftover LiveSub, claude or node processes.
41. The log contains no API keys.
42. (Optional) Vulkan build with GPU Acceleration: the log line `whisper: loaded … runtime …` shows the runtime used; compare speed.
43. Upgrade from an earlier build: start the new build with the earlier `%APPDATA%\LiveSub\settings.json` and `secrets.bin` in place. The previous service and model stay selected; the Anthropic API becomes an “Anthropic” service and each OpenAI-compatible service keeps its name; Test succeeds (keys kept); the log has `settings: migrated …`. Restarting again adds no services.
44. Settings window: Translation, Subtitles and a collapsed More. Audio Source offers Microphone, System Audio and Both; Microphone Noise Reduction hides for System Audio; Filter Chinese Speech hides for Mandarin; Lines Shown hides after dragging the top/bottom edge and returns after Size → Reset; Cloud Models under Translation Model always lists Claude Haiku, Claude Sonnet and Claude Opus, with no separate Opus switch; Keep Original Text When Correcting shows only with auto-correction on. With Settings open, changing the model or lock from the tray or status bar updates the window. Long English labels wrap instead of being clipped.
45. Tray → **AI Assistant** opens the panel beside the overlay (right side with an ~8 px gap, else left; bottom-aligned when the overlay is in the lower half of the screen, top-aligned otherwise) and checks the menu item. Not in the taskbar or Alt+Tab. While typing in Notepad, opening the panel and clicking its header, quick questions or model selector leave Notepad focused; clicking the input box lets you type (IME included) and Enter sends (Enter while composing only commits the IME text). With the overlay hidden, the tray item shows both.
46. Start subtitles and press “Summarize recent content”: “Answering…” with a spinner, then the answer streams in; bold and inline code render; quick questions and Send are disabled while answering; answers can be selected and copied; the conversation scrolls to the bottom and the wheel scrolls it. A follow-up about earlier lines is answered correctly, also after switching the model in the header. New Conversation clears it; Stop/Start and New Transcript clear it too, automatic restarts after a settings change do not. With claude.exe renamed or a wrong key for the selected service, an orange error appears without hanging; after fixing it the next question works.
47. Drag the header (also on the icon and title) to move the panel; resize from all edges and corners (minimum about 280×220); position and size persist across restarts. Moving or resizing the overlay does not move the panel; Move Subtitles to Bottom puts it back beside the overlay. Hiding the overlay (quick button, hotkey, tray menu) hides the panel and showing it (tray click, hotkey) brings the panel back; Close in the header and Alt+F4 close it and uncheck the tray item; it is closed after a restart. With Claude Code, a second claude process appears only after the panel was opened (or subtitles were started with it open).
48. Generate Notes is disabled without subtitles. With subtitles: the item reads “Generating Notes…” (disabled) and the tray tooltip shows “Generating notes with Claude Sonnet…” (the assistant's model); afterwards `<time> Notes.md` (`<time> 笔记.md` under the Chinese UI) appears next to the transcript and opens, and the tooltip shows “Notes saved: …”. When the model is unavailable, “Failed to generate notes: …” or the reason is shown.
49. Quick buttons and the “⋯” menu: hovering shows exactly four buttons — Start/Stop (“Start” or “Stop”), an open lock (“Lock subtitles”), “⋯” (“More”) and Hide (“Hide subtitles. Press Win+Alt+L to show them again.”). “⋯” opens a menu below it: Display ▸ (Bilingual / Translation Only / Original Only, current one checked), Transcript…, Show AI Assistant (Hide AI Assistant while open), Settings…; each works. With the menu open, moving the mouse off the overlay keeps the buttons; after it closes they disappear unless the mouse is over the overlay. While typing in Notepad, opening “⋯” and switching the display mode leave Notepad focused. Clicking the desktop, taskbar or another app closes the menu; the status-bar model menu and the assistant's model menu behave the same.
50. Tray icon and menu: with the overlay shown, a left click opens the menu (same as right click); clicking the icon again while the menu is open closes it without reopening it. With the overlay hidden, a left click shows it (and the assistant if it was open) without a menu. Right click always opens the menu; clicking outside closes it. The order is: status → (Open Microphone Settings when blocked) → Start Subtitles, Audio Source, Display, Translation Model → Hide Subtitles (with the shortcut), AI Assistant, Lock Subtitles, Move Subtitles to Bottom → Transcript…, Generate Notes, Export Subtitles, New Transcript, Open Transcript Folder → Settings…, Quit. Start Subtitles from the menu while hidden shows the overlay and starts. After a start, Open Transcript Folder opens Explorer with the current transcript selected.
51. Both: choose Both as the audio source in Settings (“Microphone and System Audio”, checked, in the tray menu), play an English video and speak into the microphone. Both sides produce subtitles, each source line starting with a small “Microphone” or “System Audio” tag (small text on a translucent rounded background); a single source shows no tags. When both speak at once the live line has two rows, each tagged, and the overlay height does not change; live previews appear for both. The Transcript window shows the source after each time, and the transcript file writes `**[time] Microphone** text`. With one side quiet and the other playing, the status bar shows input rather than “No audio input”; the status reads “Listening: Microphone and System Audio · …”. During fast speech, backlog merging never joins lines from different sources. Note CPU use and latency with speech on both sides compared with a single source.
52. Both — devices and access: changing the default output device while running restarts only system audio (log `audio: default device changed, restarting loopback capture`) and microphone subtitles continue; changing the default recording device restarts only the microphone (`restarting microphone capture`). With “Let desktop apps access your microphone” turned off, Start: system audio subtitles continue; the microphone notice and “Open Microphone Settings” appear below the subtitles and the tray status reads “System audio continues; the microphone resumes once access is turned on”. Turning access back on resumes the microphone within seconds without restarting. Without a microphone (or after unplugging it), Both keeps system audio running, briefly shows the microphone error and retries every few seconds instead of stopping.
53. Microphone Noise Reduction appears under the audio source in Settings → Translation for Microphone and Both (with a tooltip) and hides for System Audio. Turning it on restarts recognition; the log shows `audio: microphone (noise reduction) … Hz`, or `audio: noise reduction unavailable, capturing without it` with subtitles continuing. Compare recognition with a fan or keyboard noise nearby, and with a distant speaker. With Control Panel › Sound › Communications left at “Reduce the volume of other sounds by 80%”, source Both and noise reduction on: the video's volume is not reduced and system-audio subtitles continue; the log has no `audio: cannot opt out of ducking`. Playing a video through speakers with source Both, compare whether duplicate “Microphone” lines decrease with noise reduction on.
54. Saving files: start subtitles, speak a little, then ask the assistant “save the summary to a file”. “Answering…” appears first, then “Saving file…” with a spinner once the assistant starts the file (the file content never shows in the panel); afterwards a blue line such as “Saved: 2026-10-21 21.32 Summary.md” with a document icon appears below the answer, and the text outside the file is at most one sentence. Hovering the line shows a hand cursor and “Show in File Explorer”; clicking it opens Explorer with the file selected, in `Documents\LiveSub\AI Assistant\` (`AI 助手` under the Chinese UI); Notepad shows complete Markdown with Chinese intact. Asking for a file with the same name again creates “… 2.md” and leaves the first one unchanged; asking for two files shows two “Saved” lines; ordinary questions create no files. With the transcript folder set to a folder you cannot write to (e.g. `C:\Windows`), the file goes to `%LOCALAPPDATA%\LiveSub\AI Assistant\`, still shows “Saved”, and the log has `assistant: cannot write to the transcript folder` (set the folder back afterwards). It also works after switching the header to a model service or local model. New Conversation while “Saving file…” shows clears the panel without errors and saves nothing. The log has a line like `assistant: saved 1/1 files` (count only).
55. System dialogs are not covered: with the subtitles and the assistant open (drag the assistant to the middle of the screen), open Settings › More › Transcript Folder › Change…, Claude Path › Browse…, and tray › Export Subtitles › SRT…: each dialog shows above the subtitles and the assistant with its buttons visible, and stays uncovered after a few seconds; after closing it, the subtitles and the assistant are still above other windows.

### Project layout

```
LiveSub.Windows.sln
src/LiveSub.Core/         platform-independent logic (ports of macOS LiveSubCore + chat/SSE/audio chunking), net10.0
src/LiveSub.App/          WPF tray app (net10.0-windows): audio, Whisper, providers, overlay, settings
tests/LiveSub.Core.Tests/ xUnit tests (same inputs/outputs as the Swift tests, plus Windows-specific pieces)
```
