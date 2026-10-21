# LiveSub 路线图

LiveSub 的目标是一款面向所有人的实时语音翻译字幕工具，适用于现场交谈、讲座、会议和视频。本文记录接下来的计划与尚未决定的问题，会随项目进展更新。

## 现状

- **平台**：macOS 14.2 或更新。macOS 26 使用 SpeechAnalyzer；macOS 14.2–15 使用 SFSpeechRecognizer，没有 Apple 本地翻译和中文说话声过滤。Windows 10/11 版本 0.1 已完成，等待实体机测试。
- **翻译模型**：三档。基础为 Apple 本地翻译（macOS 26）；云端模型为 Claude Code 或自己的 API（OpenAI 兼容格式、Anthropic Messages API）；本地模型为 Ollama、LM Studio。Apple Translation 同时负责所选模型出错时的备用翻译。
- **界面语言**：简体中文、英文，跟随系统。中文名“实时翻译助手”。
- **许可证**：MIT。
- **已有功能**：悬浮双语字幕、AI 助手、笔记生成、SRT 与 WebVTT 导出、中文说话声过滤、识别困难时自动纠错、运行状态条、麦克风降噪。
- **主要限制**：
  - macOS 14.2–15 的识别路径与界面兼容代码尚未在实体机上运行过。
  - 除 Claude 外，其他模型对提示词（纠错标记、输出格式）的遵循程度还没有做基准测试。

## 一、开源准备

1. **README**：改写为正式的项目介绍，中英双语，附截图和演示动图，包含安装、权限说明与常见问题。
2. **许可证**：已选定 MIT。Windows 版依赖（NAudio、Whisper.net、Anthropic C# SDK）和 Whisper 模型应当都是 MIT 许可，发布前需要逐个核对。
3. **英文界面**：已完成，使用 `Localizable.strings`（命令行工具链不能编译 String Catalog）。新增界面文字时要同时补充两种语言。
4. **分发方式**：没有经过 Apple 公证的应用，从网上下载后会被 Gatekeeper 拦截。可选方案：
   - 发布源码和构建脚本，由用户在本机构建，配合 `scripts/make-signing-cert.sh` 生成本机签名证书。
   - 加入 Apple Developer Program，用 Developer ID 签名并公证后发布安装包，年费 99 美元。
5. **持续集成**：用 GitHub Actions 在 macOS 上构建并运行单元测试。
6. **社区文件**：贡献指南、Issue 与 PR 模板、行为准则。
7. **最低系统版本**：已降到 macOS 14.2（系统录音需要的最低版本）。macOS 14.2–15 使用 SFSpeechRecognizer，识别质量和延迟需要在实体机上实测。

## 二、多模型支持

目前最好的翻译、自动纠错、AI 助手和笔记都依赖 Claude Code 命令行。这是作者本人具备的条件，多数用户并没有，需要提供其他选择。

已实现前三步：通用会话接口（`ChatSession`）、OpenAI 兼容提供方、Anthropic API 提供方，以及“模型服务”设置窗口和钥匙串存储。原计划的四档合并为三档：

| 档位 | 用户需要准备 | 翻译 | 自动纠错、AI 助手、笔记 |
|---|---|---|---|
| 基础 | 无，免费且无需账号 | Apple 本地翻译（macOS 26） | 不可用 |
| 云端模型 | 已登录的 Claude Code，或自己的 API 密钥 | Claude、OpenAI、DeepSeek、通义千问、Gemini 等 | 可用 |
| 本地模型 | 自行运行 Ollama 或 LM Studio | 本地大模型 | 取决于模型能力 |

### 架构

- 把 `ClaudeTranslator` 里与命令行无关的部分抽成通用的大模型会话接口，包括系统提示词、多轮对话、流式输出、超时，以及新会话时补发上下文。翻译、AI 助手、笔记和自动纠错都基于这个接口。
- 计划实现的提供方：
  1. Claude Code 命令行，即现有实现。
  2. OpenAI 兼容接口。一个实现即可覆盖 OpenAI、DeepSeek、通义千问、OpenRouter 等云服务，以及 Ollama、LM Studio 等本地服务。
  3. Anthropic Messages API。
  4. Apple Translation，只提供翻译。
- API 密钥保存在钥匙串中，不写入设置文件，也不出现在日志里。
- 首次启动时引导用户选择档位，并自动检测本机是否已有 Claude Code 或 Ollama。
- 不同模型对提示词的遵循程度不同，例如纠错标记和输出格式。每个提供方都要用现有的 `--export-test` 与 `LIVESUB_DEBUG_LATENCY` 做质量和延迟基准测试。

### 实施顺序

1. 抽出通用会话接口，把 Claude Code 迁移上去，行为保持不变。以现有单元测试和延迟基准作为回归标准。（已完成）
2. OpenAI 兼容提供方，同时完成钥匙串存储和设置界面。（已完成）
3. Anthropic API 提供方。（已完成）
4. 首次启动引导，以及各功能在不同档位下的可用性提示。（未开始）
5. 其他模型的质量与延迟基准测试。（未开始）

## 三、Windows 版本

以下方案均未经验证，开工前需要在实体 Windows 电脑上逐项确认。

### 需要替换的模块

| 模块 | macOS 现状 | Windows 候选方案 |
|---|---|---|
| 系统音频采集 | Core Audio process tap | WASAPI 环回采集 |
| 麦克风采集 | AVAudioEngine | WASAPI |
| 语音识别 | SpeechAnalyzer | whisper.cpp 或 faster-whisper 本地识别；或用户自带密钥的云端识别 |
| 本地备用翻译 | Apple Translation | OPUS-MT 等专用翻译模型，或只使用大模型 |
| 悬浮字幕、托盘图标、全局快捷键 | AppKit 与 SwiftUI | WinUI 3 或 WPF，配合系统托盘与 RegisterHotKey |
| 大模型 | Claude Code 等 | 与多模型支持共用同一套提供方设计；Claude Code 也可在 Windows 上使用 |

### 可复用的部分

`LiveSubCore` 中的纯逻辑可以复用，包括分句、中文过滤判定、识别质量判定、提示词、译文清理和字幕导出。有两种做法：

- 用 Windows 版 Swift 工具链直接编译这部分。
- 在 Windows 技术栈中重写，并把现有单元测试整理成两个平台共用的测试数据，保证行为一致。

### 阶段

1. 选定技术栈，做一个只包含“系统音频 → 识别 → 悬浮字幕”的原型。
2. 在实体 Windows 电脑上验证系统音频、耳机切换、全屏应用上方的字幕显示和锁定后的点击穿透。界面开发可以在虚拟机中进行，音频相关的功能必须用实体机验证。
3. 用同一段音频与 macOS 版对比识别准确度、专业术语、口音、延迟，以及长时间运行的内存与 CPU 占用。
4. 接入多模型提供方，再补齐 AI 助手、笔记与导出。

## 待决定

1. 分发方式：源码自行构建，还是签名公证后发布安装包。
2. Windows 版最低电脑配置。技术栈已选定 C#、.NET、WPF，识别用 Whisper.net。
3. 主要支持的语言方向。目前以英译中为主。
4. 是否提供托管的云端服务。提供的话会产生服务器费用。

## 待核实

- whisper.cpp 与 faster-whisper 在普通 Windows 笔记本上能否做到实时识别。
- OPUS-MT 英译中模型、Qwen 系列模型的许可证、实际翻译质量和资源占用。
