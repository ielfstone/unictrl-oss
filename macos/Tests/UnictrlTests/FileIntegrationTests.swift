import AppKit
import Foundation
import Network
import Testing
@testable import Unictrl

@Suite(.serialized) struct FileIntegrationTests {
    @MainActor @Test func nativeMacFilesRequireAcceptanceAndTransferBothDirections() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("Unictrl files " + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let fixture = try FileTests.fixture(), bytes = Data(base64Encoded: fixture.data)!
        let source = root.appendingPathComponent("source.bin"); try bytes.write(to: source)
        let peer = UUID().uuidString, key = "files-integration-test-only"
        let uuid = UUID(uuidString: peer)!, peerBytes = withUnsafeBytes(of: UUID(uuidString: peer)!.uuid) { Data($0) }
        _ = uuid
        let board = NSPasteboard.withUniqueName(); board.setString("baseline text", forType: .string)
        var settings = UnictrlSettings(); settings.port = 0
        let server = PeerServer(settings: settings); server.keyFor = { $0 == peer ? key : nil }
        let service = MacFileTransfer(server: server, keyFor: server.keyFor, pasteboard: board, receiveDirectory: root.appendingPathComponent("received"))
        service.canReplayPaste = { false }
        service.configure(enabled: true, selected: peer, rate: 100)
        server.onConnected = { id, _ in service.connected(id) }; server.onDisconnected = { id, _ in service.disconnected(id) }
        server.onMessage = { id, data in service.control(data, from: id) }
        server.onFileConnection = { service.accept($0) }
        try server.start(); defer { service.cancel(); server.stop(); board.releaseGlobally() }
        let port = try #require(server.boundPort)
        let control = ClipboardTestClient(port: port); try await control.open(); defer { control.connection.cancel() }
        let salt = Data(repeating: 4, count: 32)
        try await control.send(Data("UNICTRL2".utf8) + salt + peerBytes)
        let hello = try await control.read(40)
        let controlCipher = SecureChannel(pairingKey: key, clientSalt: salt, serverSalt: hello.suffix(32), isServer: false)
        _ = try await control.readMessage(controlCipher)
        var name = MessageWriter(.hello); name.u8(2); name.utf8("FILE-TEST-PC"); try await control.send(controlCipher.seal(name.data)!)
        #expect(try await control.readMessage(controlCipher) == Data([0x34, 1, 1]))
        try await control.send(controlCipher.seal(Data([0x34, 1, 1]))!)
        for _ in 0..<50 { if service.status.hasPrefix("Files ready") { break }; try await Task.sleep(nanoseconds: 10_000_000) }
        service.offer([source])
        let offer = try #require(try FileProtocol.decode(await control.readMessage(controlCipher)).offer)
        #expect(!service.busy) // Copy/drop only offers metadata; no file data moves without acceptance.
        try await control.send(controlCipher.seal(FileProtocol.encode(FileCommand(kind: "request", id: offer.id, rate: 100)))!)
        let ticket = try FileProtocol.decode(await control.readMessage(controlCipher))
        let (outbound, outCipher) = try await openFiles(ticket, port: port, peer: peerBytes, key: key)
        defer { outbound.connection.cancel() }
        let sink = try FileInbox(offer: offer, directory: root.appendingPathComponent("mock-pc"))
        while true {
            let data = try await outbound.readMessage(outCipher)
            if data.first == 0x36 { let (index, offset, chunk) = try FileProtocol.parseChunk(data); try sink.append(index: index, offset: offset, bytes: chunk); continue }
            let command = try FileProtocol.decode(data)
            if command.kind == "begin" { try sink.begin(command.index!) }
            else if command.kind == "end" { try sink.end(command.index!, hash: command.hash!) }
            else if command.kind == "complete" { break }
        }
        #expect(try Data(contentsOf: sink.complete()[0]) == bytes)
        try await outbound.send(outCipher.seal(FileProtocol.encode(FileCommand(kind: "received", id: offer.id)))!)
        for _ in 0..<50 { if !service.busy { break }; try await Task.sleep(nanoseconds: 10_000_000) }
        #expect(!service.busy)

        // Incoming clipboard offer is fetched on paste, then replaces text with real local file URLs.
        let incoming = FileOffer(id: UUID().uuidString, mode: "clipboard", files: [FileEntry(name: "received.bin", size: Int64(bytes.count))])
        try await control.send(controlCipher.seal(FileProtocol.encode(FileCommand(kind: "offer", offer: incoming)))!)
        for _ in 0..<50 { if !service.incoming.isEmpty { break }; try await Task.sleep(nanoseconds: 10_000_000) }
        #expect(!service.busy); #expect(board.string(forType: .string) == "baseline text")
        #expect(service.tryPaste())
        let receiveTicket = try FileProtocol.decode(await control.readMessage(controlCipher))
        let (inbound, inCipher) = try await openFiles(receiveTicket, port: port, peer: peerBytes, key: key)
        defer { inbound.connection.cancel() }
        try await inbound.send(inCipher.seal(FileProtocol.encode(FileCommand(kind: "begin", index: 0)))!)
        for offset in stride(from: 0, to: bytes.count, by: FileProtocol.chunkSize) {
            let chunk = bytes.subdata(in: offset..<min(bytes.count, offset + FileProtocol.chunkSize))
            try await inbound.send(inCipher.seal(FileProtocol.chunk(index: 0, offset: Int64(offset), bytes: chunk))!)
        }
        try await inbound.send(inCipher.seal(FileProtocol.encode(FileCommand(kind: "end", index: 0, hash: fixture.sha256)))!)
        try await inbound.send(inCipher.seal(FileProtocol.encode(FileCommand(kind: "complete", id: incoming.id)))!)
        #expect(try FileProtocol.decode(await inbound.readMessage(inCipher)).kind == "received")
        for _ in 0..<50 { if !service.busy { break }; try await Task.sleep(nanoseconds: 10_000_000) }
        #expect(try Data(contentsOf: service.receivedURLs[0]) == bytes)
        #expect(board.types?.contains(.fileURL) == true)
        #expect(board.string(forType: .string) == nil)
    }
    private func openFiles(_ ticket: FileCommand, port: UInt16, peer: Data, key: String) async throws -> (ClipboardTestClient, SecureChannel) {
        let client = ClipboardTestClient(port: port); try await client.open()
        let salt = Data(repeating: 8, count: 32)
        try await client.send(FileProtocol.magic + salt + peer + Data(base64Encoded: ticket.ticket!)!)
        let hello = try await client.read(40); #expect(hello.prefix(8) == FileProtocol.reply)
        let cipher = SecureChannel(pairingKey: key, clientSalt: salt, serverSalt: hello.suffix(32), isServer: false, context: FileProtocol.context)
        let ready = try FileProtocol.decode(await client.readMessage(cipher)); #expect(ready.kind == "ready")
        try await client.send(cipher.seal(FileProtocol.encode(FileCommand(kind: "ready", id: ticket.id)))!)
        return (client, cipher)
    }
}
