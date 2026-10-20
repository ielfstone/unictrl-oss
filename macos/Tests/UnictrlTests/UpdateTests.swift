import Foundation
import Testing
@testable import Unictrl

@Suite struct UpdateTests {
    private func fixture() throws -> Data {
        let url = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("tests/updates/signed-v1.json")
        return try Data(contentsOf: url)
    }
    @Test func signedManifestAndVersionOrdering() throws {
        let release = try UpdateProtocol.parse(fixture())
        #expect(release.version == "0.4.0")
        #expect(UpdateProtocol.newer("0.4.0", than: "0.3.0"))
        #expect(!UpdateProtocol.newer("0.4.0", than: "0.4.0"))
        #expect(!UpdateProtocol.newer("0.3.0", than: "0.4.0"))
        #expect(!UpdateProtocol.newer("0.4.1-beta", than: "0.4.0"))
        #expect(UpdateProtocol.version("1.2") == nil)
    }
    @Test func tamperedManifestIsRejected() throws {
        var envelope = try JSONDecoder().decode(UpdateProtocol.Envelope.self, from: fixture())
        let bytes = Data(base64Encoded: envelope.payload)!
        let text = String(decoding: bytes, as: UTF8.self).replacingOccurrences(of: "0.4.0", with: "9.9.9")
        envelope = UpdateProtocol.Envelope(payload: Data(text.utf8).base64EncodedString(), signature: envelope.signature)
        #expect(throws: (any Error).self) { try UpdateProtocol.parse(JSONEncoder().encode(envelope)) }
    }
    @Test func corruptedPackageNeverVerifies() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try Data("bad".utf8).write(to: root); defer { try? FileManager.default.removeItem(at: root) }
        #expect(throws: (any Error).self) { try UpdateProtocol.verify(root, asset: UpdateAsset(url: "", sha256: String(repeating: "0", count: 64), size: 3)) }
    }
    @Test func installerPreservesBackupAndRollsBackFailedMove() throws {
        for fail in [false, true] {
            let root = FileManager.default.temporaryDirectory.appendingPathComponent("Unictrl updater test " + UUID().uuidString)
            defer { try? FileManager.default.removeItem(at: root) }
            let target = root.appendingPathComponent("Unictrl.app"), work = root.appendingPathComponent("work")
            let source = work.appendingPathComponent("Unictrl.app")
            try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
            try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
            try Data("old".utf8).write(to: target.appendingPathComponent("version"))
            if !fail { try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true); try Data("new".utf8).write(to: source.appendingPathComponent("version")) }
            let script = root.appendingPathComponent("install.sh")
            try AppUpdater.helper.replacingOccurrences(of: "/usr/bin/open -n", with: "/usr/bin/true").write(to: script, atomically: true, encoding: .utf8)
            let process = Process(); process.executableURL = URL(fileURLWithPath: "/bin/sh")
            process.arguments = [script.path, "2147483647", target.path, source.path, work.path]
            process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice
            try process.run(); process.waitUntilExit()
            #expect(process.terminationStatus == (fail ? 1 : 0))
            #expect(try String(contentsOf: target.appendingPathComponent("version"), encoding: .utf8) == (fail ? "old" : "new"))
            if !fail { #expect(try String(contentsOf: URL(fileURLWithPath: target.path + ".previous/version"), encoding: .utf8) == "old") }
        }
    }
}
