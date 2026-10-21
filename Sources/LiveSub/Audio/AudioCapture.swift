import AVFoundation

/// 音频来源。`onBuffer` 在采集线程上同步调用，buffer 只在回调期间有效，需要立即转换/拷贝。
protocol AudioCapture: AnyObject {
    var label: String { get }
    func start(onBuffer: @escaping (AVAudioPCMBuffer) -> Void) throws
    func stop()
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

final class MicrophoneCapture: AudioCapture {
    let label = L("麦克风")
    private let engine = AVAudioEngine()
    private var running = false
    private let noiseReduction: Bool

    /// - noiseReduction: 打开系统的语音处理（FaceTime 用的那套）：降噪、回声消除、自动增益。
    ///   嘈杂环境、讲者离得远时可能更清楚；但它偏向“离麦克风近的人声”，远处的讲者也可能被压低，所以做成可选项。
    ///   回声消除还能去掉扬声器外放被麦克风再录一遍的声音（“两者同时”模式下的重复字幕）。
    init(noiseReduction: Bool = false) {
        self.noiseReduction = noiseReduction
    }

    static func requestPermission() async -> Bool {
        await AVAudioApplication.requestRecordPermission()
    }

    func start(onBuffer: @escaping (AVAudioPCMBuffer) -> Void) throws {
        let input = engine.inputNode
        if noiseReduction {
            try input.setVoiceProcessingEnabled(true)
            input.isVoiceProcessingAGCEnabled = true
            // 语音处理默认会把其他声音（视频、音乐）压低，这里尽量不压。
            input.voiceProcessingOtherAudioDuckingConfiguration =
                AVAudioVoiceProcessingOtherAudioDuckingConfiguration(enableAdvancedDucking: false, duckingLevel: .min)
        }
        // 打开语音处理后会给出多个声道（麦克风阵列）；后面转成单声道时取第一个，也就是处理后的人声。
        let format = input.outputFormat(forBus: 0)
        guard format.sampleRate > 0, format.channelCount > 0 else { throw CaptureError.noInput }
        input.installTap(onBus: 0, bufferSize: 4096, format: format) { buffer, _ in
            onBuffer(buffer)
        }
        engine.prepare()
        try engine.start()
        running = true
    }

    func stop() {
        guard running else { return }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        running = false
    }
}
