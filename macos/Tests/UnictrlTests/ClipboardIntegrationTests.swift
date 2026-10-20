import AppKit
import Foundation
import Network
import Testing
@testable import Unictrl

final class ClipboardTestClient {
    let connection: NWConnection
    init(port: UInt16) { connection = NWConnection(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!, using: .tcp) }
    func open() async throws {
        FileHandle.standardError.write(Data("clipboard test: opening \(connection.endpoint)\n".utf8))
        try await withCheckedThrowingContinuation { (done: CheckedContinuation<Void, Error>) in
            connection.stateUpdateHandler = { [weak self] state in
                switch state {
                case .ready: self?.connection.stateUpdateHandler = nil; done.resume()
                case .failed(let error): self?.connection.stateUpdateHandler = nil; done.resume(throwing: error)
                default: break
                }
            }
            connection.start(queue: .main)
        }
    }
    func send(_ data: Data) async throws {
        try await withCheckedThrowingContinuation { (done: CheckedContinuation<Void, Error>) in
            connection.send(content: data, completion: .contentProcessed { error in
                if let error { done.resume(throwing: error) } else { done.resume() }
            })
        }
    }
    func read(_ count: Int) async throws -> Data {
        FileHandle.standardError.write(Data("clipboard test: read \(count)\n".utf8))
        let timeout = DispatchWorkItem { [connection] in connection.cancel() }
        DispatchQueue.main.asyncAfter(deadline: .now() + 5, execute: timeout)
        return try await withCheckedThrowingContinuation { done in
            connection.receive(minimumIncompleteLength: count, maximumLength: count) { data, _, _, error in
                timeout.cancel()
                if let data, data.count == count { done.resume(returning: data) }
                else { done.resume(throwing: error ?? NWError.posix(.ECONNRESET)) }
            }
        }
    }
    func readMessage(_ channel: SecureChannel) async throws -> Data {
        let prefix = try await read(2)
        let size = Int(prefix[0]) << 8 | Int(prefix[1])
        guard size <= SecureChannel.maxFrame else { throw NWError.posix(.EMSGSIZE) }
        var buffer = prefix + (try await read(size))
        guard let message = channel.open(from: &buffer)?.first else { throw NWError.posix(.EPROTO) }
        return message
    }
}

@Suite(.serialized) struct ClipboardIntegrationTests {
    @MainActor @Test func nativeMacTextAndAuthenticatedAuxiliarySocket() async throws { try await exercise(images: false) }
    @MainActor @Test func nativeMacImagesAndAuthenticatedAuxiliarySocket() async throws { try await exercise(images: true) }
    @MainActor private func exercise(images: Bool) async throws {
        let peer = UUID().uuidString
        let uuid = UUID(uuidString: peer)!
        let peerBytes = withUnsafeBytes(of: uuid.uuid) { Data($0) }
        let key = "clipboard-integration-test-only"
        let board = NSPasteboard.withUniqueName()
        board.setString("existing clipboard must not auto-sync", forType: .string)
        var settings = UnictrlSettings(); settings.port = 0
        let server = PeerServer(settings: settings)
        server.keyFor = { $0 == peer ? key : nil }
        let service = MacClipboardSync(server: server, keyFor: server.keyFor, pasteboard: board)
        server.onConnected = { id, _ in service.connected(id) }
        server.onDisconnected = { id, _ in service.disconnected(id) }
        server.onMessage = { id, data in if data.first == MessageType.clipboardCapabilities.rawValue || data.first == MessageType.clipboardImageCapabilities.rawValue { service.control(data, from: id) } }
        server.onClipboardConnection = { service.accept($0) }
        service.configure(enabled: true, images: images, selected: peer, paired: [peer])
        try server.start()
        defer { service.stop(); server.stop(); board.releaseGlobally() }
        let port = try #require(server.boundPort)
        let control = ClipboardTestClient(port: port)
        try await control.open(); defer { control.connection.cancel() }
        let salt = Data(repeating: 3, count: 32)
        try await control.send(Data("UNICTRL2".utf8) + salt + peerBytes)
        let hello = try await control.read(40)
        let controlCipher = SecureChannel(pairingKey: key, clientSalt: salt, serverSalt: hello.suffix(32), isServer: false)
        #expect(try await control.readMessage(controlCipher).first == MessageType.hello.rawValue)
        var name = MessageWriter(.hello); name.u8(2); name.utf8("TEST-PC")
        try await control.send(controlCipher.seal(name.data)!)
        let caps = try await control.readMessage(controlCipher)
        #expect(caps == Data([MessageType.clipboardCapabilities.rawValue, 1, 1]))
        #expect(try await control.readMessage(controlCipher) == Data([MessageType.clipboardImageCapabilities.rawValue, 1, images ? 1 : 0]))
        var response = MessageWriter(.clipboardCapabilities); response.u8(1); response.u8(1)
        try await control.send(controlCipher.seal(response.data)!)
        var imageCaps = MessageWriter(.clipboardImageCapabilities); imageCaps.u8(1); imageCaps.u8(images ? 1 : 0)
        try await control.send(controlCipher.seal(imageCaps.data)!)
        let ticket = try await control.readMessage(controlCipher)
        #expect(ticket.count == 17)
        let data = ClipboardTestClient(port: port)
        try await data.open(); defer { data.connection.cancel() }
        let dataSalt = Data(repeating: 9, count: 32)
        try await data.send(MacClipboardSync.magic + dataSalt + peerBytes + ticket.dropFirst())
        let reply = try await data.read(40)
        #expect(reply.prefix(8) == Data("CLIPOK01".utf8))
        let cipher = SecureChannel(pairingKey: key, clientSalt: dataSalt, serverSalt: reply.suffix(32), isServer: false, context: ClipboardReplica.context)
        #expect(ClipboardReplica.parse(try await data.readMessage(cipher))?.kind == "ready")
        try await data.send(cipher.seal(ClipboardReplica.message(ClipboardPacket(kind: "ready"))!.data)!)
        let pc = ClipboardReplica(authority: false); pc.imagesAllowed = images
        let text = "Windows → Mac：中文 🧩\nline 2"
        for packet in pc.copy(text) { try await data.send(cipher.seal(ClipboardReplica.message(packet)!.data)!) }
        let order = try #require(ClipboardReplica.parse(try await data.readMessage(cipher)))
        _ = pc.receive(order)
        for _ in 0..<100 {
            if board.string(forType: .string) == text { break }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        #expect(board.string(forType: .string) == text)
        let macText = "Mac → Windows：多行\r\nEmoji 🧩"
        board.clearContents(); board.setString(macText, forType: .string)
        var received: String?
        for _ in 0..<8 {
            let packet = try #require(ClipboardReplica.parse(try await data.readMessage(cipher)))
            if let text = pc.receive(packet).text { received = text; break }
        }
        #expect(received == macText)
        if images {
            let png = Data(base64Encoded: try ClipboardImageTests.fixture().png)!
            let offered = pc.copyImage(png)
            for packet in offered { try await data.send(cipher.seal(ClipboardReplica.message(packet)!.data)!) }
            for _ in 0..<8 {
                let packet = try #require(ClipboardReplica.parse(try await data.readMessage(cipher)))
                _ = pc.receive(packet)
                if packet.kind == "ack" && packet.id == offered.last?.id { break }
            }
            #expect(board.types?.contains(.png) == true)
            #expect(NSBitmapImageRep(data: board.data(forType: .png)!)?.pixelsWide == 2)
            // Real Mac copy operations can advertise both a file URL and actual raster bytes.
            // The URL must not hide the image or be opened by the sync service.
            let item = NSPasteboardItem(); item.setData(png, forType: .png)
            item.setString("file:///nonexistent/unictrl-image-regression.png", forType: .fileURL)
            board.clearContents(); board.writeObjects([item])
            var receivedImage: Data?
            for _ in 0..<8 {
                let packet = try #require(ClipboardReplica.parse(try await data.readMessage(cipher)))
                if let image = pc.receive(packet).png { receivedImage = image; break }
            }
            #expect(receivedImage != nil)
            #expect(NSBitmapImageRep(data: receivedImage!)?.pixelsHigh == 2)

            // Screenshot-sized, incompressible pixels exercise many encrypted chunks and TIFF-only sources.
            let bitmap = try #require(NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 257, pixelsHigh: 129,
                bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                colorSpaceName: .deviceRGB, bytesPerRow: 257 * 4, bitsPerPixel: 32))
            let pixels = try #require(bitmap.bitmapData)
            var random: UInt32 = 0x12345678
            for index in 0..<(257 * 129 * 4) {
                random ^= random << 13; random ^= random >> 17; random ^= random << 5
                pixels[index] = index % 4 == 3 ? 255 : UInt8(truncatingIfNeeded: random)
            }
            for type: NSPasteboard.PasteboardType in [.png, .tiff] {
                let previousText = "text before \(type.rawValue)"
                board.clearContents(); board.setString(previousText, forType: .string)
                var gotText = false
                for _ in 0..<8 {
                    let packet = try #require(ClipboardReplica.parse(try await data.readMessage(cipher)))
                    if pc.receive(packet).text == previousText { gotText = true; break }
                }
                #expect(gotText)
                let raster = try #require(bitmap.representation(using: type == .png ? .png : .tiff, properties: [:]))
                #expect(raster.count > 8_192)
                let screenshot = NSPasteboardItem(); screenshot.setData(raster, forType: type)
                screenshot.setString("file:///nonexistent/screenshot.png", forType: .fileURL)
                board.clearContents(); board.writeObjects([screenshot])
                var imageEffect: ClipboardEffects?
                for _ in 0..<2048 {
                    let packet = try #require(ClipboardReplica.parse(try await data.readMessage(cipher)))
                    let effect = pc.receive(packet)
                    if effect.png != nil { imageEffect = effect; break }
                }
                let receivedRaster = try #require(imageEffect?.png)
                let decoded = try #require(NSBitmapImageRep(data: receivedRaster))
                #expect(decoded.pixelsWide == 257 && decoded.pixelsHigh == 129)
                #expect(decoded.bitmapData?[0] == pixels[0])
                let ack = ClipboardPacket(kind: "ack", id: imageEffect?.id)
                try await data.send(cipher.seal(ClipboardReplica.message(ack)!.data)!)
            }
        }
        service.configure(enabled: false, selected: peer, paired: [peer])
        let disabled = try await control.readMessage(controlCipher)
        #expect(disabled == Data([MessageType.clipboardCapabilities.rawValue, 1, 0]))
        _ = try await control.readMessage(controlCipher) // images disabled capability
        var ping = MessageWriter(.ping); ping.u64(77)
        server.send(ping, to: peer)
        #expect(try await control.readMessage(controlCipher) == ping.data) // disabling clipboard keeps input connection alive
    }
}
