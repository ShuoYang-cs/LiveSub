import AVFoundation

/// 录音：把一路音源采集到的声音存成 AAC（.m4a），和字幕记录放在一起。
/// 采集回调里只复制数据（buffer 只在回调期间有效），编码写盘放在自己的串行队列上，不拖慢识别。
final class AudioRecorder: @unchecked Sendable {
    let url: URL
    private let queue = DispatchQueue(label: "LiveSub.AudioRecorder", qos: .utility)
    // 以下只在 queue 上访问。
    private var file: AVAudioFile?
    private var failed = false

    init(url: URL) {
        self.url = url
    }

    /// 采集线程上调用。
    func append(_ buffer: AVAudioPCMBuffer) {
        guard let copy = Self.copy(buffer) else { return }
        queue.async { self.write(copy) }
    }

    /// 写完已经收到的声音后关闭文件（m4a 要关闭后才能播放）。
    func finish() {
        queue.sync { file = nil }
    }

    private func write(_ buffer: AVAudioPCMBuffer) {
        guard !failed else { return }
        do {
            if file == nil {
                let format = buffer.format
                // 讲课、会议的人声：64 kbps 足够清楚，一小时约 29 MB。
                let settings: [String: Any] = [
                    AVFormatIDKey: kAudioFormatMPEG4AAC,
                    AVSampleRateKey: format.sampleRate,
                    AVNumberOfChannelsKey: format.channelCount,
                    AVEncoderBitRateKey: 64_000,
                ]
                file = try AVAudioFile(forWriting: url, settings: settings,
                                       commonFormat: format.commonFormat, interleaved: format.isInterleaved)
                appLog.notice("recording: \(self.url.lastPathComponent, privacy: .public)")
            }
            try file?.write(from: buffer)
        } catch {
            failed = true
            file = nil
            appLog.error("recording failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    private static func copy(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        guard buffer.frameLength > 0,
              let copy = AVAudioPCMBuffer(pcmFormat: buffer.format, frameCapacity: buffer.frameLength) else { return nil }
        copy.frameLength = buffer.frameLength
        let source = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: buffer.audioBufferList))
        let target = UnsafeMutableAudioBufferListPointer(copy.mutableAudioBufferList)
        for (from, to) in zip(source, target) {
            guard let src = from.mData, let dst = to.mData else { return nil }
            memcpy(dst, src, Int(min(from.mDataByteSize, to.mDataByteSize)))
        }
        return copy
    }
}

/// 采集线程和主线程之间交接录音器：录音可以在字幕运行中途打开或关掉。
final class RecorderSlot: @unchecked Sendable {
    private let lock = NSLock()
    private var recorder: AudioRecorder?

    func set(_ recorder: AudioRecorder?) -> AudioRecorder? {
        lock.withLock {
            let old = self.recorder
            self.recorder = recorder
            return old
        }
    }

    func append(_ buffer: AVAudioPCMBuffer) {
        lock.withLock { recorder }?.append(buffer)
    }
}
