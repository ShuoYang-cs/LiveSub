import Foundation
import LiveSubCore

/// 把每一句原文 + 译文实时追加写入 Markdown 文件，崩溃或断电也不会丢失之前的记录。
final class TranscriptStore {
    let fileURL: URL
    let started: Date

    init(folderPath: String, started: Date, source: LanguageOption, target: LanguageOption) {
        self.started = started
        let stamp = TranscriptFormat.fileStampFormatter.string(from: started)
        let name = "\(stamp) \(L("字幕记录")).md"
        let preferred = URL(fileURLWithPath: (folderPath as NSString).expandingTildeInPath, isDirectory: true)
        let fallback = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("LiveSub/Transcripts", isDirectory: true)

        let header = TranscriptFormat.header(title: "\(stamp) \(L("字幕记录"))", started: started, source: source, target: target)
        var chosen = fallback.appendingPathComponent(name)
        for folder in [preferred, fallback] {
            let url = folder.appendingPathComponent(name)
            do {
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                try header.write(to: url, atomically: true, encoding: .utf8)
                chosen = url
                break
            } catch {
                continue
            }
        }
        fileURL = chosen
    }

    func append(_ text: String) {
        guard let handle = try? FileHandle(forWritingTo: fileURL) else { return }
        defer { try? handle.close() }
        _ = try? handle.seekToEnd()
        try? handle.write(contentsOf: Data(text.utf8))
    }

    var notesURL: URL {
        fileURL.deletingLastPathComponent()
            .appendingPathComponent(fileURL.deletingPathExtension().lastPathComponent.replacingOccurrences(of: L("字幕记录"), with: L("笔记")) + ".md")
    }
}
