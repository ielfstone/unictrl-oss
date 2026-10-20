import Foundation
import Testing
@testable import Unictrl

struct TransferHistoryTests {
    @Test func metadataSurvivesRestartAndCleanupTouchesOnlyManagedExpiredBatches() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = TransferHistory(root: root), now = Date()
        let old = store.cache.appendingPathComponent("Received-" + UUID().uuidString)
        let fresh = store.cache.appendingPathComponent("Received-" + UUID().uuidString)
        let unrelated = store.cache.appendingPathComponent("my-files")
        for folder in [old, fresh, unrelated] {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try Data([1, 2, 3]).write(to: folder.appendingPathComponent("payload.bin"))
        }
        try FileManager.default.setAttributes([.modificationDate: now.addingTimeInterval(-3601)], ofItemAtPath: old.path)
        let offer = FileOffer(id: UUID().uuidString, mode: "clipboard", files: [FileEntry(name: "record.txt", size: 33)])
        for _ in 0..<105 { store.add(offer, sent: false, result: "Completed") }
        #expect(store.records.count == 100)
        #expect(TransferHistory(root: root).records.count == 100)
        store.cleanCache(now: now)
        #expect(!FileManager.default.fileExists(atPath: old.path))
        #expect(FileManager.default.fileExists(atPath: fresh.path))
        store.cleanCache(all: true)
        #expect(!FileManager.default.fileExists(atPath: fresh.path))
        #expect(FileManager.default.fileExists(atPath: unrelated.path))
        #expect(store.records.count == 100)
        store.clear(); #expect(TransferHistory(root: root).records.isEmpty)
    }
}
