import Foundation
import LiveSubCore

/// 把每一句原文 + 译文实时追加写入 Markdown 文件，崩溃或断电也不会丢失之前的记录。
/// 每次记录一个文件夹（`RecordFolders`）：字幕记录、AI 助手对话、录音、笔记都放在里面。
final class TranscriptStore {
    let fileURL: URL
    let started: Date

    init(folderPath: String, started: Date, source: LanguageOption, target: LanguageOption) {
        self.started = started
        let stamp = TranscriptFormat.fileStampFormatter.string(from: started)
        let preferred = URL(fileURLWithPath: (folderPath as NSString).expandingTildeInPath, isDirectory: true)
        let fallback = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("LiveSub/Transcripts", isDirectory: true)

        var chosen: URL?
        for root in [preferred, fallback] {
            RecordFolderMigration.run(root)
            let base = RecordFolders.folderName(stamp: stamp) {
                FileManager.default.fileExists(atPath: root.appendingPathComponent($0).path)
            }
            let folder = root.appendingPathComponent(base, isDirectory: true)
            let url = folder.appendingPathComponent("\(base) \(L("字幕记录")).md")
            let header = TranscriptFormat.header(title: "\(base) \(L("字幕记录"))", started: started, source: source, target: target)
            do {
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                try header.write(to: url, atomically: true, encoding: .utf8)
                chosen = url
                break
            } catch {
                continue
            }
        }
        fileURL = chosen ?? fallback.appendingPathComponent("\(stamp) \(L("字幕记录")).md")
    }

    func append(_ text: String) {
        guard let handle = try? FileHandle(forWritingTo: fileURL) else { return }
        defer { try? handle.close() }
        _ = try? handle.seekToEnd()
        try? handle.write(contentsOf: Data(text.utf8))
    }

    /// 本次记录的文件夹。
    var folderURL: URL { fileURL.deletingLastPathComponent() }

    var notesURL: URL { Self.notesURL(for: fileURL) }

    /// 同一次记录的其他文件：把字幕记录文件名里的“字幕记录”换成别的词，例如“2026-10-28 13.11 笔记.md”。
    static func sibling(of transcript: URL, kind: String, extension ext: String) -> URL {
        transcript.deletingLastPathComponent()
            .appendingPathComponent(transcript.deletingPathExtension().lastPathComponent
                .replacingOccurrences(of: L("字幕记录"), with: kind) + "." + ext)
    }

    /// 笔记和字幕记录放在一起：“2026-10-28 13.11 笔记.md”。
    static func notesURL(for transcript: URL) -> URL {
        sibling(of: transcript, kind: L("笔记"), extension: "md")
    }

    /// AI 助手对话：“2026-11-23 13.07 AI 助手对话.md”。
    var assistantLogURL: URL { Self.sibling(of: fileURL, kind: L("AI 助手对话"), extension: "md") }
}

/// 把旧版平铺在记录文件夹里的字幕记录、录音、笔记搬进各自的文件夹（`RecordFolders.migrationPlan`）。
/// 启动时（用过 App 的才跑）、新建记录前、打开字幕记录和录音窗口前各跑一次；没有要搬的文件时只是列一次目录。
enum RecordFolderMigration {
    private static let lock = NSLock()

    /// 两个 LiveSub 同时开着（比如测新版时旧版还在上课）时，另一个正在写的文件不能搬：
    /// 最近两分钟改过的文件这次先不动，下次再搬。
    static let busyInterval: TimeInterval = 120

    /// 先搬旧文件，再列出记录文件夹最上层和每次记录文件夹里的文件（字幕记录窗口、录音窗口用）。
    /// 最上层还会有搬不了的旧文件（同名、正被另一个 LiveSub 写着），也要列出来。
    static func recordFiles(in root: URL) -> [URL] {
        run(root)
        let fm = FileManager.default
        let top = (try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isDirectoryKey])) ?? []
        return top.flatMap { url -> [URL] in
            guard (try? url.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory == true else { return [url] }
            return (try? fm.contentsOfDirectory(at: url, includingPropertiesForKeys: nil)) ?? []
        }
    }

    static func run(_ root: URL) {
        lock.lock()
        defer { lock.unlock() }
        let fm = FileManager.default
        let keys: [URLResourceKey] = [.isDirectoryKey, .contentModificationDateKey]
        guard let entries = try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: keys) else { return }
        let now = Date()
        let files = entries.filter { url in
            guard let values = try? url.resourceValues(forKeys: Set(keys)), values.isDirectory != true else { return false }
            return now.timeIntervalSince(values.contentModificationDate ?? .distantPast) > busyInterval
        }.map(\.lastPathComponent)
        // 中文和英文界面写的文件名都认。
        let keywords = Array(Set(["字幕记录", "录音", "笔记", "Transcript", "Record", "Notes", L("字幕记录"), L("录音"), L("笔记")]))
        let plan = RecordFolders.migrationPlan(files: files, keywords: keywords) { folder, file in
            fm.fileExists(atPath: root.appendingPathComponent(folder).appendingPathComponent(file).path)
        }
        guard !plan.isEmpty else { return }
        var moved = 0
        for move in plan {
            let folder = root.appendingPathComponent(move.folder, isDirectory: true)
            do {
                try fm.createDirectory(at: folder, withIntermediateDirectories: true)
                try fm.moveItem(at: root.appendingPathComponent(move.file), to: folder.appendingPathComponent(move.file))
                moved += 1
            } catch {
                appLog.error("record folders: could not move \(move.file, privacy: .public): \(error.localizedDescription, privacy: .public)")
            }
        }
        appLog.notice("record folders: moved \(moved)/\(plan.count) old files into record folders")
    }
}
