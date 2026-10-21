import Foundation

/// 从“应用程序”或菜单栏打开的 App 不会读取 `~/.zshrc` 等终端配置。用户在终端里通过环境变量
/// 登录 Claude Code（例如 `CLAUDE_CODE_OAUTH_TOKEN`、`ANTHROPIC_API_KEY`，或者 Bedrock、Vertex、代理设置）时，
/// App 启动的 `claude` 会显示未登录。这里解析终端环境，挑出这些变量补给 `claude`。
public enum ShellEnvironment {
    /// 终端配置启动时可能先打印别的内容，环境变量从这个标记之后开始。
    public static let marker = "__LIVESUB_ENV_BEGIN__"

    /// 在登录、交互式 shell 里运行的命令：先打印标记，再以 NUL 分隔打印全部环境变量（值里可能有换行）。
    public static let command = "printf '%s' '\(marker)'; /usr/bin/env -0"

    /// 解析 `command` 的输出。
    public static func parse(_ data: Data) -> [String: String] {
        let text = String(decoding: data, as: UTF8.self)
        guard let range = text.range(of: marker, options: .backwards) else { return [:] }
        var env: [String: String] = [:]
        for entry in text[range.upperBound...].split(separator: "\0") {
            guard let eq = entry.firstIndex(of: "="), eq != entry.startIndex else { continue }
            env[String(entry[..<eq])] = String(entry[entry.index(after: eq)...])
        }
        return env
    }

    static let prefixes = ["CLAUDE_", "ANTHROPIC_", "AWS_", "CLOUD_ML_", "GOOGLE_", "VERTEX_"]
    static let names: Set<String> = ["HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "ALL_PROXY",
                                     "https_proxy", "http_proxy", "no_proxy", "all_proxy", "NODE_EXTRA_CA_CERTS"]
    /// 在 Claude Code 终端里启动时才有的嵌套会话标记，不能带给新的 `claude`。
    public static let sessionMarkers: Set<String> = ["CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT", "CLAUDE_PID"]

    /// 只挑和 Claude Code 登录、接口、代理有关的变量。
    public static func relevant(_ env: [String: String]) -> [String: String] {
        env.filter { key, _ in
            !sessionMarkers.contains(key) && (names.contains(key) || prefixes.contains { key.hasPrefix($0) })
        }
    }

    /// 把终端里的变量补进 App 自己的环境；App 环境里已有的值优先。
    public static func merge(_ base: [String: String], shell: [String: String]) -> [String: String] {
        base.merging(relevant(shell)) { current, _ in current }
    }
}
