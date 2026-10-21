// swift-tools-version: 6.0
import PackageDescription

// SwiftPM 会把可执行文件记录的“链接 SDK 版本”写成部署版本，导致 Translation 框架按旧行为
// 报告语言包未安装（notInstalled）。这里显式写入实际 SDK 版本；scripts/build-app.sh 会自动传入。
//
// 最低支持 macOS 14.2（系统音频采集需要 Core Audio process tap）。更新的系统功能按版本启用：
// macOS 26 用 SpeechAnalyzer 识别和 Apple 本地翻译；更早的系统用 SFSpeechRecognizer，翻译需要大模型。
// Translation 框架在 macOS 15 之前不存在，必须弱链接，否则在旧系统上无法启动。
let sdkVersion = Context.environment["LIVESUB_SDK_VERSION"] ?? "27.0"

let package = Package(
    name: "LiveSub",
    platforms: [.macOS("14.2")],
    targets: [
        // 纯逻辑：分句、Claude stream-json 协议解析、提示词、记录格式。无系统框架依赖，可单测。
        .target(name: "LiveSubCore"),
        // 应用本体：音频采集、语音识别、翻译进程、悬浮字幕 UI。
        .executableTarget(
            name: "LiveSub",
            dependencies: ["LiveSubCore"],
            linkerSettings: [
                .unsafeFlags(["-Xlinker", "-platform_version", "-Xlinker", "macos", "-Xlinker", "14.2", "-Xlinker", sdkVersion,
                              "-Xlinker", "-weak_framework", "-Xlinker", "Translation"]),
            ]
        ),
        .testTarget(name: "LiveSubCoreTests", dependencies: ["LiveSubCore"]),
    ],
    swiftLanguageModes: [.v5]
)
