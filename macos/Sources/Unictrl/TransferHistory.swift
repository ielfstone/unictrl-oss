import Foundation

struct TransferRecord: Codable, Identifiable {
    let id: UUID
    let date: Date
    let names: [String]
    let bytes: Int64
    let direction: String
    let result: String
    var summary: String { "\(direction) · \(names.joined(separator: ", ")) · \(ByteCountFormatter.string(fromByteCount: bytes, countStyle: .binary)) · \(result)" }
}

/// Only metadata is retained. Clipboard payloads live in a separately managed, expiring cache.
final class TransferHistory {
    let cache: URL
    private let file: URL
    private(set) var records: [TransferRecord]
    init(root: URL? = nil) {
        let support = root ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Unictrl")
        file = support.appendingPathComponent("transfer-history.json")
        cache = (root ?? FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appendingPathComponent("com.enlivatech.unictrl")).appendingPathComponent("ClipboardFiles")
        records = (try? JSONDecoder().decode([TransferRecord].self, from: Data(contentsOf: file))) ?? []
        cleanCache()
    }
    func add(_ offer: FileOffer, sent: Bool, result: String) {
        records.insert(TransferRecord(id: UUID(), date: Date(), names: offer.files.map(\.name), bytes: offer.size, direction: sent ? "Sent" : "Received", result: result), at: 0)
        records = Array(records.prefix(100)); save()
    }
    func clear() { records = []; save() }
    private func save() {
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        if let data = try? JSONEncoder().encode(records) { try? data.write(to: file, options: .atomic) }
    }
    func cleanCache(all: Bool = false, now: Date = Date()) {
        for url in (try? FileManager.default.contentsOfDirectory(at: cache, includingPropertiesForKeys: [.contentModificationDateKey, .isSymbolicLinkKey])) ?? [] {
            let name = url.lastPathComponent
            let suffix = name.hasPrefix("Received-") ? String(name.dropFirst(9)) : name.hasPrefix(".partial-") ? String(name.dropFirst(9)) : ""
            guard UUID(uuidString: suffix) != nil, let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .isSymbolicLinkKey]), values.isSymbolicLink != true else { continue }
            if all || now.timeIntervalSince(values.contentModificationDate ?? .distantFuture) >= 3600 { try? FileManager.default.removeItem(at: url) }
        }
    }
}
