import AppKit
import Network
import Security

/// Main-queue clipboard adapter and authenticated auxiliary connection. Never touches the event tap.
final class MacClipboardSync {
    static let magic = Data("UNICTRC1".utf8)
    private static let reply = Data("CLIPOK01".utf8)
    var onStatus: (String) -> Void = { _ in }
    private let server: PeerServer
    private let keyFor: (String) -> String?
    private let pasteboard: NSPasteboard
    private var enabled = false
    private var selected: String?
    private var remoteEnabled: [String: Bool] = [:]
    private var ticket: Data?
    private var expires = Date.distantPast
    private var wire: NWConnection?
    private var candidate: NWConnection?
    private var channel: SecureChannel?
    private var replica = ClipboardReplica(authority: true)
    private var ready = false
    private var seen: Int
    private var timer: Timer?
    private var transferTimer: Timer?
    private var output: [ClipboardPacket] = []
    private var writing = false

    init(server: PeerServer, keyFor: @escaping (String) -> String?, pasteboard: NSPasteboard = .general) {
        self.server = server; self.keyFor = keyFor; self.pasteboard = pasteboard
        seen = pasteboard.changeCount
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in self?.poll() }
    }
    func configure(enabled: Bool, selected: String?, paired: [String]) {
        let peer = selected.flatMap { paired.contains($0) ? $0 : nil } ?? (paired.count == 1 ? paired.first : nil)
        guard self.enabled != enabled || self.selected != peer else { return }
        self.enabled = enabled; self.selected = peer
        close(); ticket = nil
        for id in server.connectedPeers { announce(id); grant(id) }
        status()
    }
    func connected(_ id: String) { remoteEnabled[id] = nil; announce(id); status() }
    func disconnected(_ id: String) {
        remoteEnabled[id] = nil
        if selected == id { close(); ticket = nil }; status()
    }
    func control(_ data: Data, from peer: String) {
        guard data.count == 3, data[1] == 1, data[2] <= 1 else { return }
        remoteEnabled[peer] = data[2] == 1
        if selected == peer { close(); ticket = nil }
        grant(peer); status()
    }
    private func announce(_ id: String) {
        var message = MessageWriter(.clipboardCapabilities)
        message.u8(1); message.u8(enabled && selected == id ? 1 : 0)
        server.send(message, to: id)
    }
    private func grant(_ id: String) {
        guard enabled, selected == id, remoteEnabled[id] == true, server.isConnected(id) else { return }
        var bytes = Data(count: 16)
        guard bytes.withUnsafeMutableBytes({ SecRandomCopyBytes(kSecRandomDefault, 16, $0.baseAddress!) }) == errSecSuccess else { return }
        ticket = bytes; expires = Date().addingTimeInterval(10)
        var message = MessageWriter(.clipboardTicket); message.bytes(bytes); server.send(message, to: id)
    }
    private func status() {
        if !enabled { onStatus("Off") }
        else if selected == nil { onStatus("Select one paired PC") }
        else if ready { onStatus("Ready · text only") }
        else { onStatus("Waiting for the selected PC to enable text clipboard") }
    }
    func stop() { enabled = false; close(); ticket = nil; timer?.invalidate(); timer = nil }
    private func close() {
        ready = false; seen = pasteboard.changeCount
        candidate?.stateUpdateHandler = nil; candidate?.cancel(); candidate = nil
        wire?.stateUpdateHandler = nil; wire?.cancel(); wire = nil; channel = nil
        output = []; writing = false; replica = ClipboardReplica(authority: true)
        transferTimer?.invalidate(); transferTimer = nil
    }
    func accept(_ connection: NWConnection) {
        connection.stateUpdateHandler = nil
        guard enabled, candidate == nil, wire == nil, let expected = ticket, Date() < expires else { connection.cancel(); return }
        candidate = connection
        let timeout = DispatchWorkItem { [weak self, weak connection] in
            guard let self, let connection, self.candidate === connection else { return }
            self.candidate = nil; connection.cancel()
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 5, execute: timeout)
        connection.receive(minimumIncompleteLength: 64, maximumLength: 64) { [weak self] data, _, _, error in
            guard let self, candidate === connection, let data, data.count == 64, error == nil,
                  enabled, let peer = selected, server.isConnected(peer), remoteEnabled[peer] == true,
                  ticket == expected, Date() < expires, data.suffix(16) == expected,
                  Self.uuid(data.subdata(in: 32..<48)) == peer, let key = keyFor(peer) else {
                timeout.cancel(); if self?.candidate === connection { self?.candidate = nil }; connection.cancel(); return
            }
            var salt = Data(count: 32)
            guard salt.withUnsafeMutableBytes({ SecRandomCopyBytes(kSecRandomDefault, 32, $0.baseAddress!) }) == errSecSuccess else { connection.cancel(); return }
            let secure = SecureChannel(pairingKey: key, clientSalt: Data(data.prefix(32)), serverSalt: salt,
                                       isServer: true, context: ClipboardReplica.context)
            connection.send(content: Self.reply + salt, completion: .contentProcessed { _ in })
            if let hello = ClipboardReplica.message(ClipboardPacket(kind: "ready")), let frame = secure.seal(hello.data) {
                connection.send(content: frame, completion: .contentProcessed { _ in })
            }
            read(connection, secure: secure, buffer: Data(), authenticated: false, timeout: timeout)
        }
    }
    private func read(_ connection: NWConnection, secure: SecureChannel, buffer: Data, authenticated: Bool, timeout: DispatchWorkItem) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16_384) { [weak self] data, _, complete, error in
            guard let self, self.wire === connection || self.candidate === connection else { return }
            var buffer = buffer
            if let data { buffer.append(data) }
            guard let messages = secure.open(from: &buffer) else { fail(connection); return }
            var authenticated = authenticated
            for message in messages {
                guard let packet = ClipboardReplica.parse(message) else { fail(connection); return }
                if !authenticated {
                    guard packet.kind == "ready", self.candidate === connection else { fail(connection); return }
                    timeout.cancel(); ticket = nil; candidate = nil; wire = connection; channel = secure
                    authenticated = true; ready = true; seen = pasteboard.changeCount; status(); continue
                }
                poll() // A newer local copy must win over an older in-flight transfer.
                let effect = replica.receive(packet)
                enqueue(effect.packets)
                if packet.kind == "begin" {
                    transferTimer?.invalidate()
                    transferTimer = Timer.scheduledTimer(withTimeInterval: 10, repeats: false) { [weak self] _ in self?.replica.cancelIncoming() }
                }
                if let text = effect.text, let id = effect.id, replica.isCurrent(id) {
                    poll()
                    if replica.isCurrent(id) {
                        pasteboard.clearContents()
                        if !pasteboard.setString(text, forType: .string) { onStatus("Clipboard write failed") }
                        else { status() }
                        seen = pasteboard.changeCount
                    }
                }
            }
            if complete || error != nil { fail(connection); return }
            read(connection, secure: secure, buffer: buffer, authenticated: authenticated, timeout: timeout)
        }
    }
    private func fail(_ connection: NWConnection) {
        if candidate === connection { candidate = nil; connection.cancel(); return }
        if wire === connection { close(); status() }
    }
    private func poll() {
        let count = pasteboard.changeCount
        guard ready, enabled else { seen = count; return }
        guard count != seen else { return }
        seen = count; replica.cancelIncoming()
        let types = pasteboard.types ?? []
        guard !types.contains(.fileURL), !types.contains(where: {
            ["org.nspasteboard.ConcealedType", "org.nspasteboard.TransientType", "org.nspasteboard.AutoGeneratedType"].contains($0.rawValue)
        }), let text = pasteboard.string(forType: .string), pasteboard.changeCount == count, !text.isEmpty else {
            replica.invalidateLocal(); output.removeAll { $0.kind != "order" }; return
        }
        let packets = replica.copy(text)
        guard !packets.isEmpty else { replica.invalidateLocal(); output.removeAll { $0.kind != "order" }; onStatus("Text exceeds 1 MiB or is unsupported"); return }
        output.removeAll { $0.kind != "order" }
        enqueue(packets)
    }
    private func enqueue(_ packets: [ClipboardPacket]) {
        guard ready, let connection = wire else { return }
        guard output.count + packets.count <= 512 else { fail(connection); return }
        output.append(contentsOf: packets); pump()
    }
    private func pump() {
        guard !writing, !output.isEmpty, let connection = wire, let channel else { return }
        let packet = output.removeFirst()
        guard let message = ClipboardReplica.message(packet), let frame = channel.seal(message.data) else { fail(connection); return }
        writing = true
        connection.send(content: frame, completion: .contentProcessed { [weak self] error in
            guard let self, self.wire === connection else { return }
            self.writing = false
            if error != nil { self.fail(connection) } else { self.pump() }
        })
    }
    private static func uuid(_ data: Data) -> String? {
        let b = Array(data); guard b.count == 16 else { return nil }
        return UUID(uuid: (b[0],b[1],b[2],b[3],b[4],b[5],b[6],b[7],b[8],b[9],b[10],b[11],b[12],b[13],b[14],b[15])).uuidString
    }
}
