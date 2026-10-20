import AppKit
import Testing
@testable import Unictrl

@Suite struct ClipboardImageTests {
    struct Fixture: Decodable { let png: String; let packets: [ClipboardPacket] }
    static func fixture() throws -> Fixture {
        let path = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("tests/clipboard/image-v2.json")
        return try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: path))
    }
    @Test func sharedImageFixtureAndBidirectionalTransfer() throws {
        let fixture = try Self.fixture(), png = Data(base64Encoded: fixture.png)!
        let mac = ClipboardReplica(authority: true), pc = ClipboardReplica(authority: false)
        mac.imagesAllowed = true; pc.imagesAllowed = true
        #expect(fixture.packets.map { pc.receive($0) }.compactMap(\.png) == [png])
        let packets = pc.copyImage(png)
        #expect(packets.map { mac.receive($0) }.compactMap(\.png) == [png])
        for packet in packets { #expect(ClipboardReplica.message(packet)!.data.count < SecureChannel.maxFrame - 16) }
    }
    @Test func imageCapabilityIsRequiredAndTextStillWorks() throws {
        let fixture = try Self.fixture(), pc = ClipboardReplica(authority: false)
        #expect(fixture.packets.map { pc.receive($0) }.compactMap(\.png).isEmpty)
        #expect(pc.copyImage(Data(base64Encoded: fixture.png)!).isEmpty)
        let mac = ClipboardReplica(authority: true), legacy = ClipboardReplica(authority: false)
        #expect(mac.copy("text").map { legacy.receive($0) }.compactMap(\.text) == ["text"])
    }
    @Test func corruptImageAndNewerTextNeverApply() throws {
        let fixture = try Self.fixture(), pc = ClipboardReplica(authority: false)
        pc.imagesAllowed = true
        var packets = fixture.packets; packets[2].data = Data(repeating: 0, count: 77).base64EncodedString()
        #expect(packets.map { pc.receive($0) }.compactMap(\.png).isEmpty)
        _ = fixture.packets.dropLast().map { pc.receive($0) }; _ = pc.copy("new local text")
        #expect(pc.receive(fixture.packets.last!).png == nil)
    }
    @Test func limitsAndUnknownMime() throws {
        var png = Data(base64Encoded: try Self.fixture().png)!
        png.replaceSubrange(16..<24, with: [0,0,39,16,0,0,39,16]) // 100 MP
        #expect(ClipboardReplica.pngSize(png) == nil)
        #expect(!ClipboardReplica.validSize(0, 4))
        #expect(ClipboardReplica.pngSize(Data(repeating: 0, count: ClipboardReplica.maxImageBytes + 1)) == nil)
        var packets = try Self.fixture().packets; packets[1].mime = "image/svg+xml"
        let pc = ClipboardReplica(authority: false); pc.imagesAllowed = true
        #expect(packets.map { pc.receive($0) }.compactMap(\.png).isEmpty)
    }
    @Test func nativeConversionPreservesAlphaAndRejectsBadPixels() throws {
        let png = Data(base64Encoded: try Self.fixture().png)!
        let converted = try #require(ClipboardImage.convert(png))
        let bitmap = try #require(NSBitmapImageRep(data: converted.png))
        #expect(bitmap.pixelsWide == 2 && bitmap.pixelsHigh == 2)
        #expect(abs(bitmap.colorAt(x: 1, y: 0)!.alphaComponent - 128.0 / 255) < 0.01)
        #expect(bitmap.colorAt(x: 0, y: 1)!.alphaComponent == 0)
        var broken = png; broken.removeSubrange(33..<broken.count - 12)
        #expect(ClipboardImage.convert(broken) == nil)
    }
}
