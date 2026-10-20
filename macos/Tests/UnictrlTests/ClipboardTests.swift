import Foundation
import Testing
@testable import Unictrl

@Suite struct ClipboardTests {
    let id = "11111111-2222-3333-4444-555555555555"
    private func send(_ packets: [ClipboardPacket], to peer: ClipboardReplica) -> [ClipboardEffects] { packets.map { peer.receive($0) } }
    @Test func macToWindowsUnicode() {
        let mac = ClipboardReplica(authority: true), pc = ClipboardReplica(authority: false)
        let text = "中文 🧩\nline 2\r\ntab\t";
        #expect(send(mac.copy(text), to: pc).compactMap(\.text) == [text])
    }
    @Test func windowsToMacAndAcknowledgement() {
        let mac = ClipboardReplica(authority: true), pc = ClipboardReplica(authority: false)
        let packets = pc.copy("Windows → Mac", id: id)
        let effects = send(packets, to: mac)
        #expect(effects.compactMap(\.text) == ["Windows → Mac"])
        _ = send(effects.flatMap(\.packets), to: pc)
        #expect(pc.isCurrent(id))
    }
    @Test func simultaneousCopiesConvergeWithPendingWindowsCopy() {
        let mac = ClipboardReplica(authority: true), pc = ClipboardReplica(authority: false)
        let older = mac.copy("Mac")
        let newer = pc.copy("PC", id: id)
        #expect(send(older, to: pc).compactMap(\.text).isEmpty)
        let effects = send(newer, to: mac)
        _ = send(effects.flatMap(\.packets), to: pc)
        #expect(effects.compactMap(\.text) == ["PC"])
        #expect(pc.latestID == mac.latestID)
    }
    @Test func oldTransferCannotOverwriteNewLocalCopy() {
        let mac = ClipboardReplica(authority: true), pc = ClipboardReplica(authority: false)
        let old = mac.copy("old")
        _ = send(Array(old.dropLast()), to: pc)
        _ = pc.copy("new")
        #expect(pc.receive(old.last!).text == nil)
    }
    @Test func unsupportedLocalCopyInvalidatesQueuedWrite() {
        let pc = ClipboardReplica(authority: false), mac = ClipboardReplica(authority: true)
        let effects = send(mac.copy("old", id: id), to: pc)
        #expect(effects.last?.text == "old")
        pc.invalidateLocal()
        #expect(!pc.isCurrent(id))
    }
    @Test func truncationCorruptionAndReorderingNeverApply() {
        for kind in 0..<3 {
            let mac = ClipboardReplica(authority: true), pc = ClipboardReplica(authority: false)
            var packets = mac.copy("complete only")
            if kind == 0 { packets.remove(at: 2) }
            if kind == 1 { packets[2].data = Data("tampered".utf8).base64EncodedString() }
            if kind == 2 { packets[2].offset = 99 }
            #expect(send(packets, to: pc).compactMap(\.text).isEmpty)
        }
    }
    @Test func largeTextIsChunkedAndWithinFrameLimit() {
        let text = String(repeating: "中", count: 100_000)
        let mac = ClipboardReplica(authority: true), pc = ClipboardReplica(authority: false)
        let packets = mac.copy(text)
        #expect(packets.count > 3)
        for packet in packets { #expect(ClipboardReplica.message(packet)!.data.count < SecureChannel.maxFrame - 16) }
        #expect(send(packets, to: pc).compactMap(\.text) == [text])
    }
    @Test func rejectsLimitsAndNul() {
        let mac = ClipboardReplica(authority: true)
        #expect(mac.copy("").isEmpty)
        #expect(mac.copy("a\0b").isEmpty)
        #expect(mac.copy(String(repeating: "a", count: ClipboardReplica.maxBytes + 1)).isEmpty)
    }
    @Test func appliedTextDoesNotEchoAndDuplicateFinishIgnored() {
        let mac = ClipboardReplica(authority: true), pc = ClipboardReplica(authority: false)
        let packets = mac.copy("once")
        #expect(send(packets, to: pc).flatMap(\.packets).isEmpty)
        #expect(pc.receive(packets.last!).text == nil)
    }
    @Test func clipboardContextCannotDecryptKeyboardFrames() {
        let salt = Data(repeating: 7, count: 32), key = "test-only-key"
        let input = SecureChannel(pairingKey: key, clientSalt: salt, serverSalt: salt, isServer: false)
        let clipboard = SecureChannel(pairingKey: key, clientSalt: salt, serverSalt: salt, isServer: true, context: ClipboardReplica.context)
        var frame = input.seal(Data([1,2,3]))!
        #expect(clipboard.open(from: &frame) == nil)
    }
    @Test func sharedCrossLanguageFixture() throws {
        struct Fixture: Decodable { let text: String; let packets: [ClipboardPacket] }
        let path = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("tests/clipboard/text-v1.json")
        let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: path))
        let pc = ClipboardReplica(authority: false)
        #expect(send(fixture.packets, to: pc).compactMap(\.text) == [fixture.text])
        let mac = ClipboardReplica(authority: true)
        let actual = mac.copy(fixture.text, id: id)
        for (a, b) in zip(actual, fixture.packets) { #expect(try JSONEncoder().encode(a) != Data()); #expect(a.kind == b.kind); #expect(a.hash == b.hash); #expect(a.data == b.data) }
    }
}
