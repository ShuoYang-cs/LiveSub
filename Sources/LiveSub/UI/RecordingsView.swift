import AVFoundation
import AppKit
import LiveSubCore
import SwiftUI

/// 录音记录（设置 › 记录 › 录音记录）：记录文件夹里录下的声音，最新的在最上面。
/// 点“播放”用系统默认的播放器打开；有同时间的字幕记录时可以直接打开它。
@MainActor @Observable
final class RecordingsModel {
    struct Recording: Identifiable, Sendable {
        var id: URL { url }
        let url: URL
        let created: Date
        let duration: TimeInterval?
        let bytes: Int
        /// 同一次的字幕记录（录音文件名把“字幕记录”换成了“录音”，“两者”时后面还带音源）。
        let transcript: URL?
    }

    private(set) var recordings: [Recording] = []
    private let settings: AppSettings

    init(settings: AppSettings) {
        self.settings = settings
    }

    var folder: URL { URL(fileURLWithPath: (settings.transcriptFolder as NSString).expandingTildeInPath, isDirectory: true) }

    /// 在后台读：记录文件夹在“文稿”里时，第一次读系统会弹窗问权限，这期间不能卡住界面（2026-10-22 实测会卡住）。
    func reload() {
        let folder = folder
        Task {
            let found = await Task.detached(priority: .userInitiated) { Self.scan(folder) }.value
            recordings = found
        }
    }

    private nonisolated static func scan(_ folder: URL) -> [Recording] {
        let keys: [URLResourceKey] = [.creationDateKey, .fileSizeKey]
        let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: keys)) ?? []
        return files.filter { $0.pathExtension.lowercased() == "m4a" }.map { url in
            let values = try? url.resourceValues(forKeys: Set(keys))
            let duration = (try? AVAudioFile(forReading: url)).map { Double($0.length) / $0.processingFormat.sampleRate }
            return Recording(url: url, created: values?.creationDate ?? .distantPast, duration: duration,
                             bytes: values?.fileSize ?? 0, transcript: transcript(for: url))
        }
        .sorted { $0.created > $1.created }
    }

    private nonisolated static func transcript(for recording: URL) -> URL? {
        var name = recording.deletingPathExtension().lastPathComponent
        // 去掉“（麦克风）”这类音源后缀和重名时加的序号。
        if let paren = name.firstIndex(of: "（") { name = String(name[..<paren]) }
        name = name.replacingOccurrences(of: L("录音"), with: L("字幕记录")).trimmingCharacters(in: .whitespaces)
        let url = recording.deletingLastPathComponent().appendingPathComponent(name + ".md")
        return FileManager.default.fileExists(atPath: url.path) ? url : nil
    }
}

struct RecordingsView: View {
    let model: RecordingsModel

    var body: some View {
        VStack(spacing: 0) {
            if model.recordings.isEmpty {
                Text("还没有录音。字幕运行时点快捷按钮里的录音按钮，听到的声音就会存在这里。")
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(30)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List(model.recordings) { recording in
                    row(recording)
                }
            }
            Divider()
            HStack {
                Text(L("共 %d 个录音", model.recordings.count)).font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("打开记录文件夹") { NSWorkspace.shared.open(model.folder) }
                Button("刷新") { model.reload() }
            }
            .padding(10)
        }
        .frame(minWidth: 520, minHeight: 320)
        .onAppear { model.reload() }
        // 打开着的窗口回到前面时（可能刚录完一段）重新读一遍。
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in model.reload() }
    }

    private func row(_ recording: RecordingsModel.Recording) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "waveform").foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 2) {
                Text(recording.url.deletingPathExtension().lastPathComponent).lineLimit(1).truncationMode(.middle)
                Text(details(recording)).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if let transcript = recording.transcript {
                Button("字幕记录") { NSWorkspace.shared.open(transcript) }
            }
            Button("播放") { NSWorkspace.shared.open(recording.url) }
            Button { NSWorkspace.shared.activateFileViewerSelecting([recording.url]) } label: {
                Image(systemName: "folder")
            }
            .help(L("在 Finder 中显示"))
        }
        .padding(.vertical, 3)
    }

    private func details(_ recording: RecordingsModel.Recording) -> String {
        var parts: [String] = []
        if let duration = recording.duration {
            let s = Int(duration.rounded())
            parts.append(s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60) : String(format: "%d:%02d", s / 60, s % 60))
        }
        parts.append(ByteCountFormatter.string(fromByteCount: Int64(recording.bytes), countStyle: .file))
        return parts.joined(separator: " · ")
    }
}
