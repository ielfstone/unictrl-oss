import Foundation
import CryptoKit

struct FileEntry: Codable { let name: String; let size: Int64 }
struct FileOffer: Codable {
    let id: String
    let mode: String
    let files: [FileEntry]
    var size: Int64 { files.reduce(0) { $0 + $1.size } }
    var summary: String { "\(files.count) file(s) · \(ByteCountFormatter.string(fromByteCount: size, countStyle: .binary))" }
}
struct FileCommand: Codable {
    var kind: String
    var offer: FileOffer? = nil
    var id: String? = nil
    var ticket: String? = nil
    var rate: Int? = nil
    var index: Int? = nil
    var hash: String? = nil
    var request: String? = nil
    var reason: String? = nil
}
enum FileProtocol {
    static let clipboardLimit: Int64 = 100 * 1_048_576
    static let fileLimit: Int64 = 1_073_741_824
    static let batchLimit: Int64 = 4 * 1_073_741_824
    static let chunkSize = 16_300
    static let context = "unictrl files v1"
    static let magic = Data("UNICTRF1".utf8)
    static let reply = Data("FILEOK01".utf8)
    static func validName(_ name: String) -> Bool {
        guard !name.isEmpty, name.utf8.count <= 240, name != ".", name != "..", !name.hasSuffix("."), !name.hasSuffix(" "),
              !name.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) || "/\\:*?\"<>|".unicodeScalars.contains($0) }) else { return false }
        let stem = String(name.split(separator: ".", omittingEmptySubsequences: false).first ?? "").uppercased()
        return !(["CON", "PRN", "AUX", "NUL"] + (1...9).flatMap { ["COM\($0)", "LPT\($0)"] }).contains(stem)
    }
    static func validate(_ offer: FileOffer) throws {
        guard UUID(uuidString: offer.id) != nil, ["clipboard", "drop"].contains(offer.mode), (1...32).contains(offer.files.count) else { throw error("Invalid file offer") }
        var total: Int64 = 0, names = Set<String>()
        for file in offer.files {
            guard validName(file.name), file.size >= 0, file.size <= fileLimit,
                  names.insert(file.name.precomposedStringWithCanonicalMapping.lowercased()).inserted else { throw error("Unsupported filename, duplicate name or file larger than 1 GiB") }
            total += file.size
        }
        guard total <= (offer.mode == "clipboard" ? clipboardLimit : batchLimit) else {
            throw error(offer.mode == "clipboard" ? "Clipboard files exceed 100 MiB; use drag and drop (1 GiB per file / 4 GiB per batch)" : "File batch exceeds 4 GiB")
        }
    }
    static func encode(_ command: FileCommand) throws -> Data {
        let encoder = JSONEncoder(); encoder.outputFormatting = .withoutEscapingSlashes
        let data = try encoder.encode(command)
        guard data.count < 16_000 else { throw error("File command too large") }; return Data([0x35]) + data
    }
    static func decode(_ data: Data) throws -> FileCommand {
        guard data.first == 0x35, data.count <= 16_000 else { throw error("Invalid file command") }
        return try JSONDecoder().decode(FileCommand.self, from: data.dropFirst())
    }
    static func chunk(index: Int, offset: Int64, bytes: Data) -> Data {
        var data = Data([0x36, UInt8(index >> 8), UInt8(index & 255)])
        withUnsafeBytes(of: UInt64(offset).bigEndian) { data.append(contentsOf: $0) }; data.append(bytes); return data
    }
    static func parseChunk(_ data: Data) throws -> (Int, Int64, Data) {
        guard data.count > 11, data.count <= chunkSize + 11, data.first == 0x36 else { throw error("Invalid file chunk") }
        let bytes = Array(data.prefix(11)); var offset: UInt64 = 0
        for value in bytes[3..<11] { offset = offset << 8 | UInt64(value) }
        guard offset <= UInt64(Int64.max) else { throw error("Invalid file offset") }
        return (Int(bytes[1]) << 8 | Int(bytes[2]), Int64(offset), Data(data.dropFirst(11)))
    }
    static func rate(_ local: Int, _ remote: Int) -> Int { let values = [local, remote].filter { $0 > 0 }; return min(100, max(1, values.min() ?? 100)) }
    static func error(_ text: String) -> NSError { NSError(domain: "UnictrlFiles", code: 1, userInfo: [NSLocalizedDescriptionKey: text]) }
}

/// Private partial files become visible together only after every byte and hash has been checked.
final class FileInbox {
    let offer: FileOffer
    private let partial: URL, finished: URL
    private var handle: FileHandle?, digest = SHA256(), offset: Int64 = 0, index = 0
    private var committed = false
    init(offer: FileOffer, directory: URL) throws {
        try FileProtocol.validate(offer); self.offer = offer
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        if let capacity = try? directory.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]).volumeAvailableCapacityForImportantUsage,
           capacity < offer.size + 16 * 1_048_576 { throw FileProtocol.error("Not enough free space") }
        let id = UUID().uuidString
        partial = directory.appendingPathComponent(".partial-" + id)
        finished = directory.appendingPathComponent("Received-" + id)
        try FileManager.default.createDirectory(at: partial, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        try FileManager.default.createDirectory(at: partial.appendingPathComponent("parts"), withIntermediateDirectories: false)
        try FileManager.default.createDirectory(at: partial.appendingPathComponent("ready"), withIntermediateDirectories: false)
    }
    deinit { try? handle?.close(); if !committed { try? FileManager.default.removeItem(at: partial) } }
    func begin(_ fileIndex: Int) throws {
        guard handle == nil, fileIndex == index, index < offer.files.count else { throw FileProtocol.error("File order mismatch") }
        let path = partial.appendingPathComponent("parts/.part-\(index)")
        guard FileManager.default.createFile(atPath: path.path, contents: nil, attributes: [.posixPermissions: 0o600]) else { throw FileProtocol.error("Cannot create received file") }
        handle = try FileHandle(forWritingTo: path); offset = 0; digest = SHA256()
    }
    func append(index fileIndex: Int, offset expected: Int64, bytes: Data) throws {
        guard let handle, fileIndex == index, expected == offset, !bytes.isEmpty, bytes.count <= FileProtocol.chunkSize,
              offset + Int64(bytes.count) <= offer.files[index].size else { throw FileProtocol.error("File chunk order or size mismatch") }
        try handle.write(contentsOf: bytes); digest.update(data: bytes); offset += Int64(bytes.count)
    }
    func end(_ fileIndex: Int, hash: String) throws {
        guard let handle, fileIndex == index, offset == offer.files[index].size,
              digest.finalize().map({ String(format: "%02x", $0) }).joined() == hash else { throw FileProtocol.error("File integrity check failed") }
        try handle.synchronize(); try handle.close(); self.handle = nil
        try FileManager.default.moveItem(at: partial.appendingPathComponent("parts/.part-\(index)"), to: partial.appendingPathComponent("ready").appendingPathComponent(offer.files[index].name)); index += 1
    }
    func complete() throws -> [URL] {
        guard handle == nil, index == offer.files.count else { throw FileProtocol.error("Incomplete file transfer") }
        try FileManager.default.moveItem(at: partial.appendingPathComponent("ready"), to: finished); try? FileManager.default.removeItem(at: partial); committed = true
        return offer.files.map { finished.appendingPathComponent($0.name) }
    }
}
