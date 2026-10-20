import AppKit
import Network
import Security
import CryptoKit

private final class FileWire: @unchecked Sendable {
    let connection: NWConnection
    let cipher: SecureChannel
    private let lock = NSLock()
    private var last = ProcessInfo.processInfo.systemUptime
    private var watchdog: DispatchSourceTimer?
    init(_ connection: NWConnection, _ cipher: SecureChannel) {
        self.connection = connection; self.cipher = cipher
        let timer = DispatchSource.makeTimerSource(queue: .global(qos: .utility)); watchdog = timer
        timer.schedule(deadline: .now() + 5, repeating: 5)
        timer.setEventHandler { [weak self] in guard let self else { return }; if self.lock.withLock({ ProcessInfo.processInfo.systemUptime - self.last > 30 }) { self.close() } }; timer.resume()
    }
    func close() { watchdog?.cancel(); connection.cancel() }
    deinit { close() }
    private func touch() { lock.withLock { last = ProcessInfo.processInfo.systemUptime } }
    func rawSend(_ data: Data) async throws {
        try Task.checkCancellation()
        try await withCheckedThrowingContinuation { (done: CheckedContinuation<Void, Error>) in
            connection.send(content: data, completion: .contentProcessed { [self] error in if let error { done.resume(throwing: error) } else { touch(); done.resume() } })
        }
    }
    func rawRead(_ count: Int) async throws -> Data {
        try Task.checkCancellation()
        return try await withCheckedThrowingContinuation { done in
            connection.receive(minimumIncompleteLength: count, maximumLength: count) { [self] bytes, _, _, error in
                if let bytes, bytes.count == count { touch(); done.resume(returning: bytes) }
                else { done.resume(throwing: error ?? FileProtocol.error("File connection closed")) }
            }
        }
    }
    func send(_ command: FileCommand) async throws { try await sendData(FileProtocol.encode(command)) }
    func sendData(_ data: Data) async throws {
        guard data.count + 16 <= SecureChannel.maxFrame, let frame = cipher.seal(data) else { throw FileProtocol.error("File encryption failed") }
        try await rawSend(frame)
    }
    func read() async throws -> Data {
        let prefix = try await rawRead(2); let n = Int(prefix[0]) << 8 | Int(prefix[1])
        guard n >= 17, n <= SecureChannel.maxFrame else { throw FileProtocol.error("Invalid file frame") }
        var frame = prefix + (try await rawRead(n))
        guard let data = cipher.open(from: &frame)?.first else { throw FileProtocol.error("File authentication failed") }; return data
    }
}

final class MacFileTransfer: ObservableObject {
    @Published private(set) var status = "Files off"
    @Published private(set) var incoming = ""
    @Published private(set) var busy = false { didSet { pasteLock.withLock { transferBusy = busy } } }
    @Published private(set) var progress = 0.0
    @Published private(set) var receivedURLs: [URL] = []
    private let server: PeerServer
    private let keyFor: (String) -> String?
    private let board: NSPasteboard
    private let receiveDirectory: URL
    private let historyStore: TransferHistory
    @Published private(set) var history: [TransferRecord] = []
    @Published private(set) var notice = ""
    var onAttention: ((String) -> Void)?
    var beforeRemoteClipboard: (() -> Void)?
    private let marker = UUID().uuidString
    private static let markerType = NSPasteboard.PasteboardType("com.unictrl.remote-files")
    private var blocked: String?
    private var cleanupTimer: Timer?
    private var enabled = false, remoteEnabled: [String: Bool] = [:]
    private var selected: String?
    private var rate = 10
    private struct Source { let offer: FileOffer; let urls: [URL]; let dates: [Date?]; let created: Date }
    private struct Paste { let count: Int; let pid: pid_t? }
    private struct Grant { let bytes: Data; let offer: FileOffer; let sender: Bool; let expires: Date; let rate: Int; let paste: Paste?; let directory: URL }
    private var sources: [String: Source] = [:], offers: [String: FileOffer] = [:]
    private var grant: Grant?, wire: FileWire?, job: Task<Void, Never>?
    private var generation = UUID()
    private let pasteLock = NSLock()
    private var pasteID: String?, pasteCount = 0, pasteScheduled = false, transferBusy = false
    var canReplayPaste: () -> Bool = { true }
    init(server: PeerServer, keyFor: @escaping (String) -> String?, pasteboard: NSPasteboard = .general, receiveDirectory: URL? = nil) {
        self.server = server; self.keyFor = keyFor; self.board = pasteboard
        historyStore = TransferHistory(root: receiveDirectory?.appendingPathComponent("metadata"))
        self.receiveDirectory = receiveDirectory ?? historyStore.cache
        history = historyStore.records
        cleanupTimer = Timer(timeInterval: 300, repeats: true) { [weak self] _ in self?.cleanCache() }
        RunLoop.main.add(cleanupTimer!, forMode: .common)
    }
    deinit { cleanupTimer?.invalidate() }
    var isRemoteFileClipboard: Bool { board.string(forType: Self.markerType) == marker }
    private func attention(_ text: String) { notice = text; status = text; onAttention?(text) }
    func dismissNotice() { notice = "" }
    private func writeIntent() {
        board.clearContents(); board.setString(marker, forType: Self.markerType); onClipboardWritten?()
    }
    func clearHistory() { historyStore.clear(); history = [] }
    func cleanCache(all: Bool = false) {
        guard !busy else { return }
        historyStore.cleanCache(all: all)
        if receivedURLs.contains(where: { !FileManager.default.fileExists(atPath: $0.path) }) {
            receivedURLs = []
            if isRemoteFileClipboard { writeIntent(); attention("Temporary clipboard files expired. Copy the files again, or use the transfer panel.") }
        }
    }
    private func record(_ offer: FileOffer, sent: Bool, result: String) {
        historyStore.add(offer, sent: sent, result: result); history = historyStore.records
    }
    private var ready: Bool { enabled && selected.flatMap { remoteEnabled[$0] } == true }
    func configure(enabled: Bool, selected: String?, rate: Int) {
        let changed = self.enabled != enabled || self.selected != selected
        self.enabled = enabled; self.selected = selected; self.rate = min(100, max(1, rate))
        if changed { reset(); for peer in server.connectedPeers { announce(peer) } }
        if !busy { status = ready ? "Files ready · copy ≤100 MiB · drag ≤1 GiB/file" : "Enable files on both computers" }
    }
    func connected(_ peer: String) { remoteEnabled[peer] = nil; announce(peer) }
    func disconnected(_ peer: String) { remoteEnabled[peer] = nil; if selected == peer { reset(); status = "File connection offline" } }
    private func announce(_ peer: String) {
        var message = MessageWriter(.fileCapabilities); message.u8(1); message.u8(enabled && selected == peer ? 1 : 0); server.send(message, to: peer)
    }
    private func send(_ command: FileCommand) {
        guard let peer = selected, let data = try? FileProtocol.encode(command) else { return }
        var message = MessageWriter(.fileCommand); message.bytes(data.dropFirst()); server.send(message, to: peer)
    }
    func control(_ data: Data, from peer: String) {
        guard selected == peer else { return }
        if data.first == MessageType.fileCapabilities.rawValue {
            guard data.count == 3, data[1] == 1, data[2] <= 1 else { return }
            remoteEnabled[peer] = data[2] == 1
            if !ready { reset() }; if !busy { status = ready ? "Files ready · copy ≤100 MiB · drag ≤1 GiB/file" : "Enable files on both computers" }; return
        }
        guard ready, let command = try? FileProtocol.decode(data) else { return }
        switch command.kind {
        case "offer":
            beforeRemoteClipboard?()
            guard let offer = command.offer, (try? FileProtocol.validate(offer)) != nil else { return }
            offers = offers.filter { $0.value.mode != offer.mode }; offers[offer.id] = offer
            if offer.mode == "clipboard" {
                blocked = nil; writeIntent()
                pasteLock.withLock { pasteID = offer.id; pasteCount = board.changeCount; pasteScheduled = false }
            }
            refreshIncoming(); status = "Files offered · paste or click Receive to transfer"
            if offer.mode == "drop" { onAttention?("Incoming files: " + incoming + ". Open Unictrl to receive.") }
        case "blocked":
            beforeRemoteClipboard?(); localClipboardChanged(notify: false); blocked = command.reason == "too-large" ? "Files exceed 100 MiB. Open Unictrl and drag the files into the transfer panel (up to 1 GiB per file)." : "These files cannot be pasted. Open Unictrl to transfer regular files; zip folders first."
            writeIntent(); pasteLock.withLock { pasteID = "blocked"; pasteCount = board.changeCount }; attention(blocked!)
        case "clear":
            beforeRemoteClipboard?()
            blocked = nil; if isRemoteFileClipboard { writeIntent() }
            offers = offers.filter { $0.value.mode != "clipboard" }; pasteLock.withLock { pasteID = nil; pasteScheduled = false }; refreshIncoming()
        case "request":
            guard let request = command.request, UUID(uuidString: request) != nil, let id = command.id, let source = sources[id], Date().timeIntervalSince(source.created) < 1800 else { return }
            issue(source.offer, sender: true, remoteRate: command.rate ?? 10, paste: nil, request: request)
        case "withdraw":
            guard let id = command.id else { return }; offers[id] = nil
            pasteLock.withLock { if pasteID == id { pasteID = nil; pasteScheduled = false } }; refreshIncoming()
            if id == grant?.offer.id, grant?.sender == false { cancel(notify: false) }
        case "cancel": if command.id == grant?.offer.id { cancel(notify: false) }
        default: break
        }
    }
    func remoteClipboardWritten() { pasteLock.withLock { if pasteID != nil { pasteCount = board.changeCount } } }
    func localClipboardChanged(notify: Bool = true) {
        blocked = nil
        let old = sources.filter { $0.value.offer.mode == "clipboard" }; for id in old.keys { sources[id] = nil }
        pasteLock.withLock { pasteID = nil; pasteScheduled = false }
        offers = offers.filter { $0.value.mode != "clipboard" }; refreshIncoming()
        if notify, ready { send(FileCommand(kind: "clear")) }
    }
    func offer(_ urls: [URL], clipboard: Bool = false) {
        guard ready else { status = "Enable files on both computers"; return }
        do {
            guard (1...32).contains(urls.count) else { throw FileProtocol.error("Choose 1–32 regular files; zip folders first") }
            var entries: [FileEntry] = [], dates: [Date?] = []
            for url in urls {
                let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey, .contentModificationDateKey])
                guard url.isFileURL, values.isRegularFile == true, values.isSymbolicLink != true, let size = values.fileSize else { throw FileProtocol.error("Folders and links are not supported; zip folders first") }
                entries.append(FileEntry(name: url.lastPathComponent, size: Int64(size))); dates.append(values.contentModificationDate)
            }
            let offer = FileOffer(id: UUID().uuidString, mode: clipboard ? "clipboard" : "drop", files: entries); try FileProtocol.validate(offer)
            sources = sources.filter { $0.value.offer.mode != offer.mode }
            sources[offer.id] = Source(offer: offer, urls: urls, dates: dates, created: Date())
            send(FileCommand(kind: "offer", offer: offer)); status = "Offered \(offer.summary) · waiting for receiver"
        } catch {
            let tooLarge = clipboard && urls.reduce(Int64(0), { $0 + Int64((try? $1.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) }) > FileProtocol.clipboardLimit
            let text = tooLarge ? "Files exceed 100 MiB. Open Unictrl and drag the files into the transfer panel (up to 1 GiB per file)." : error.localizedDescription
            if clipboard { send(FileCommand(kind: "blocked", reason: tooLarge ? "too-large" : "unsupported")) }
            attention(text)
        }
    }
    private func refreshIncoming() { incoming = offers.values.sorted { $0.mode > $1.mode }.first.map { $0.summary + " · " + $0.files.prefix(3).map(\.name).joined(separator: ", ") } ?? "" }
    func receive(to directory: URL? = nil) { if let offer = offers.values.sorted(by: { $0.mode > $1.mode }).first { receive(offer, paste: nil, directory: directory) } }
    func tryPaste() -> Bool {
        let state: (String?, Bool) = pasteLock.withLock {
            guard let id = pasteID else { return (nil, false) }; if pasteScheduled || transferBusy { return (id, true) }
            pasteScheduled = true; return (id, false)
        }
        guard let id = state.0 else { return false }; if state.1 { return true }
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            if id == "blocked", let reason = self.blocked { self.pasteLock.withLock { self.pasteScheduled = false }; self.attention(reason); return }
            guard let offer = self.offers[id], self.board.changeCount == self.pasteCount else { self.pasteLock.withLock { self.pasteScheduled = false }; return }
            self.receive(offer, paste: Paste(count: self.board.changeCount, pid: NSWorkspace.shared.frontmostApplication?.processIdentifier))
        }
        return true
    }
    private func receive(_ offer: FileOffer, paste: Paste?, directory: URL? = nil) { if busy { pasteLock.withLock { pasteScheduled = false }; return }; issue(offer, sender: false, remoteRate: rate, paste: paste, directory: directory) }
    private func issue(_ offer: FileOffer, sender: Bool, remoteRate: Int, paste: Paste?, request: String? = nil, directory: URL? = nil) {
        guard ready, !busy else { return }
        var bytes = Data(count: 16)
        guard bytes.withUnsafeMutableBytes({ SecRandomCopyBytes(kSecRandomDefault, 16, $0.baseAddress!) }) == errSecSuccess else { return }
        busy = true; progress = 0; status = "Connecting file transfer…"
        let value = Grant(bytes: bytes, offer: offer, sender: sender, expires: Date().addingTimeInterval(10), rate: FileProtocol.rate(rate, remoteRate), paste: paste, directory: directory ?? receiveDirectory); grant = value
        send(FileCommand(kind: "ticket", id: offer.id, ticket: bytes.base64EncodedString(), rate: value.rate, request: request))
        DispatchQueue.main.asyncAfter(deadline: .now() + 10) { [weak self] in
            guard let self, self.grant?.bytes == bytes, self.wire == nil else { return }; self.cancel(); self.status = "File connection timed out; try Receive again"
        }
    }
    func accept(_ connection: NWConnection) {
        guard ready, wire == nil, let grant, Date() < grant.expires, let peer = selected, let uuid = UUID(uuidString: peer), let key = keyFor(peer) else { connection.cancel(); return }
        let peerBytes = withUnsafeBytes(of: uuid.uuid) { Data($0) }
        let timeout = DispatchWorkItem { connection.cancel() }; DispatchQueue.main.asyncAfter(deadline: .now() + 5, execute: timeout)
        connection.receive(minimumIncompleteLength: 64, maximumLength: 64) { [weak self] data, _, _, error in
            guard let self, self.wire == nil, self.server.isConnected(peer), self.grant?.bytes == grant.bytes, let data, data.count == 64, error == nil,
                  Date() < grant.expires, data.subdata(in: 32..<48) == peerBytes, data.suffix(16) == grant.bytes else { timeout.cancel(); connection.cancel(); return }
            timeout.cancel()
            var salt = Data(count: 32)
            guard salt.withUnsafeMutableBytes({ SecRandomCopyBytes(kSecRandomDefault, 32, $0.baseAddress!) }) == errSecSuccess else { connection.cancel(); return }
            let cipher = SecureChannel(pairingKey: key, clientSalt: Data(data.prefix(32)), serverSalt: salt, isServer: true, context: FileProtocol.context)
            let wire = FileWire(connection, cipher); self.wire = wire
            let source = self.sources[grant.offer.id], generation = self.generation, directory = grant.directory
            self.job = Task.detached(priority: .utility) { [weak self] in
                do {
                    try await wire.rawSend(FileProtocol.reply + salt); try await wire.send(FileCommand(kind: "ready", id: grant.offer.id))
                    let proof = try FileProtocol.decode(await wire.read()); guard proof.kind == "ready", proof.id == grant.offer.id else { throw FileProtocol.error("File authentication failed") }
                    let progress: (Int64) -> Void = { bytes in Task { @MainActor [weak self] in guard let self, self.generation == generation else { return }; self.progress = grant.offer.size == 0 ? 1 : Double(bytes) / Double(grant.offer.size); self.status = "\(grant.sender ? "Sending" : "Receiving") files · \(Int(self.progress * 100))%" } }
                    let received: [URL]
                    if grant.sender {
                        guard let source else { throw FileProtocol.error("File offer expired; copy again") }
                        try await Self.sendFiles(source, wire: wire, rate: grant.rate, progress: progress); received = []
                    } else { received = try await Self.receiveFiles(grant.offer, wire: wire, directory: directory, progress: progress) }
                    wire.close()
                    await MainActor.run { [weak self] in
                        guard let self, self.generation == generation else { return }
                        self.busy = false; self.wire = nil; self.job = nil; self.grant = nil; self.progress = 1
                        self.status = grant.sender ? "Files sent" : grant.paste == nil ? "Files received in your chosen folder" : "Files ready to paste · temporary cache (1 hour)"
                        self.record(grant.offer, sent: grant.sender, result: "Completed")
                        if !received.isEmpty {
                            self.receivedURLs = received; self.offers[grant.offer.id] = nil; self.refreshIncoming()
                            if let paste = grant.paste, self.pasteLock.withLock({ self.pasteID == grant.offer.id }), self.board.changeCount == paste.count {
                                self.board.clearContents()
                                if self.board.writeObjects(received as [NSURL]) {
                                    self.board.setString(self.marker, forType: Self.markerType)
                                    self.onClipboardWritten?()
                                    if self.canReplayPaste(), NSWorkspace.shared.frontmostApplication?.processIdentifier == paste.pid { Self.replayPaste() }
                                } else { self.status = "Files received; use Copy received files to paste" }
                            }
                            self.pasteLock.withLock { if self.pasteID == grant.offer.id { self.pasteID = nil }; self.pasteScheduled = false }
                        }
                    }
                } catch {
                    wire.close()
                    await MainActor.run { [weak self] in guard let self, self.generation == generation else { return }; self.busy = false; self.wire = nil; self.job = nil; self.grant = nil; self.pasteLock.withLock { self.pasteScheduled = false }; self.record(grant.offer, sent: grant.sender, result: "Stopped"); self.attention("File transfer stopped: \(error.localizedDescription)") }
                }
            }
        }
    }
    var onClipboardWritten: (() -> Void)?
    static let pasteTag: Int64 = 0x554E4946494C45
    private static func replayPaste() {
        let source = CGEventSource(stateID: .combinedSessionState)
        for down in [true, false] {
            let event = CGEvent(keyboardEventSource: source, virtualKey: 9, keyDown: down); event?.flags = .maskCommand
            event?.setIntegerValueField(.eventSourceUserData, value: pasteTag); event?.post(tap: .cghidEventTap)
        }
    }
    private static func sendFiles(_ source: Source, wire: FileWire, rate: Int, progress: (Int64) -> Void) async throws {
        var total: Int64 = 0, notified = Date.distantPast
        let start = ProcessInfo.processInfo.systemUptime
        for (index, url) in source.urls.enumerated() {
            let values = try url.resourceValues(forKeys: [.isSymbolicLinkKey, .fileSizeKey, .contentModificationDateKey])
            guard values.isSymbolicLink != true, values.fileSize == Int(source.offer.files[index].size), values.contentModificationDate == source.dates[index] else { throw FileProtocol.error("Source file changed; copy again") }
            let handle = try FileHandle(forReadingFrom: url); defer { try? handle.close() }
            try await wire.send(FileCommand(kind: "begin", index: index)); var hash = SHA256(), offset: Int64 = 0
            while let bytes = try handle.read(upToCount: FileProtocol.chunkSize), !bytes.isEmpty {
                try Task.checkCancellation(); hash.update(data: bytes)
                try await wire.sendData(FileProtocol.chunk(index: index, offset: offset, bytes: bytes)); offset += Int64(bytes.count); total += Int64(bytes.count)
                let wait = Double(total) / Double(rate * 1_048_576) - (ProcessInfo.processInfo.systemUptime - start)
                if wait > 0.005 { try await Task.sleep(nanoseconds: UInt64(min(wait, 0.5) * 1_000_000_000)) }
                if Date().timeIntervalSince(notified) > 0.2 { notified = Date(); progress(total) }
            }
            guard offset == source.offer.files[index].size, (try url.resourceValues(forKeys: [.contentModificationDateKey])).contentModificationDate == source.dates[index] else { throw FileProtocol.error("Source file changed during transfer") }
            try await wire.send(FileCommand(kind: "end", index: index, hash: hash.finalize().map { String(format: "%02x", $0) }.joined()))
        }
        try await wire.send(FileCommand(kind: "complete", id: source.offer.id))
        let ack = try FileProtocol.decode(await wire.read()); guard ack.kind == "received", ack.id == source.offer.id else { throw FileProtocol.error("Receiver did not confirm files") }
    }
    private static func receiveFiles(_ offer: FileOffer, wire: FileWire, directory: URL, progress: (Int64) -> Void) async throws -> [URL] {
        let inbox = try FileInbox(offer: offer, directory: directory); var total: Int64 = 0, notified = Date.distantPast
        while true {
            let data = try await wire.read()
            if data.first == 0x36 {
                let (index, offset, bytes) = try FileProtocol.parseChunk(data); try inbox.append(index: index, offset: offset, bytes: bytes); total += Int64(bytes.count)
                if Date().timeIntervalSince(notified) > 0.2 { notified = Date(); progress(total) }
            } else {
                let command = try FileProtocol.decode(data)
                switch command.kind {
                case "begin": try inbox.begin(command.index ?? -1)
                case "end": try inbox.end(command.index ?? -1, hash: command.hash ?? "")
                case "complete":
                    guard command.id == offer.id else { throw FileProtocol.error("File offer mismatch") }
                    let urls = try inbox.complete(); try await wire.send(FileCommand(kind: "received", id: offer.id)); return urls
                default: throw FileProtocol.error("Unexpected file record")
                }
            }
        }
    }
    func copyReceived() {
        guard !receivedURLs.isEmpty else { return }; board.clearContents(); board.writeObjects(receivedURLs as [NSURL]); board.setString(marker, forType: Self.markerType); onClipboardWritten?()
    }
    func showReceived() { if !receivedURLs.isEmpty { NSWorkspace.shared.activateFileViewerSelecting(receivedURLs) } }
    func cancel(notify: Bool = true) {
        if notify, let id = grant?.offer.id {
            if grant?.sender == true { sources[id] = nil; send(FileCommand(kind: "withdraw", id: id)) }
            send(FileCommand(kind: "cancel", id: id))
        }
        generation = UUID(); grant = nil; wire?.close(); wire = nil; job?.cancel(); job = nil; busy = false; progress = 0
        pasteLock.withLock { pasteScheduled = false }; status = "File transfer cancelled"
    }
    private func reset() { blocked = nil; cancel(notify: false); offers = [:]; sources = [:]; incoming = ""; pasteLock.withLock { pasteID = nil; pasteScheduled = false } }
}
