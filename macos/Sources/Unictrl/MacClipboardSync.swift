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
    private var imagesEnabled = false
    private var remoteImages: [String: Bool] = [:]
    private let imageQueue = DispatchQueue(label: "unictrl.clipboard.images", qos: .utility)
    private var preparing: Int?
    private var receiveCount = 0, receivePercent = -1, sendCount = 0, sendPercent = -1
    private var receivingImage = false
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
    func configure(enabled: Bool, images: Bool = false, selected: String?, paired: [String]) {
        let peer = selected.flatMap { paired.contains($0) ? $0 : nil } ?? (paired.count == 1 ? paired.first : nil)
        guard self.enabled != enabled || self.imagesEnabled != images || self.selected != peer else { return }
        self.enabled = enabled; self.imagesEnabled = images; self.selected = peer
        close(); ticket = nil
        for id in server.connectedPeers { announce(id); grant(id) }
        status()
    }
    func connected(_ id: String) { remoteEnabled[id] = nil; remoteImages[id] = nil; announce(id); status() }
    func disconnected(_ id: String) {
        remoteEnabled[id] = nil; remoteImages[id] = nil
        if selected == id { close(); ticket = nil }; status()
    }
    func control(_ data: Data, from peer: String) {
        guard data.count == 3, data[1] == 1, data[2] <= 1 else { return }
        if data[0] == MessageType.clipboardImageCapabilities.rawValue {
            remoteImages[peer] = data[2] == 1
            replica.imagesAllowed = imagesAllowed
            if ready { status() }; return
        }
        remoteEnabled[peer] = data[2] == 1
        if selected == peer { close(); ticket = nil }
        grant(peer); status()
    }
    private func announce(_ id: String) {
        var message = MessageWriter(.clipboardCapabilities)
        message.u8(1); message.u8(enabled && selected == id ? 1 : 0)
        server.send(message, to: id)
        var images = MessageWriter(.clipboardImageCapabilities)
        images.u8(1); images.u8(enabled && imagesEnabled && selected == id ? 1 : 0)
        server.send(images, to: id)
    }
    private func grant(_ id: String) {
        guard enabled, selected == id, remoteEnabled[id] == true, server.isConnected(id) else { return }
        var bytes = Data(count: 16)
        guard bytes.withUnsafeMutableBytes({ SecRandomCopyBytes(kSecRandomDefault, 16, $0.baseAddress!) }) == errSecSuccess else { return }
        ticket = bytes; expires = Date().addingTimeInterval(10)
        var message = MessageWriter(.clipboardTicket); message.bytes(bytes); server.send(message, to: id)
    }
    private var imagesAllowed: Bool { enabled && imagesEnabled && selected.flatMap { remoteImages[$0] } == true }
    private func status() {
        if !enabled { onStatus("Off") }
        else if selected == nil { onStatus("Select one paired PC") }
        else if ready { onStatus(imagesAllowed ? "Ready · text and images" : "Ready · text only") }
        else { onStatus("Waiting for the selected PC to enable text clipboard") }
    }
    func stop() { enabled = false; close(); ticket = nil; timer?.invalidate(); timer = nil }
    private func close() {
        ready = false; seen = pasteboard.changeCount
        candidate?.stateUpdateHandler = nil; candidate?.cancel(); candidate = nil
        wire?.stateUpdateHandler = nil; wire?.cancel(); wire = nil; channel = nil
        output = []; writing = false; preparing = nil; replica = ClipboardReplica(authority: true); replica.imagesAllowed = imagesAllowed
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
                    authenticated = true; replica.imagesAllowed = imagesAllowed; ready = true; seen = pasteboard.changeCount; status(); continue
                }
                if packet.kind == "ack", packet.id == replica.localID { status(); continue }
                poll() // A newer local copy must win over an older in-flight transfer.
                let effect = replica.receive(packet)
                enqueue(effect.packets)
                if packet.kind == "begin" {
                    receivingImage = packet.mime == "image/png" && imagesAllowed
                    receiveCount = packet.count ?? 0; receivePercent = -1
                }
                if packet.kind == "begin" || packet.kind == "chunk" {
                    transferTimer?.invalidate()
                    transferTimer = Timer.scheduledTimer(withTimeInterval: 10, repeats: false) { [weak self] _ in self?.replica.cancelIncoming() }
                }
                if receivingImage, packet.kind == "chunk", receiveCount > 0 {
                    let percent = min(99, (packet.offset ?? 0) * 100 / receiveCount)
                    if percent != receivePercent { receivePercent = percent; onStatus("Receiving image \(percent)%") }
                }
                if let id = effect.id, replica.isCurrent(id), preparing == nil {
                    if let text = effect.text { apply(id, text: text, connection: connection) }
                    else if let png = effect.png {
                        onStatus("Decoding image…")
                        imageQueue.async { [weak self] in
                            let converted = ClipboardImage.convert(png)
                            DispatchQueue.main.async {
                                guard let self, self.wire === connection, self.replica.isCurrent(id), self.imagesAllowed else { return }
                                guard let converted else { self.onStatus("Image is invalid or exceeds limits"); return }
                                self.apply(id, image: converted, connection: connection)
                            }
                        }
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
        preparing = nil; replica.invalidateLocal(); output.removeAll { $0.kind != "order" }
        guard !types.contains(.fileURL), !types.contains(where: {
            ["org.nspasteboard.ConcealedType", "org.nspasteboard.TransientType", "org.nspasteboard.AutoGeneratedType"].contains($0.rawValue)
        }), (pasteboard.pasteboardItems?.count ?? 0) <= 1 else { return }
        if types.contains(.png) || types.contains(.tiff) {
            guard imagesAllowed, let raw = pasteboard.data(forType: types.contains(.png) ? .png : .tiff), raw.count <= 100_000_000, let connection = wire else {
                if imagesAllowed { onStatus("Image unavailable or exceeds limits") }; return
            }
            preparing = count; onStatus("Preparing image…")
            imageQueue.async { [weak self] in
                let converted = ClipboardImage.convert(raw)
                DispatchQueue.main.async {
                    guard let self, self.ready, self.wire === connection, self.imagesAllowed,
                          self.preparing == count, self.pasteboard.changeCount == count else { return }
                    self.preparing = nil
                    guard let converted else { self.onStatus("Image is invalid, unsupported or exceeds 10 MiB / 20 MP"); return }
                    self.enqueue(self.replica.copyImage(converted.png))
                }
            }
            return
        }
        guard let text = pasteboard.string(forType: .string), pasteboard.changeCount == count, !text.isEmpty else { return }
        let packets = replica.copy(text)
        guard !packets.isEmpty else { onStatus("Text exceeds 1 MiB or is unsupported"); return }
        onStatus("Syncing text…"); enqueue(packets)
    }
    private func apply(_ id: String, text: String? = nil, image: (png: Data, tiff: Data)? = nil, connection: NWConnection) {
        poll()
        guard ready, wire === connection, preparing == nil, replica.isCurrent(id) else { return }
        let item = NSPasteboardItem()
        if let text { item.setString(text, forType: .string) }
        else if let image, imagesAllowed { item.setData(image.png, forType: .png); item.setData(image.tiff, forType: .tiff) }
        else { return }
        pasteboard.clearContents()
        if pasteboard.writeObjects([item]) { enqueue([ClipboardPacket(kind: "ack", id: id)]); status() }
        else { onStatus("Clipboard write failed") }
        seen = pasteboard.changeCount
    }
    private func enqueue(_ packets: [ClipboardPacket]) {
        guard ready, let connection = wire else { return }
        guard output.count + packets.count <= 2048 else { fail(connection); return }
        output.append(contentsOf: packets); pump()
    }
    private func pump() {
        guard !writing, !output.isEmpty, let connection = wire, let channel else { return }
        let packet = output.removeFirst()
        guard let message = ClipboardReplica.message(packet), let frame = channel.seal(message.data) else { fail(connection); return }
        if packet.kind == "begin" { sendCount = packet.mime == "image/png" ? packet.count ?? 0 : 0; sendPercent = -1 }
        if sendCount > 0, packet.kind == "chunk" {
            let percent = min(99, (packet.offset ?? 0) * 100 / sendCount)
            if percent != sendPercent { sendPercent = percent; onStatus("Sending image \(percent)%") }
        }
        let imageChunk = sendCount > 0 && packet.kind == "chunk"
        writing = true
        connection.send(content: frame, completion: .contentProcessed { [weak self] error in
            guard let self, self.wire === connection else { return }
            if error != nil { self.fail(connection); return }
            DispatchQueue.main.asyncAfter(deadline: .now() + (imageChunk ? 0.008 : 0)) {
                guard self.wire === connection else { return }
                self.writing = false; self.pump()
            }
        })
    }
    private static func uuid(_ data: Data) -> String? {
        let b = Array(data); guard b.count == 16 else { return nil }
        return UUID(uuid: (b[0],b[1],b[2],b[3],b[4],b[5],b[6],b[7],b[8],b[9],b[10],b[11],b[12],b[13],b[14],b[15])).uuidString
    }
}
