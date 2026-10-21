import AVFoundation

/// 测试用：按真实时间速度“播放”一个音频文件，代替麦克风/系统声音。
/// 通过环境变量 LIVESUB_TEST_AUDIO_FILE 启用，方便在没有声音输入时验证整条链路。
final class FileCapture: AudioCapture {
    let label = L("测试音频")
    private let url: URL
    private var thread: Thread?

    init(url: URL) {
        self.url = url
    }

    func start(onBuffer: @escaping (AVAudioPCMBuffer) -> Void) throws {
        let file = try AVAudioFile(forReading: url)
        let format = file.processingFormat
        let chunk: AVAudioFrameCount = 4096
        let t = Thread {
            let started = Date()
            var sent: Double = 0
            while !Thread.current.isCancelled, file.framePosition < file.length {
                guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: chunk),
                      (try? file.read(into: buffer)) != nil, buffer.frameLength > 0
                else { break }
                onBuffer(buffer)
                sent += Double(buffer.frameLength) / format.sampleRate
                let ahead = sent - Date().timeIntervalSince(started)
                if ahead > 0 { Thread.sleep(forTimeInterval: ahead) }
            }
            // 文件结束后送一段静音，让识别器确认最后一句。
            if let silence = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(format.sampleRate)) {
                silence.frameLength = silence.frameCapacity
                for _ in 0..<3 where !Thread.current.isCancelled {
                    onBuffer(silence)
                    Thread.sleep(forTimeInterval: 1)
                }
            }
        }
        thread = t
        t.start()
    }

    func stop() {
        thread?.cancel()
        thread = nil
    }
}
