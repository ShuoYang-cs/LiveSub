# LiveSub · 实时翻译助手

macOS 和 Windows 小工具：实时监听**麦克风**（现场交谈、讲座、会议）和/或**系统正在播放的声音**（视频、网课、线上会议），识别成文字、翻译，并以**悬浮双语字幕**显示在屏幕上，同时自动保存文字记录；可以一键生成笔记，旁边的 AI 助手随时回答“刚才说了什么”。

## 下载安装（不需要懂 GitHub）

| 你的电脑 | 点这里下载 | 然后 |
|---|---|---|
| **Mac**（macOS 14.2 或更新，Apple 芯片和 Intel 都行） | [打开 Mac 版下载页](https://github.com/ShuoYang-cs/LiveSub/releases/latest) | 在页面下方的 **Assets** 里点 `LiveSub-…-macOS.dmg`，双击打开，把图标拖进“应用程序” |
| **Windows**（10 或 11，64 位） | [打开 Windows 版下载页](https://github.com/ShuoYang-cs/LiveSub/releases/tag/windows-0.1.0) | 在页面下方的 **Assets** 里点 `LiveSub-…-Windows-x64.zip`（有独立显卡的选 `-vulkan` 那个），解压后双击 `LiveSub.exe` |

下载页里只需要点上面说的那一个文件，其他（如 “Source code”）是给开发者的，不用管。

**第一次打开会被系统拦一下**，这是正常的：LiveSub 没有花钱做苹果的公证和微软的代码签名，不是病毒。
- **Mac**：看到“Apple 无法验证……”时点“完成”，打开 **系统设置 › 隐私与安全性**，往下找到“已阻止打开‘实时翻译助手’”，点 **仍要打开**，输入电脑密码。之后正常双击就行。
- **Windows**：看到“Windows 已保护你的电脑”时，点 **更多信息**，再点 **仍要运行**。

打开后，悬浮字幕窗口里点 **开始翻译**，按提示允许麦克风或系统录音权限即可，不需要任何设置或账号。想要更准的翻译、AI 助手、自动纠正口音，再到设置里选 Claude 或自己的模型。详细说明见下面。

## 设计思路

| 环节 | 用什么 | 说明 |
|---|---|---|
| 系统音频采集 | Core Audio process tap | 只需“系统录音”权限，不需要屏幕录制，不影响正常播放 |
| 麦克风采集 | AVAudioEngine | 可选系统语音处理降噪 |
| 语音识别 | 系统 `SpeechAnalyzer` | 本地离线，模型由系统管理，几乎不占本应用内存 |
| 翻译 | 可选：Google 翻译、Apple 本地翻译、混元翻译（下载到本机）、Claude Code、自己的 API、本地模型 | 见下方“选择翻译模型” |
| 备用翻译 | Apple `Translation` 框架 | 离线；所选模型出错时自动顶上 |
| AI 助手与笔记 | 所选的大模型 | 需要 Claude Code、自己的 API 或本地模型 |

## 运行要求

- macOS 14.2 或更新。不同系统版本可用的功能：

  | 功能 | macOS 14.2–15 | macOS 26 或更新 |
  |---|---|---|
  | 语音识别 | 系统 `SFSpeechRecognizer`，支持时只在本机识别 | 系统 `SpeechAnalyzer`，本地离线，延迟更低 |
  | 翻译模型 | Google 翻译、混元翻译、Claude Code、自己的 API、本地模型 | 另有 Apple 本地翻译 |
  | 出错时自动改用 Apple 翻译 | 不可用 | 可用 |
  | 过滤周围的中文交谈（自动） | 不可用 | 可用 |
  | 识别困难时自动纠错 | 不可用（旧版识别器不提供置信度） | 可用 |

  macOS 14.2 是系统录音（Core Audio process tap）需要的最低版本。macOS 14.2–15 上第一次开始字幕时，系统还会询问**语音识别**权限。
- Apple 芯片和 Intel 芯片的 Mac 都可以用。
- 只有从源码构建时才需要 Xcode 命令行工具；下载安装包不需要。
- 一个翻译模型：默认用 Google 翻译（联网即可）；macOS 26 自带 Apple 本地翻译；也可以下载混元翻译离线用，或用 Claude Code、自己的 API、本地模型，见“选择翻译模型”

界面语言跟随系统，目前有简体中文和英文。应用在中文系统里显示为“实时翻译助手”。

## 安装

### 下载安装（推荐）

1. 打开 [Releases 页面](https://github.com/ShuoYang-cs/LiveSub/releases/latest)，下载 `LiveSub-版本号-macOS.dmg`。
2. 双击打开下载的 dmg，在弹出的窗口里把 `LiveSub.app` 拖到旁边的“应用程序”文件夹上，然后推出这个磁盘。在中文系统里它显示为“实时翻译助手”。同一页也有 zip 版本，解压后同样拖进“应用程序”即可。
3. 第一次打开时，系统会提示“Apple 无法验证……”，这是因为 LiveSub 没有经过苹果公证（公证需要付费的开发者账号）。点“完成”，然后打开 **系统设置 › 隐私与安全性**，在页面下方找到“已阻止打开‘实时翻译助手’”，点 **仍要打开**，输入电脑的登录密码。之后就能正常双击打开。
4. 接着看下面的“首次使用”。

**更新**：下载新版本，替换“应用程序”里的旧版。每次替换后第一次打开，还要在“隐私与安全性”里再点一次“仍要打开”；麦克风和系统录音权限会保留，不用重新允许。

### 从源码构建

适合想自己修改代码的用户。

1. 安装 Xcode 命令行工具：在“终端”里运行 `xcode-select --install`，按提示安装。不需要完整的 Xcode。
2. 下载代码：
   ```bash
   git clone https://github.com/ShuoYang-cs/LiveSub.git
   cd LiveSub
   ```
   不用 git 的话，也可以在仓库页面点 **Code › Download ZIP**，解压后在终端里用 `cd` 进入这个文件夹。
3. 构建并安装：
   ```bash
   scripts/make-signing-cert.sh    # 只需一次：在登录钥匙串里创建本机签名证书“LiveSub Local Signing”
   scripts/build-app.sh --install  # 构建，安装到 ~/Applications 并打开
   ```
   只想生成 App 不安装时，运行 `scripts/build-app.sh`，结果在 `build/LiveSub.app`。

有本机签名证书时，`build-app.sh` 用它签名：签名身份固定，重新构建后系统不会再询问麦克风/系统录音权限（换成证书签名后的第一次启动还会问一次）。没有证书时用 ad-hoc 签名，每次重新构建都要重新授权。

开发时：`swift build`，单元测试：`scripts/test.sh`。

发布新版本（维护者）：改 `Resources/Info.plist` 里的版本号，运行 `scripts/package-release.sh`，生成 Apple 芯片和 Intel 通用的 `build/LiveSub-版本号-macOS.dmg` 和 `.zip`，上传到 GitHub Releases。必须在有“LiveSub Local Signing”证书的那台电脑上打包：换了证书，系统会把新版本当成另一个 App，用户要重新授权。

## 首次使用

1. **准备翻译模型**：默认用 Google 翻译，不需要账号和设置，联网就能用。想换成 Apple 本地翻译（离线）、Claude Code、自己的 API 或本地模型，见下面“选择翻译模型”。
   想用口音纠正、AI 助手和笔记：安装 Claude Code，在终端运行 `claude`，输入 `/login` 按提示登录（只需一次）。VS Code 插件里的登录和终端命令行的登录是分开的，没登录时 LiveSub 会提示。
2. 打开 LiveSub，屏幕右上角出现字幕窗口。点中间的 **开始翻译**。
3. 按系统提示允许 **麦克风** 和/或 **系统录音** 权限。
   - 默认同时听**麦克风**和**电脑正在播放的声音**（音源“两者”），字幕栏显示“正在听麦克风和系统声音…”。只想听其中一种，在设置里改音源。
   - 如果没有允许系统录音，字幕下方会提示“没有系统录音权限”，点 **打开系统设置**，在“隐私与安全性 › 屏幕与系统录音”里打开“实时翻译助手”，字幕会自动继续。字幕锁定时，先点右上角的锁解锁。
4. 第一次识别某种语言时，系统会自动下载语音识别模型（英语约十几秒）。

## 使用

- **没有菜单栏图标**：所有操作都在字幕窗口上。第一次打开时字幕在屏幕右上角，面积是屏幕的 1/16，宽高比 3:1。
- **快捷按钮**（鼠标移到字幕上出现；停在某个按钮上时按钮变亮，下面显示它的功能名）：开始/停止、录音、锁定、音源（麦克风、系统音频或两者）、AI 助手、设置、退出。
- **收起**：按 **⌃⌥L** 收起或显示字幕（识别继续在后台跑）；用 Spotlight 再打开一次 LiveSub 也能把字幕叫回来。
- **悬浮字幕**：按住拖动，字幕总在所有窗口上面。**锁定**（快捷按钮里的锁，或设置 › 字幕）后字幕点不到、不挡操作，可以直接点到下面的幻灯片、网页；鼠标移到字幕上，右上角出现一把闭合的锁，点它解锁，字幕的其他地方仍然点不到。全屏视频上也能显示。
- **外观**（设置 › 字幕）：跟随系统（默认）、浅色、深色，字幕和 AI 助手一起变。
- **大小固定**：字幕窗口的大小只随设置变化，出字幕时不会忽高忽低。新字幕在框里从下往上滚动，正在说的那句固定显示在最下面一行。
- **调整大小**：拖动字幕的上下左右边框或四角。只拖左右改宽度；拖上下边框改成自己想要的高度，会被记住。双击上/下边框恢复默认高度（宽度和底边不动）。设置 → 字幕里的“恢复默认”可以一起恢复大小和位置。
- **往回看**：在字幕上向上滚动。停在底部时自动跟随最新一句，往上翻时不会被拽走，点“返回实时”回到底部。
- **字幕记录**：设置 › 记录 › 字幕记录 › 查看…，打开单独的窗口查看本次的全部字幕，可以生成笔记、导出字幕、开始新的记录、在 Finder 中显示记录文件。
- **录音**：快捷按钮里的 ⏺。打开后，字幕运行期间把听到的声音存成 `<时间> 录音.m4a`，和字幕记录放在一起（AAC，一小时约 29 MB）；音源是“两者”时，麦克风和系统音频各存一个文件。字幕运行中也可以随时打开或关掉，录音时状态条上显示红色的“录音”。设置 › 记录 › 录音记录 › 查看… 列出全部录音，可以播放、在 Finder 中显示，或打开同一次的字幕记录。录音前请先征得讲者和在场的人同意。
- **设置**（一页，改了自动生效，不用重新开始）：翻译（语言、音源、翻译模型、模型服务、自动纠正口音以及纠正时保留识别原文）、字幕（显示方式、外观、字号、背景、显示句数、恢复默认、状态条、锁定）、记录（字幕记录窗口、录音记录、记录文件夹）。“恢复默认”把字幕的大小、位置和 AI 助手的位置都复原。
- **记录**：每句话实时写入 `~/Documents/LiveSub/<时间> 字幕记录.md`；笔记保存在同一文件夹。
- **过滤周围的中文交谈**（macOS 26，自动开启、没有开关，讲话语言不是中文时才生效）：只保留讲课的内容，旁边同学用中文聊天的话不进字幕。旁边有人用中文交谈时，英语识别器会把它硬听成乱七八糟的英语。LiveSub 同时跑一个中文识别器当“哨兵”：它听到中文会输出汉字、听到英语（包括口音很重的英语）只会输出拉丁字母，据此逐词去掉中文部分。代价是系统语音服务多用约 60 MB 内存，字幕最多晚 1.5 秒（等哨兵确认）。讲话语言是日语、韩语时开着也不会误删老师的话（2026-10-23 用合成语音测过）。只过滤中文，因为用户主要是中国留学生，字幕语言基本是中文。
- **自动纠正口音**（设置 › 翻译，默认关闭）：打开后不用手动设置口音。用 Google 或 Apple 翻译时，纠正期间临时改用 Claude Sonnet（需要本机已登录 Claude Code），识别清楚了再换回来。LiveSub 统计识别器对最近几十个词的平均置信度（实测口音较重的讲课约 0.73，清晰英语 0.9 以上），偏低时自动让大模型按发音和上下文纠正近音词（如 “sub sub problems” → subproblems），字幕原文也换成纠正后的版本；口音、语速快、噪音大都会触发。开始纠正大约要十几秒；中间偶尔几句说得清楚不会停止纠正，要连续清楚一分钟才停止，免得纠正时开时关。没打开时翻译不受影响，保持最快。系统没有识别“是哪种口音”的能力（需要专门的模型），纠正也不需要知道口音名称。打开后下面会出现“纠正时保留识别原文”（默认关闭），打开它，记录文件和字幕记录窗口里会同时保留识别器原来的文字。没打开自动纠正时，状态条仍会提示“识别较难”。
- **状态条**（字幕底部一小行，可在设置 › 字幕里关掉）：有没有收到声音（音量条）、识别清晰还是较难、当前翻译模型（点它可以切换）、翻译积压几句、最近的错误。
- **麦克风降噪**（可选，音源包含麦克风时出现）：打开系统语音处理（降噪、回声消除、自动增益）。嘈杂环境下可以试试；它偏向离麦克风近的人声，远处讲者可能反而被压低，可以对照状态条的“识别清晰/较难”决定开不开。回声消除还能减少“两者同时”模式下外放被再录一遍的重复字幕。
- **导出字幕**：字幕记录窗口 → 导出字幕 → SRT / WebVTT。每句记录了它在音频里的起止时间（相对本次记录开始录音的时刻），从开始字幕就打开录音时和录音文件对得上，也可以给同时录下的视频/会议配字幕；文字按当前“显示内容”（双语/仅译文/仅原文）。

## 选择翻译模型

翻译模型在字幕底部的状态条，或设置 → 翻译模型中切换。列表里每家服务只有一项（Claude、OpenAI、通义千问……）；这家具体用哪个模型，在“模型服务”里这家的“翻译用”“AI 助手用”分别选。分为几档：

| 档位 | 需要准备 | 翻译质量 | 口音纠正 |
|---|---|---|---|
| 基础：Google 翻译（默认） | 无（需要联网，文字会发给 Google） | 好 | 装了 Claude Code 时可用 |
| 基础：Apple 本地翻译 | macOS 26 和对应语言包，离线 | 一般 | 装了 Claude Code 时可用 |
| 云端模型 | 本机已登录的 Claude Code，或自己的 API 密钥 | 好 | 可用 |
| 本地模型：混元翻译 | 在“模型服务”里下载（约 1.15 GB），之后离线 | 好 | 装了 Claude Code 时可用 |
| 本地模型：Ollama、LM Studio | 在本机运行 Ollama 或 LM Studio | 取决于模型和电脑配置 | 取决于模型 |

第一次使用时，音源默认是麦克风和系统音频，翻译默认是英语 → 简体中文、Google 翻译。AI 助手和笔记另用 AI 助手的模型（默认 Claude Sonnet），和翻译模型无关。

云端模型里默认列出了 OpenAI（gpt-5.4-mini）、通义千问（qwen-flash）、DeepSeek（deepseek-flash）、Kimi（kimi-k2.6）和 Grok（grok-4.20-0309-non-reasoning），都选了各家快、不“思考”的模型，适合实时字幕；要用哪个，在“模型服务”里填上自己在该服务商申请的 API 密钥即可。还没填密钥就选了它时，LiveSub 会打开“模型服务”并定位到这家。模型名会随服务商更新，可以在“模型服务”里点“获取列表”，再在“翻译用”“AI 助手用”里换成别的。DeepSeek、Kimi、通义千问的这些模型默认会先思考，LiveSub 请求时自动关掉思考，免得每句多等几秒。

Google 翻译用的是非官方的免费网页接口：不需要密钥，但没有官方保证，短时间请求太多会被限流（HTTP 429）。LiveSub 准备了两个接口，一个被限流就换另一个，被限流的那个休息一分钟；两个都失败时等 1–4 秒再试一次，还不行时，装了 Apple 翻译语言包就改用 Apple 本地翻译。Google、Apple 和混元翻译时最多两句同时翻译。

选 Apple 本地翻译时要先下载语言包：设置里会提示还没下载，点“下载…”即可，也可以在“系统设置 › 通用 › 语言与地区 › 翻译语言”中下载。

### 使用 Claude Code

安装 [Claude Code](https://claude.com/claude-code) 后，在终端运行 `claude` 并输入 `/login` 登录一次。LiveSub 会自动找到 `claude` 命令，翻译模型中选择 Claude 即可。模型列表里 Claude 只有一项，具体用 Haiku、Sonnet 还是 Opus，在“模型服务”最上面的 Claude Code 里分别为翻译和 AI 助手选（默认翻译用 Haiku，AI 助手用 Sonnet）；claude 命令的路径也在那里填，点“测试”可以确认能不能用。如果你在终端里用环境变量登录（例如 `claude setup-token` 生成的 `CLAUDE_CODE_OAUTH_TOKEN`、`ANTHROPIC_API_KEY`，或 Bedrock、Vertex、代理设置），写在 `~/.zshrc` 或 `~/.zprofile` 里即可，LiveSub 会读取终端的这些变量。Haiku 速度最快，适合实时字幕；Sonnet 更准确，适合 AI 助手和笔记。

### 使用自己的 API

1. 点字幕状态条上的翻译模型 → **管理模型服务…**，或设置 → 模型服务 → **管理…**。
2. 点 **添加模型服务**，选择服务商模板。模板已填好接口格式和地址。
3. 填入 API 密钥。密钥保存在系统钥匙串中，不写入设置文件。
4. 点 **获取列表** 读取可用模型，删去不需要的；也可以在“可用模型”里直接手动填写模型名，多个用逗号分隔。
5. 在 **翻译用**、**AI 助手用** 里分别选这家的一个模型（默认第一个）。
6. 点 **测试**：会用“翻译用”的模型翻译一句话，并显示译文和用时。
7. 在翻译模型或 AI 助手的模型列表中选择这家服务。

| 服务 | 接口格式 | 接口地址 | 获取 API 密钥 |
|---|---|---|---|
| Anthropic | Anthropic | `https://api.anthropic.com` | Claude 开发者平台 |
| OpenAI | OpenAI 兼容 | `https://api.openai.com/v1` | <https://platform.openai.com/api-keys> |
| DeepSeek | OpenAI 兼容 | `https://api.deepseek.com/v1` | <https://platform.deepseek.com> |
| 通义千问 | OpenAI 兼容 | `https://dashscope.aliyuncs.com/compatible-mode/v1` | 阿里云百炼控制台 |
| Gemini | OpenAI 兼容 | `https://generativelanguage.googleapis.com/v1beta/openai` | <https://aistudio.google.com> |
| OpenRouter | OpenAI 兼容 | `https://openrouter.ai/api/v1` | <https://openrouter.ai/keys> |

其他提供 OpenAI 兼容接口的服务，选择“自定义”并填写服务商给出的地址即可。

建议：

- 实时字幕对速度要求高，选择各家的快速、低价模型，例如 Anthropic 的 `claude-haiku-5-5`、DeepSeek 的 `deepseek-chat`。模型名以服务商当前文档或“获取列表”的结果为准。
- 费用按服务商的价格计算。每句翻译都会带上系统提示和最近 12 轮对话作为上下文，一小时连续讲话大约需要 100 万输入 token 和几万输出 token。以 Claude Haiku 5.5 为例，每小时约 0.1 到 0.2 美元；Anthropic 接口会自动使用提示缓存降低费用。其他服务以其价格为准。

### 使用本地模型

本地模型完全在你的电脑上运行，不需要账号和网络，但会占用内存和算力。

**混元翻译（推荐，不用装别的软件）**

腾讯混元的翻译专用模型 [Hy-MT2 1.8B](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF)（2026 年 5 月发布，Apache-2.0 许可，支持 33 种语言）。在“模型服务”的 **混元翻译 · 本机** 里点 **下载**：LiveSub 会下载 llama.cpp 的运行程序（十几 MB）和 4 位量化的模型（1.1 GB，Hugging Face 打不开时自动换镜像），都按 SHA-256 校验，存在 `~/Library/Application Support/LiveSub/LocalModel/`。下好后在翻译模型的“本地模型”里选 **混元翻译**。

- 速度：Apple 芯片上每句 0.1–0.3 秒（比 Claude Haiku 快），第一句前要加载 1–8 秒。Intel 芯片只用 CPU，会慢一些。
- 内存：运行时约 1.4 GB。不用它、或停了字幕闲置 10 分钟后自动关掉；LiveSub 退出（包括崩溃）时也会关掉。
- 只能翻译，不能做 AI 助手和笔记。每句带上前两句原文作背景，代词和术语能前后一致。
- 不用了可以在同一处点 **删除**。

**Ollama**

1. 从 <https://ollama.com> 下载安装并启动 Ollama。
2. 在终端下载一个模型，例如中文能力较好的通义千问系列：`ollama pull qwen2.5:7b`。
3. 在 LiveSub 的模型服务中添加 **Ollama** 模板，地址为 `http://localhost:11434/v1`，不需要 API 密钥。
4. 点 **获取列表** 和 **测试**，然后在翻译模型中选择。

**LM Studio**

1. 从 <https://lmstudio.ai> 下载安装，在应用内搜索并下载一个模型。
2. 在 LM Studio 中启动本地服务器，默认地址为 `http://localhost:1234/v1`。
3. 在 LiveSub 的模型服务中添加 **LM Studio** 模板，后续步骤同上。

硬件参考：7B 到 8B 参数的量化模型大约需要 6 到 8 GB 空闲内存，建议在 16 GB 以上内存的 Apple 芯片电脑上使用。本地模型的翻译速度明显慢于云端模型，可以先用“测试”看用时是否能接受。

### 隐私

- 语音识别始终在本机完成，音频不会上传。
- 选择 Google 翻译、Claude Code 或自己的 API 时，识别出的文字会发送给对应服务商，用于翻译、AI 助手和笔记；交给 AI 助手的图片和文件也会发送过去。
- 录音只存在本机的记录文件夹里，不会上传。
- 选择 Apple 本地翻译或本地模型时，所有内容都留在本机。

## AI 助手（边听边问）

字幕旁边的 AI 助手面板（字幕快捷按钮里的 ✨）连着本机 Claude Code。默认收起，打开面板时才启动它的 Claude 进程：

- 每次点“开始字幕”新开一个对话；它能看到这次开始以来的全部字幕，问“刚才那个表格怎么填的？”不用再解释上下文。
- 实现上不是每句字幕都发给 Claude（那样每句都会触发一次回答），而是提问时把上次提问以来新增的字幕和正在说的半句一起带上；Claude 进程中途重启时会自动重新带上全部字幕。
- 面板右上角选回答用的模型；选 Claude 时默认用 Sonnet，型号在“模型服务”里改。有“总结最近的内容”等快捷问题。
- **存成文件**：在对话里说“把总结存成文件”“把要点整理到文件夹里”之类的话，助手会把内容写成 Markdown 文件，保存在记录文件夹下的“AI 助手”文件夹里（默认 `~/Documents/LiveSub/AI 助手/`）。文件名前面加日期和时间，同名时不会覆盖。回答里只显示“已保存：文件名”，点一下在 Finder 中显示这个文件。模型本身不能写文件，由 LiveSub 代为保存，而且只会存到这个文件夹。一次可以要求存多个文件。
- **带图片和文件提问**：把截图、幻灯片、PDF、Word、文本文件拖进面板，或按 ⌘V 粘贴图片和拷贝的文件，或点输入框左边的回形针选择。输入框上方会列出附件，点 × 可以去掉；只放附件不写问题也可以发送。图片缩小后发给模型；PDF 交给 Claude 时连图表一起读，交给其他服务时只发其中的文字；Word、RTF、文本、代码、字幕文件读出文字后发送。一次最多 10 个。Keynote、PowerPoint、压缩包等暂不支持，可以先导出成 PDF。所选模型不能看图片时，服务会报错。
- 第一次打开时放在字幕旁边（右边放不下就放左边）。按住面板顶部的标题栏可以拖到任何地方，位置会被记住；之后拖动字幕或字幕变化时，面板都不跟着动。拖边框调大小。设置 › 字幕 › 大小 › *恢复默认* 会把面板也放回字幕旁边。显示面板、点按钮、拖动都不会把焦点从你正在用的 App 抢走；点输入框打字时焦点才移到面板，原来那个 App 的光标随之停止闪烁。

## 工作流程

```
音频采集 ──▶ SpeechAnalyzer（临时结果 + 确认结果）
                 │临时结果 ──▶ 悬浮字幕“正在说”的一行
                 │确认结果 ──▶ Segmenter 分句 ──▶ 翻译队列 ──▶ Claude 流式译文 ──▶ 字幕 + 记录文件
```

- **分句**（`Sources/LiveSubCore/Segmenter.swift`）：完整句子立即送翻；太短的句子与下一句合并；过长的句子在逗号处切开；讲者停顿约 1.6 秒后把剩余内容送出。
- **长句提前翻译**（`Sources/LiveSubCore/BreathCutter.swift`）：讲者一口气说很长时，识别器要等停顿才确认整段话。这里从音量里找讲者换气时的短暂回落，把已经识别出的前半句提前确认、送去翻译。测试里一个 55 个词的长句，前半句从说完到确认由约 7 秒降到不到 2 秒。
- **防积压**：翻译跟不上语速、积压 2 句以上时，把积压的句子合并成一次请求，延迟不会越拖越长。Google 和 Apple 翻译时最多两句同时进行。
- **上下文**：Claude 会话里前面的句子就是上下文；每 40 句换新会话防止变慢，新会话第一句附带最近 6 句作为参考。

## 目录结构

```
Sources/LiveSubCore/     纯逻辑（可单测）：分句、Claude stream-json 解析、提示词、记录格式
Sources/LiveSub/Audio/   麦克风、系统声音（process tap）、测试音频文件
Sources/LiveSub/Speech/  SpeechAnalyzer 封装与音频格式转换
Sources/LiveSub/Translation/  Google、Apple、Claude CLI 和 HTTP API 翻译器
Sources/LiveSub/App/     控制器（整条链路）、设置、记录、入口
Sources/LiveSub/UI/      悬浮字幕、设置、字幕记录窗口
Tests/LiveSubCoreTests/  单元测试
scripts/                 构建与测试脚本
```

## 自检命令（不启动界面）

```bash
B=.build/debug/LiveSub
$B --transcribe-file lecture.aiff --locale en-US     # 测语音识别 + 分句
$B --transcribe-file mixed.wav --guard zh-CN --realtime   # 测中文过滤（按真实速度播放，延迟才真实）
$B --translate "First sentence." "Second one." --engine claude --model haiku   # 测 Claude 翻译和延迟（--engine google 测 Google 翻译，--engine local 测下载好的混元翻译）
$B --translate "use dynamic program." --correct      # 测识别纠错（✎ 行是纠正后的原文）
$B --translate "Hello." --engine api --format openai --base-url http://localhost:11434/v1 --model qwen2.5:7b   # 测 API 或本地模型（密钥放在环境变量 LIVESUB_API_KEY）
$B --render-window out.png    # 用真实字幕窗口离屏截图（含滚动区域）
$B --render-assistant out.png [--hover] [--attachments] # AI 助手面板截图（--hover 模型菜单悬停样式，--attachments 放上示例附件）
$B --render-models out.png [--focus-local] [--dark]   # “模型服务”窗口截图（--focus-local 滚到混元翻译一栏）
$B --assistant-test "~/Documents/LiveSub/xxx 字幕记录.md" [--ask "把要点存成文件"] [--attach 文件]...   # 用真实记录测 AI 助手（真的调用 Claude；--ask 问自定义问题并打印保存的文件，文件存在记录文件夹里，测完删掉；--attach 带附件提问，可以多个）
$B --export-test talk.aiff out.srt   # 整条链路（按真实速度识别 + Claude 翻译）后导出 SRT，检查时间轴
$B --translate "Hello everyone." --engine apple      # 测 Apple 本地翻译（macOS 26）
# 在 macOS 26 上改用旧版识别器，检查 macOS 14.2–15 的识别路径（要通过 App 包启动，语音识别权限才会记在本应用上）：
open -n --stdout out.txt --stderr out.txt --env LIVESUB_LEGACY_SPEECH=1 build/LiveSub.app --args --transcribe-file lecture.aiff --realtime
# 用音频文件代替真实声音跑完整界面：
LIVESUB_TEST_AUDIO_FILE=$PWD/lecture.aiff LIVESUB_AUTOSTART=1 $B
```

可以用 `say -o lecture.aiff "..."` 生成测试音频。

## 已知限制

- 下载的安装包没有经过苹果公证：第一次打开，以及每次更新后第一次打开，都要在“系统设置 › 隐私与安全性”里点“仍要打开”。
- Intel 芯片的版本能编译，但还没在 Intel Mac 上实际运行过。
- 使用 ad-hoc 签名时，每次重新构建后系统都会重新询问麦克风与系统录音权限。先运行一次 `scripts/make-signing-cert.sh` 创建本机签名证书即可避免。
- macOS 14.2–15 的旧版识别器只给出整段的临时文字。LiveSub 在讲者停顿约 1 秒时结束一段；连续说话时，一个完整句子的文字稳定 1 秒后提前确认。识别准确度比 macOS 26 低，字幕时间轴也是估计值。
- 音源为“麦克风和系统音频”时，扬声器外放的声音可能被麦克风再次录入，产生重复字幕。打开“麦克风降噪”的回声消除可以减轻；现场交谈建议只用麦克风，视频与会议建议只用系统音频。
- `Package.swift` 显式写入链接的 SDK 版本（`LIVESUB_SDK_VERSION`），否则 Translation 框架会误报语言包未安装。
- 命令行工具链没有 SwiftUI 宏插件，界面代码不使用 `@State`，改用 `@Observable` 小对象。

## Windows 版本

`windows/` 目录是 Windows 10/11 版本（C#、WPF），说明和测试清单见 [windows/README.md](windows/README.md)。

## 路线图

开源准备、多模型支持与 Windows 版本的计划见 [ROADMAP.md](ROADMAP.md)。

## 许可证

[MIT](LICENSE) © 2026 Shuo Yang（杨朔）

## 致谢

Google 翻译、长句提前翻译、字幕窗口的快捷按钮和外观等改进，来自同学 Haotian Zhu 基于 LiveSub 改写的 [ICT（InClass Translator）](https://github.com/HaotianZhu296/ICT)，经作者同意合并回 LiveSub。
