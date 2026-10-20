import Foundation
import CryptoKit
import Testing
@testable import Unictrl

@Suite struct FileTests {
    struct Fixture: Decodable { let offer: FileOffer; let data: String; let sha256: String; let firstChunk: String }
    static func fixture() throws -> Fixture {
        let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        return try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: root.appendingPathComponent("tests/files/v1.json")))
    }
    @Test func clipboardAndDropLimitsAreDifferent() throws {
        let id = UUID().uuidString
        try FileProtocol.validate(FileOffer(id: id, mode: "clipboard", files: [FileEntry(name: "a", size: FileProtocol.clipboardLimit)]))
        #expect(throws: (any Error).self) { try FileProtocol.validate(FileOffer(id: id, mode: "clipboard", files: [FileEntry(name: "a", size: FileProtocol.clipboardLimit + 1)])) }
        try FileProtocol.validate(FileOffer(id: id, mode: "drop", files: (0..<4).map { FileEntry(name: "file\($0)", size: FileProtocol.fileLimit) }))
        #expect(throws: (any Error).self) { try FileProtocol.validate(FileOffer(id: id, mode: "drop", files: [FileEntry(name: "a", size: FileProtocol.fileLimit + 1)])) }
        #expect(throws: (any Error).self) { try FileProtocol.validate(FileOffer(id: id, mode: "drop", files: (0..<5).map { FileEntry(name: "file\($0)", size: FileProtocol.fileLimit) })) }
    }
    @Test func unsafeNamesAndDuplicatesAreRejected() throws {
        for name in ["../escape", "a/b", "a\\b", "x:y", "CON.txt", "a.", "a ", "", "..", "LPT9", "a\0b"] { #expect(!FileProtocol.validName(name)) }
        #expect(FileProtocol.validName("测试文件.bin"))
        #expect(throws: (any Error).self) { try FileProtocol.validate(FileOffer(id: UUID().uuidString, mode: "drop", files: [FileEntry(name: "A", size: 0), FileEntry(name: "a", size: 0)])) }
    }
    @Test func fixtureStreamsToDiskAndPublishesAtomically() throws {
        let fixture = try Self.fixture(), data = Data(base64Encoded: fixture.data)!
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString); defer { try? FileManager.default.removeItem(at: root) }
        let inbox = try FileInbox(offer: fixture.offer, directory: root)
        try inbox.begin(0)
        #expect(FileProtocol.chunk(index: 0, offset: 0, bytes: data.prefix(FileProtocol.chunkSize)) == Data(base64Encoded: fixture.firstChunk))
        for offset in stride(from: 0, to: data.count, by: FileProtocol.chunkSize) {
            let bytes = data.subdata(in: offset..<min(data.count, offset + FileProtocol.chunkSize))
            try inbox.append(index: 0, offset: Int64(offset), bytes: bytes)
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: root.path).allSatisfy { $0.hasPrefix(".partial-") })
        try inbox.end(0, hash: fixture.sha256); let urls = try inbox.complete()
        #expect(try Data(contentsOf: urls[0]) == data)
        #expect(try FileManager.default.contentsOfDirectory(atPath: root.path).count == 1)
    }
    @Test func corruptionCancellationAndReorderingNeverPublish() throws {
        let fixture = try Self.fixture()
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString); defer { try? FileManager.default.removeItem(at: root) }
        do {
            let inbox = try FileInbox(offer: fixture.offer, directory: root); try inbox.begin(0)
            #expect(throws: (any Error).self) { try inbox.append(index: 0, offset: 1, bytes: Data([1])) }
            #expect(throws: (any Error).self) { try inbox.end(0, hash: fixture.sha256) }
            #expect(throws: (any Error).self) { _ = try inbox.complete() }
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
    }
}
