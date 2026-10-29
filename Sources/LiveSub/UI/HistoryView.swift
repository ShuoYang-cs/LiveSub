import AppKit
import LiveSubCore
import SwiftUI

/// 字幕记录窗口（设置 › 记录 › 字幕记录）：本次的字幕，或者记录文件夹里以前的某一份（只读）。
/// 以前只显示本次的：重新打开 App 后窗口是空的，文件夹里明明有记录（2026-10-28 用户提出）。
@MainActor @Observable
final class HistoryModel {
    enum Selection: Hashable {
        case current
        case file(URL)
    }

    /// 记录文件夹里以前的记录（不含本次的），最新的在前面。
    private(set) var files: [URL] = []
    /// 选中的以前那份记录里的字幕。
    private(set) var pastLines: [SubtitleLine] = []
    var selection: Selection = .current {
        didSet { if selection != oldValue { load() } }
    }
    /// 打开窗口时本次还没有字幕，自动显示了最近的一份：本次有了字幕就换回来。
    @ObservationIgnored private var autoSelected = false
    private let controller: SubtitleController

    init(controller: SubtitleController) {
        self.controller = controller
    }

    var folder: URL {
        URL(fileURLWithPath: (controller.settings.transcriptFolder as NSString).expandingTildeInPath, isDirectory: true)
    }

    /// 在后台读：记录文件夹在“文稿”里时，第一次读系统会弹窗问权限，这期间不能卡住界面（同 RecordingsModel）。
    func reload() {
        let folder = folder
        let current = controller.transcriptURL?.standardizedFileURL
        Task {
            let found = await Task.detached(priority: .userInitiated) { Self.scan(folder) }.value
            files = found.filter { $0.standardizedFileURL != current }
            if case .file(let url) = selection, !files.contains(url) { selection = .current }
            if selection == .current, controller.lines.isEmpty, let latest = files.first {
                selection = .file(latest)
                autoSelected = true
            }
        }
    }

    /// 本次有了第一句字幕。
    func currentStarted() {
        guard autoSelected else { return }
        autoSelected = false
        selection = .current
    }

    /// 菜单里的名字：文件名去掉“字幕记录”，剩下开始时间。
    static func title(of url: URL) -> String {
        url.deletingPathExtension().lastPathComponent
            .replacingOccurrences(of: "字幕记录", with: "")
            .replacingOccurrences(of: "Transcript", with: "")
            .trimmingCharacters(in: .whitespaces)
    }

    private func load() {
        guard case .file(let url) = selection else {
            pastLines = []
            return
        }
        Task {
            let entries = await Task.detached(priority: .userInitiated) {
                TranscriptFormat.parse((try? String(contentsOf: url, encoding: .utf8)) ?? "")
            }.value
            guard selection == .file(url) else { return }
            // 文件里只有“时:分:秒”，窗口里也只显示这个。
            let day = Calendar.current.startOfDay(for: Date())
            pastLines = entries.map { entry in
                SubtitleLine(time: day.addingTimeInterval(TimeInterval(entry.seconds)), sourceLabel: entry.label,
                             source: entry.source, translation: entry.translation, state: .done, originalSource: entry.original)
            }
        }
    }

    /// 只列有字幕的记录（开始后没说话就停了的只有标题）；英文界面下写的文件名是“Transcript”。
    private nonisolated static func scan(_ folder: URL) -> [URL] {
        let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
        return files.filter { url in
            let name = url.lastPathComponent
            guard url.pathExtension.lowercased() == "md", name.contains("字幕记录") || name.contains("Transcript"),
                  let handle = try? FileHandle(forReadingFrom: url) else { return false }
            defer { try? handle.close() }
            let head = (try? handle.read(upToCount: 4096)).map { String(decoding: $0, as: UTF8.self) } ?? ""
            return head.contains("\n**[")
        }
        // 文件名以“年-月-日 时.分”开头，按名字倒排就是最新的在前面。
        .sorted { $0.lastPathComponent > $1.lastPathComponent }
    }
}

struct HistoryView: View {
    let controller: SubtitleController
    @Bindable var model: HistoryModel

    private var pastFile: URL? {
        if case .file(let url) = model.selection { url } else { nil }
    }

    var body: some View {
        VStack(spacing: 0) {
            if !model.files.isEmpty {
                HStack {
                    Picker("记录", selection: $model.selection) {
                        Text("本次").tag(HistoryModel.Selection.current)
                        Divider()
                        ForEach(model.files, id: \.self) { url in
                            Text(HistoryModel.title(of: url)).tag(HistoryModel.Selection.file(url))
                        }
                    }
                    .pickerStyle(.menu)
                    .fixedSize()
                    Spacer()
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                Divider()
            }
            if pastFile == nil, controller.lines.isEmpty {
                Text(model.files.isEmpty ? L("本次还没有字幕。") : L("本次还没有字幕。以前的记录在左上角的菜单里。"))
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(30)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                transcript(pastFile == nil ? controller.lines : model.pastLines, followsLatest: pastFile == nil)
            }
            Divider()
            if let pastFile { pastBar(pastFile) } else { currentBar }
        }
        .frame(minWidth: 520, minHeight: 420)
        .onAppear { model.reload() }
        // 窗口留着不关，回到前面时可能已经多了一份记录（开始了新的记录、重新打开过 App）。
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in model.reload() }
        .onChange(of: controller.lines.isEmpty) { _, empty in
            if empty { model.reload() } else { model.currentStarted() }
        }
    }

    private func transcript(_ lines: [SubtitleLine], followsLatest: Bool) -> some View {
        ScrollViewReader { proxy in
            List(lines) { line in
                VStack(alignment: .leading, spacing: 3) {
                    HStack(spacing: 6) {
                        Text(line.time, format: .dateTime.hour().minute().second())
                            .font(.caption.monospacedDigit())
                            .foregroundStyle(.secondary)
                        if let label = line.sourceLabel {
                            Text(label).font(.caption).foregroundStyle(.secondary)
                        }
                        if let note = line.note {
                            Text(note).font(.caption).foregroundStyle(.orange)
                        }
                    }
                    Text(line.source).textSelection(.enabled)
                    if let original = line.originalSource, original != line.source {
                        Text(L("识别原文：%@", original)).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                    }
                    if !line.translation.isEmpty {
                        Text(line.translation).font(.body.weight(.medium)).textSelection(.enabled)
                    }
                }
                .padding(.vertical, 2)
                .id(line.id)
            }
            .onChange(of: lines.count) {
                if followsLatest, let last = lines.last { proxy.scrollTo(last.id, anchor: .bottom) }
            }
        }
    }

    private var currentBar: some View {
        HStack {
            Text(controller.status).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            Spacer()
            // 菜单栏图标去掉后（2026-10-22），记录相关的操作都在这里。
            Button("在 Finder 中显示") { openTranscriptFolder() }
            Menu("导出字幕") {
                ForEach(SubtitleFileFormat.allCases, id: \.self) { format in
                    Button(format.menuTitle) { controller.exportSubtitles(format: format) }
                }
            }
            .fixedSize()
            .disabled(controller.lines.isEmpty)
            Button("开始新的记录") { controller.newSession() }
                .disabled(controller.lines.isEmpty)
            notesButton { controller.generateNotes() }
                .disabled(controller.notesInProgress || controller.lines.isEmpty)
        }
        .padding(10)
    }

    /// 以前的记录：没有音频时间，不能导出字幕；可以生成笔记（存在这份记录旁边）。
    private func pastBar(_ url: URL) -> some View {
        HStack {
            Text(controller.notesInProgress ? controller.status : L("共 %d 句", model.pastLines.count))
                .font(.caption).foregroundStyle(.secondary).lineLimit(1)
            Spacer()
            Button("在 Finder 中显示") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
            notesButton { controller.generateNotes(lines: model.pastLines, transcriptURL: url) }
                .disabled(controller.notesInProgress || model.pastLines.isEmpty)
        }
        .padding(10)
    }

    private func notesButton(_ action: @escaping () -> Void) -> some View {
        Button(controller.notesInProgress ? L("正在生成…") : L("生成笔记"), action: action)
    }

    /// 有本次的记录文件就在 Finder 里选中它，否则打开记录文件夹。
    private func openTranscriptFolder() {
        if let url = controller.transcriptURL {
            NSWorkspace.shared.activateFileViewerSelecting([url])
        } else {
            try? FileManager.default.createDirectory(at: model.folder, withIntermediateDirectories: true)
            NSWorkspace.shared.open(model.folder)
        }
    }
}
