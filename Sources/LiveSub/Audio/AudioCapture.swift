import AVFoundation

/// 音频来源。`onBuffer` 在采集线程上同步调用，buffer 只在回调期间有效，需要立即转换/拷贝。
protocol AudioCapture: AnyObject {
    var label: String { get }
    func start(onBuffer: @escaping (AVAudioPCMBuffer) -> Void) throws
    func stop()
    /// 权限把声音挡住了（系统录音没权限时只送来静音，不报错）。
    var blockedSuspected: Bool { get }
}

extension AudioCapture {
    var blockedSuspected: Bool { false }
}

/// 把非 Sendable 的采集对象交给后台任务启动（采集对象自己负责线程安全）。
struct UncheckedSendable<Value>: @unchecked Sendable {
    let value: Value
    init(_ value: Value) { self.value = value }
}

enum CaptureError: LocalizedError {
    case permissionDenied(String)
    case coreAudio(String, OSStatus)
    case noInput

    var errorDescription: String? {
        switch self {
        case .permissionDenied(let what): L("没有%@权限。请在“系统设置 › 隐私与安全性”中允许 LiveSub。", what)
        case .coreAudio(let step, let status): L("%@失败，错误码 %d。", L(step), Int(status))
        case .noInput: L("找不到可用的麦克风输入设备。")
        }
    }
}

/// 采集的开始、停止和重新开始都在 `queue` 上，所以可以跨线程使用。
final class MicrophoneCapture: AudioCapture, @unchecked Sendable {
    let label = L("麦克风")
    private let engine = AVAudioEngine()
    private let queue = DispatchQueue(label: "LiveSub.MicrophoneCapture")
    // 以下只在 queue 上访问。
    private var running = false
    private var onBuffer: ((AVAudioPCMBuffer) -> Void)?
    private var observer: NSObjectProtocol?

    // 没有“麦克风降噪”（系统语音处理）：它是给通话用的，只留离电脑近的人声，
    // 教室里远处老师的声音几乎全被去掉，自动增益再把剩下的低频噪音放大，识别不出任何字（2026-10-29 课堂实测）。

    static func requestPermission() async -> Bool {
        await AVAudioApplication.requestRecordPermission()
    }

    func start(onBuffer: @escaping (AVAudioPCMBuffer) -> Void) throws {
        try queue.sync {
            self.onBuffer = onBuffer
            try run()
            running = true
        }
        // 别的程序打开麦克风的语音处理（Teams、FaceTime 等通话软件）、换了输入设备时，系统会改麦克风的声道数或采样率，
        // AVAudioEngine 随之自己停下，不报错，字幕从此“无声音”（2026-10-29 实测）。收到通知就按新的格式重新开始。
        let observer = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: engine, queue: nil) { [weak self] _ in
            self?.queue.async { self?.restart(attempt: 1) }
        }
        queue.sync { self.observer = observer }
    }

    private func run() throws {
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        guard format.sampleRate > 0, format.channelCount > 0 else { throw CaptureError.noInput }
        let onBuffer = onBuffer
        input.installTap(onBus: 0, bufferSize: 4096, format: format) { buffer, _ in
            onBuffer?(buffer)
        }
        engine.prepare()
        try engine.start()
    }

    /// 设备刚改完配置时可能还在忙（日志里 `Start failed ... error 89`），隔半秒再试，最多 10 次。
    private func restart(attempt: Int) {
        guard running, !engine.isRunning else { return }
        engine.inputNode.removeTap(onBus: 0)
        do {
            try run()
            appLog.notice("microphone: restarted after configuration change (\(self.engine.inputNode.outputFormat(forBus: 0), privacy: .public))")
        } catch {
            appLog.error("microphone: restart \(attempt) failed: \(error.localizedDescription, privacy: .public)")
            guard attempt < 10 else { return }
            queue.asyncAfter(deadline: .now() + 0.5) { [weak self] in self?.restart(attempt: attempt + 1) }
        }
    }

    /// 自检用（`--test-mic-restart`）：像系统改了麦克风配置那样停下引擎、发出通知。
    func simulateConfigurationChange() {
        queue.sync { engine.stop() }
        NotificationCenter.default.post(name: .AVAudioEngineConfigurationChange, object: engine)
    }

    func stop() {
        queue.sync {
            if let observer { NotificationCenter.default.removeObserver(observer) }
            observer = nil
            guard running else { return }
            engine.inputNode.removeTap(onBus: 0)
            engine.stop()
            running = false
        }
    }
}
