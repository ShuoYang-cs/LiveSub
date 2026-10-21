import Foundation
import Testing
@testable import LiveSubCore

@Suite struct ShellEnvironmentTests {
    @Test func parsesAfterMarkerAndKeepsNewlines() {
        let output = "Welcome back!\n" + ShellEnvironment.marker
            + "PATH=/usr/bin\0CLAUDE_CODE_OAUTH_TOKEN=tok\0NOTE=line1\nline2\0EMPTY=\0=bad\0"
        let env = ShellEnvironment.parse(Data(output.utf8))
        #expect(env["PATH"] == "/usr/bin")
        #expect(env["CLAUDE_CODE_OAUTH_TOKEN"] == "tok")
        #expect(env["NOTE"] == "line1\nline2")
        #expect(env["EMPTY"] == "")
        #expect(env.count == 4)
    }

    @Test func noMarkerMeansNothing() {
        #expect(ShellEnvironment.parse(Data("PATH=/usr/bin\0".utf8)).isEmpty)
    }

    @Test func picksOnlyClaudeRelatedVariables() {
        let shell = [
            "CLAUDE_CODE_OAUTH_TOKEN": "tok", "ANTHROPIC_API_KEY": "k", "CLAUDE_CODE_USE_BEDROCK": "1",
            "AWS_PROFILE": "work", "HTTPS_PROXY": "http://proxy:8080",
            "CLAUDECODE": "1", "CLAUDE_CODE_ENTRYPOINT": "cli", "PATH": "/x", "TERM": "xterm", "PWD": "/tmp",
        ]
        let picked = ShellEnvironment.relevant(shell)
        #expect(Set(picked.keys) == ["CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY", "CLAUDE_CODE_USE_BEDROCK",
                                     "AWS_PROFILE", "HTTPS_PROXY"])
    }

    @Test func appEnvironmentWins() {
        let merged = ShellEnvironment.merge(["ANTHROPIC_API_KEY": "app", "PATH": "/usr/bin"],
                                            shell: ["ANTHROPIC_API_KEY": "shell", "CLAUDE_CODE_OAUTH_TOKEN": "tok", "PATH": "/x"])
        #expect(merged["ANTHROPIC_API_KEY"] == "app")
        #expect(merged["CLAUDE_CODE_OAUTH_TOKEN"] == "tok")
        #expect(merged["PATH"] == "/usr/bin")
    }
}
