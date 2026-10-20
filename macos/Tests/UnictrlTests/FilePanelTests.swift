import AppKit
import SwiftUI
import Testing
@testable import Unictrl

struct FilePanelTests {
    @MainActor @Test func renderFileCardWithIncomingOffer() throws {
        let board = NSPasteboard.withUniqueName(); defer { board.releaseGlobally() }
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let peer = UUID().uuidString
        let service = MacFileTransfer(server: PeerServer(settings: UnictrlSettings()), keyFor: { _ in nil }, pasteboard: board, receiveDirectory: root)
        service.configure(enabled: true, selected: peer, rate: 10)
        service.control(Data([0x34, 1, 1]), from: peer)
        let offer = FileOffer(id: UUID().uuidString, mode: "drop", files: [FileEntry(name: "presentation.zip", size: 640 * 1_048_576)])
        service.control(try FileProtocol.encode(FileCommand(kind: "offer", offer: offer)), from: peer)
        let view = FileTransferCard(enabled: true, service: service, enable: {}).padding(12).frame(width: 340).background(Color(nsColor: .windowBackgroundColor))
        let renderer = ImageRenderer(content: view); renderer.scale = 2
        let image = try #require(renderer.nsImage)
        #expect(image.size.width == 340); #expect(image.size.height > 250)
        if let folder = ProcessInfo.processInfo.environment["UNICTRL_PANEL_ARTIFACT"] {
            let url = URL(fileURLWithPath: folder).appendingPathComponent("mac-file-panel.png")
            let bitmap = try #require(image.tiffRepresentation.flatMap(NSBitmapImageRep.init(data:)))
            try bitmap.representation(using: .png, properties: [:])!.write(to: url)
        }
    }
}
