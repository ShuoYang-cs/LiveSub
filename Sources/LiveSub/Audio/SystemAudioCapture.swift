import AVFoundation
import AudioToolbox
import CoreAudio

/// 用 Core Audio process tap（macOS 14.4+）录制电脑正在播放的声音。
/// 只需要“系统录音”权限，不需要屏幕录制权限，也不会改变声音输出。
final class SystemAudioCapture: AudioCapture {
    let label = L("系统音频")

    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)
    private var procID: AudioDeviceIOProcID?
    private let queue = DispatchQueue(label: "LiveSub.SystemAudio", qos: .userInitiated)
    private var onBuffer: ((AVAudioPCMBuffer) -> Void)?
    private var deviceListener: AudioObjectPropertyListenerBlock?

    func start(onBuffer: @escaping (AVAudioPCMBuffer) -> Void) throws {
        self.onBuffer = onBuffer
        try startTap()
        listenForOutputDeviceChanges()
    }

    func stop() {
        stopListening()
        stopTap()
        onBuffer = nil
    }

    // MARK: - Tap

    private func startTap() throws {
        let description = CATapDescription(monoGlobalTapButExcludeProcesses: [])
        description.uuid = UUID()
        description.name = "LiveSub System Audio"
        description.isPrivate = true
        description.muteBehavior = .unmuted

        var tap = AudioObjectID(kAudioObjectUnknown)
        try check(AudioHardwareCreateProcessTap(description, &tap), "创建系统音频采集")
        tapID = tap

        var asbd = try readProperty(tap, kAudioTapPropertyFormat, as: AudioStreamBasicDescription.self)
        guard let format = AVAudioFormat(streamDescription: &asbd) else {
            throw CaptureError.coreAudio("读取系统音频格式", -1)
        }

        let outputDevice = try readProperty(AudioObjectID(kAudioObjectSystemObject),
                                            kAudioHardwarePropertyDefaultOutputDevice, as: AudioDeviceID.self)
        let outputUID = try readStringProperty(outputDevice, kAudioDevicePropertyDeviceUID)

        let aggregate: [String: Any] = [
            kAudioAggregateDeviceNameKey: "LiveSub Tap",
            kAudioAggregateDeviceUIDKey: UUID().uuidString,
            kAudioAggregateDeviceMainSubDeviceKey: outputUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: outputUID]],
            kAudioAggregateDeviceTapListKey: [[
                kAudioSubTapDriftCompensationKey: true,
                kAudioSubTapUIDKey: description.uuid.uuidString,
            ]],
        ]
        var agg = AudioObjectID(kAudioObjectUnknown)
        try check(AudioHardwareCreateAggregateDevice(aggregate as CFDictionary, &agg), "创建聚合音频设备")
        aggregateID = agg

        let deliver = onBuffer
        try check(AudioDeviceCreateIOProcIDWithBlock(&procID, agg, queue) { _, inputData, _, _, _ in
            guard let buffer = AVAudioPCMBuffer(pcmFormat: format, bufferListNoCopy: inputData, deallocator: nil),
                  buffer.frameLength > 0
            else { return }
            deliver?(buffer)
        }, "注册音频回调")
        try check(AudioDeviceStart(agg, procID), "启动系统音频采集")
    }

    private func stopTap() {
        if aggregateID != kAudioObjectUnknown {
            AudioDeviceStop(aggregateID, procID)
            if let procID { AudioDeviceDestroyIOProcID(aggregateID, procID) }
            AudioHardwareDestroyAggregateDevice(aggregateID)
        }
        if tapID != kAudioObjectUnknown {
            AudioHardwareDestroyProcessTap(tapID)
        }
        procID = nil
        aggregateID = AudioObjectID(kAudioObjectUnknown)
        tapID = AudioObjectID(kAudioObjectUnknown)
    }

    // MARK: - 输出设备切换（例如插拔耳机）时重建 tap

    private var defaultOutputAddress = AudioObjectPropertyAddress(
        mSelector: kAudioHardwarePropertyDefaultOutputDevice,
        mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain
    )

    private func listenForOutputDeviceChanges() {
        let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            guard let self, self.onBuffer != nil else { return }
            self.stopTap()
            try? self.startTap()
        }
        deviceListener = block
        AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &defaultOutputAddress, queue, block)
    }

    private func stopListening() {
        guard let block = deviceListener else { return }
        AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &defaultOutputAddress, queue, block)
        deviceListener = nil
    }

    // MARK: - Core Audio 辅助

    private func check(_ status: OSStatus, _ step: String) throws {
        guard status == noErr else { throw CaptureError.coreAudio(step, status) }
    }

    private func readProperty<T>(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector, as: T.Type) throws -> T {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var size = UInt32(MemoryLayout<T>.size)
        let pointer = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<T>.alignment)
        defer { pointer.deallocate() }
        try check(AudioObjectGetPropertyData(object, &address, 0, nil, &size, pointer), "读取音频属性")
        return pointer.load(as: T.self)
    }

    private func readStringProperty(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) throws -> String {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        var value: Unmanaged<CFString>?
        try check(AudioObjectGetPropertyData(object, &address, 0, nil, &size, &value), "读取音频设备")
        guard let value else { throw CaptureError.coreAudio("读取音频设备", -1) }
        return value.takeRetainedValue() as String
    }
}
