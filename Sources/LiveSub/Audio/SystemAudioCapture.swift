import AppKit
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
    /// “系统录音”权限被拒绝了：系统不报错，只送来静音，用户会以为程序没反应。定时检查在 queue 上写，界面线程读。
    private let permission = PermissionState()
    private var checkTimer: DispatchSourceTimer?

    private final class PermissionState: @unchecked Sendable {
        let lock = NSLock()
        var denied = false
    }

    func start(onBuffer: @escaping (AVAudioPCMBuffer) -> Void) throws {
        self.onBuffer = onBuffer
        permission.lock.withLock { permission.denied = Self.permissionDenied }
        try startTap()
        listenForOutputDeviceChanges()
        startCheckingPermission()
    }

    func stop() {
        checkTimer?.cancel()
        checkTimer = nil
        stopListening()
        stopTap()
        onBuffer = nil
    }

    var blockedSuspected: Bool {
        permission.lock.withLock { permission.denied }
    }

    /// 打开“隐私与安全性”里管系统录音的那一页（macOS 15 叫“屏幕与系统录音”）。
    static func openPrivacySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(url)
        }
    }

    // MARK: - 权限

    private typealias Preflight = @convention(c) (CFString, CFDictionary?) -> Int

    /// 系统的非公开接口 TCCAccessPreflight：0 已允许，1 已拒绝，2 还没问过（开源的系统录音示例 AudioCap 也这样查）。
    /// 公开接口查不到“系统录音”权限；接口不存在时当作不知道，不提示。
    private static let preflight: Preflight? = {
        guard let handle = dlopen("/System/Library/PrivateFrameworks/TCC.framework/Versions/A/TCC", RTLD_NOW),
              let symbol = dlsym(handle, "TCCAccessPreflight") else { return nil }
        return unsafeBitCast(symbol, to: Preflight.self)
    }()

    private static var permissionDenied: Bool {
        preflight?("kTCCServiceAudioCapture" as CFString, nil) == 1
    }

    /// 每 2 秒查一次权限；从拒绝变成允许时重建 tap，字幕自动继续。
    private func startCheckingPermission() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 2, repeating: 2)
        timer.setEventHandler { [weak self] in
            guard let self, self.onBuffer != nil else { return }
            let denied = Self.permissionDenied
            let wasDenied = self.permission.lock.withLock {
                defer { self.permission.denied = denied }
                return self.permission.denied
            }
            if wasDenied, !denied {
                self.stopTap()
                try? self.startTap()
            }
        }
        timer.resume()
        checkTimer = timer
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
