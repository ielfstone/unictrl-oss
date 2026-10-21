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
        let clipboard = MacClipboardSync(server: server, keyFor: server.keyFor, pasteboard: board)
        clipboard.onLocalClipboardChanged = { service.localClipboardChanged() }
        clipboard.onFilesCopied = { service.offer($0, clipboard: true) }
        service.onClipboardWritten = { clipboard.markRemoteFileWrite() }
        service.beforeRemoteClipboard = { clipboard.poll() }
        clipboard.isRemoteFileClipboard = { service.isRemoteFileClipboard }
        clipboard.onRemoteClipboardWritten = { service.remoteClipboardWritten() }
        var clipboardReady = false
        clipboard.onStatus = { if $0.hasPrefix("Ready") { clipboardReady = true } }
        clipboard.configure(enabled: true, selected: peer, paired: [peer])
        server.onConnected = { id, _ in service.connected(id); clipboard.connected(id) }
        server.onDisconnected = { id, _ in service.disconnected(id); clipboard.disconnected(id) }
        server.onMessage = { id, data in
            if data.first == 0x30 || data.first == 0x32 { clipboard.control(data, from: id) }
            else { service.control(data, from: id) }
        }
        server.onClipboardConnection = { clipboard.accept($0) }
        server.onFileConnection = { service.accept($0) }
        try server.start(); defer { clipboard.stop(); service.cancel(); server.stop(); board.releaseGlobally() }
        let port = try #require(server.boundPort)
        let control = ClipboardTestClient(port: port); try await control.open(); defer { control.connection.cancel() }
        let salt = Data(repeating: 4, count: 32)
        try await control.send(Data("UNICTRL2".utf8) + salt + peerBytes)
        let hello = try await control.read(40)
        let controlCipher = SecureChannel(pairingKey: key, clientSalt: salt, serverSalt: hello.suffix(32), isServer: false)
        _ = try await control.readMessage(controlCipher)
        var name = MessageWriter(.hello); name.u8(2); name.utf8("FILE-TEST-PC"); try await control.send(controlCipher.seal(name.data)!)
        #expect(try await control.readMessage(controlCipher) == Data([0x34, 1, 1]))
        #expect(try await control.readMessage(controlCipher) == Data([0x30, 1, 1]))
        #expect(try await control.readMessage(controlCipher) == Data([0x32, 1, 0]))
        try await control.send(controlCipher.seal(Data([0x34, 1, 1]))!)
        try await control.send(controlCipher.seal(Data([0x30, 1, 1]))!)
        try await control.send(controlCipher.seal(Data([0x32, 1, 0]))!)
        let clipboardTicket = try await control.readMessage(controlCipher)
        let clipboardConnection = ClipboardTestClient(port: port); try await clipboardConnection.open(); defer { clipboardConnection.connection.cancel() }
        let clipSalt = Data(repeating: 3, count: 32)
        try await clipboardConnection.send(MacClipboardSync.magic + clipSalt + peerBytes + clipboardTicket.dropFirst())
        let clipReply = try await clipboardConnection.read(40)
        let clipCipher = SecureChannel(pairingKey: key, clientSalt: clipSalt, serverSalt: clipReply.suffix(32), isServer: false, context: ClipboardReplica.context)
        _ = try await clipboardConnection.readMessage(clipCipher)
        try await clipboardConnection.send(clipCipher.seal(ClipboardReplica.message(ClipboardPacket(kind: "ready"))!.data)!)
        for _ in 0..<50 { if clipboardReady { break }; try await Task.sleep(nanoseconds: 10_000_000) }; #expect(clipboardReady)
        for _ in 0..<50 { if service.status.hasPrefix("Files ready") { break }; try await Task.sleep(nanoseconds: 10_000_000) }
        board.clearContents(); board.writeObjects([source as NSURL])
        var first = try FileProtocol.decode(await control.readMessage(controlCipher))
        if first.kind == "clear" { first = try FileProtocol.decode(await control.readMessage(controlCipher)) }
        let offer = try #require(first.offer)
        #expect(offer.mode == "clipboard")
        #expect(!service.busy) // Copy/drop only offers metadata; no file data moves without acceptance.
        try await control.send(controlCipher.seal(FileProtocol.encode(FileCommand(kind: "request", id: offer.id, rate: 100, request: UUID().uuidString)))!)
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

        // Clipboard offers prepare in the background, retain the pending card and expose native URLs.
        let previousBoardCount = board.changeCount
        let incoming = FileOffer(id: UUID().uuidString, mode: "clipboard", files: [FileEntry(name: "received.bin", size: Int64(bytes.count))])
        try await control.send(controlCipher.seal(FileProtocol.encode(FileCommand(kind: "offer", offer: incoming)))!)
        for _ in 0..<50 { if !service.incoming.isEmpty { break }; try await Task.sleep(nanoseconds: 10_000_000) }
        #expect(service.busy); #expect(board.changeCount > previousBoardCount)
        #expect(board.types?.contains(.fileURL) != true) // A pending offer cannot paste the preceding file.
        // No keyboard interception: a native menu consumer must be able to paste the result.
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
        #expect(service.incoming.contains("received.bin")) // Cached is not yet accepted or pasted.
        #expect(board.canReadObject(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]))
        try await Task.sleep(nanoseconds: 20_000_000)
        #expect(service.incoming.contains("received.bin")) // Native type/menu queries must not consume it.
        #expect((board.readObjects(forClasses: [NSURL.self]) as? [URL])?.first == service.receivedURLs.first)
        for _ in 0..<50 { if service.incoming.isEmpty { break }; try await Task.sleep(nanoseconds: 10_000_000) }
        #expect(service.incoming.isEmpty) // Native paste consumption retires the pending card.

        // Repeated new copies, including an unpolled local clipboard change and native metadata churn.
        for cycle in 0..<6 {
            if cycle == 2 { board.clearContents(); board.setString("local unpolled copy", forType: .string) }
            let next = FileOffer(id: UUID().uuidString, mode: "clipboard", files: [FileEntry(name: "cycle-\(cycle).bin", size: Int64(bytes.count))])
            service.control(try FileProtocol.encode(FileCommand(kind: "offer", offer: next)), from: peer)
            #expect(board.types?.contains(.fileURL) != true)
            board.addTypes([NSPasteboard.PasteboardType("com.unictrl.test.metadata")], owner: nil)
            clipboard.poll() // The OS updating our own file marker must not echo/clear the remote intent.
            #expect(service.tryPaste())
            var nextTicket = try FileProtocol.decode(await control.readMessage(controlCipher))
            while nextTicket.kind == "clear" { nextTicket = try FileProtocol.decode(await control.readMessage(controlCipher)) }
            #expect(nextTicket.id == next.id)
            let (connection, cipher) = try await openFiles(nextTicket, port: port, peer: peerBytes, key: key)
            try await connection.send(cipher.seal(FileProtocol.encode(FileCommand(kind: "begin", index: 0)))!)
            for offset in stride(from: 0, to: bytes.count, by: FileProtocol.chunkSize) {
                try await connection.send(cipher.seal(FileProtocol.chunk(index: 0, offset: Int64(offset), bytes: bytes.subdata(in: offset..<min(bytes.count, offset + FileProtocol.chunkSize))))!)
            }
            try await connection.send(cipher.seal(FileProtocol.encode(FileCommand(kind: "end", index: 0, hash: fixture.sha256)))!)
            try await connection.send(cipher.seal(FileProtocol.encode(FileCommand(kind: "complete", id: next.id)))!)
            #expect(try FileProtocol.decode(await connection.readMessage(cipher)).kind == "received")
            for _ in 0..<100 { if !service.busy { break }; try await Task.sleep(nanoseconds: 10_000_000) }
            #expect(service.receivedURLs.first?.lastPathComponent == "cycle-\(cycle).bin")
            #expect(service.incoming.contains("cycle-\(cycle).bin"))
            if cycle == 5 {
                let downloads = root.appendingPathComponent("Downloads")
                service.receive(to: downloads)
                for _ in 0..<100 { if !service.busy { break }; try await Task.sleep(nanoseconds: 10_000_000) }
                #expect(service.status.hasPrefix("Saved to Downloads")); #expect(service.incoming.isEmpty)
                let batch = try #require(FileManager.default.contentsOfDirectory(at: downloads, includingPropertiesForKeys: nil).first)
                #expect(try Data(contentsOf: batch.appendingPathComponent("cycle-5.bin")) == bytes)
            } else {
                #expect((board.readObjects(forClasses: [NSURL.self]) as? [URL])?.first?.lastPathComponent == "cycle-\(cycle).bin")
            }
            connection.connection.cancel()
        }
        service.control(try FileProtocol.encode(FileCommand(kind: "blocked", reason: "too-large")), from: peer)
        #expect(!service.notice.contains("100 MiB")); #expect(service.tryPaste())
        for _ in 0..<50 { if service.notice.contains("100 MiB") { break }; try await Task.sleep(nanoseconds: 10_000_000) }
        #expect(service.notice.contains("100 MiB")); service.dismissNotice()
        #expect(board.types?.contains(.fileURL) != true)
        service.control(try FileProtocol.encode(FileCommand(kind: "clear")), from: peer)
        let oversize = root.appendingPathComponent("too-large.bin")
        FileManager.default.createFile(atPath: oversize.path, contents: nil)
        let largeHandle = try FileHandle(forWritingTo: oversize); try largeHandle.truncate(atOffset: UInt64(FileProtocol.clipboardLimit + 1)); try largeHandle.close()
        service.offer([oversize], clipboard: true)
        let rejected = try FileProtocol.decode(await control.readMessage(controlCipher)); #expect(rejected.kind == "blocked"); #expect(rejected.reason == "too-large")
        #expect(service.notice.isEmpty) // Copying a large local file must stay silent.
        #expect(service.history.count == 8) // One sent, first receive, six repeated receives.

        // A newer remote copy must survive completion of an older, already downloading file.
        let oldCopy = FileOffer(id: UUID().uuidString, mode: "clipboard", files: [FileEntry(name: "older.bin", size: Int64(bytes.count))])
        let latestCopy = FileOffer(id: UUID().uuidString, mode: "clipboard", files: [FileEntry(name: "newest.bin", size: Int64(bytes.count))])
        try await control.send(controlCipher.seal(FileProtocol.encode(FileCommand(kind: "offer", offer: oldCopy)))!)
        for _ in 0..<50 { if service.incoming.contains("older.bin") { break }; try await Task.sleep(nanoseconds: 10_000_000) }
        #expect(service.tryPaste())
        let oldTicket = try FileProtocol.decode(await control.readMessage(controlCipher))
        let (oldConnection, oldCipher) = try await openFiles(oldTicket, port: port, peer: peerBytes, key: key)
        defer { oldConnection.connection.cancel() }
        try await oldConnection.send(oldCipher.seal(FileProtocol.encode(FileCommand(kind: "begin", index: 0)))!)
        try await oldConnection.send(oldCipher.seal(FileProtocol.chunk(index: 0, offset: 0, bytes: bytes.prefix(FileProtocol.chunkSize)))!)
        try await control.send(controlCipher.seal(FileProtocol.encode(FileCommand(kind: "offer", offer: latestCopy)))!)
        for _ in 0..<50 { if service.incoming.contains("newest.bin") { break }; try await Task.sleep(nanoseconds: 10_000_000) }
        let unchanged = board.changeCount
        #expect(service.tryPaste()) // Swallow repeated paste while busy without losing the new offer.
        for offset in stride(from: FileProtocol.chunkSize, to: bytes.count, by: FileProtocol.chunkSize) {
            try await oldConnection.send(oldCipher.seal(FileProtocol.chunk(index: 0, offset: Int64(offset), bytes: bytes.subdata(in: offset..<min(bytes.count, offset + FileProtocol.chunkSize))))!)
        }
        try await oldConnection.send(oldCipher.seal(FileProtocol.encode(FileCommand(kind: "end", index: 0, hash: fixture.sha256)))!)
        try await oldConnection.send(oldCipher.seal(FileProtocol.encode(FileCommand(kind: "complete", id: oldCopy.id)))!)
        #expect(try FileProtocol.decode(await oldConnection.readMessage(oldCipher)).kind == "received")
        for _ in 0..<50 { if !service.busy { break }; try await Task.sleep(nanoseconds: 10_000_000) }
        #expect(board.changeCount == unchanged); #expect(service.incoming.contains("newest.bin"))
        #expect(service.tryPaste())
        let newestTicket = try FileProtocol.decode(await control.readMessage(controlCipher)); #expect(newestTicket.id == latestCopy.id)
        service.cancel()
        #expect(try FileProtocol.decode(await control.readMessage(controlCipher)).kind == "cancel")

        let cancelledOffer = FileOffer(id: UUID().uuidString, mode: "drop", files: [FileEntry(name: "cancelled.bin", size: Int64(bytes.count))])
        try await control.send(controlCipher.seal(FileProtocol.encode(FileCommand(kind: "offer", offer: cancelledOffer)))!)
        for _ in 0..<50 { if service.incoming.contains("cancelled.bin") { break }; try await Task.sleep(nanoseconds: 10_000_000) }
        service.receive(to: root.appendingPathComponent("received"))
        let cancelTicket = try FileProtocol.decode(await control.readMessage(controlCipher))
        let (partial, partialCipher) = try await openFiles(cancelTicket, port: port, peer: peerBytes, key: key)
        defer { partial.connection.cancel() }
        try await partial.send(partialCipher.seal(FileProtocol.encode(FileCommand(kind: "begin", index: 0)))!)
        try await partial.send(partialCipher.seal(FileProtocol.chunk(index: 0, offset: 0, bytes: bytes.prefix(FileProtocol.chunkSize)))!)
        for _ in 0..<50 { if service.progress > 0 { break }; try await Task.sleep(nanoseconds: 10_000_000) }
        #expect(service.progress > 0); service.cancel()
        let receivedDirectory = root.appendingPathComponent("received")
        for _ in 0..<100 {
            if !(try FileManager.default.contentsOfDirectory(atPath: receivedDirectory.path)).contains(where: { $0.hasPrefix(".partial-") }) { break }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        #expect(!(try FileManager.default.contentsOfDirectory(atPath: receivedDirectory.path)).contains(where: { $0.hasPrefix(".partial-") }))
        #expect(try Data(contentsOf: service.receivedURLs[0]) == bytes)
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
