import Foundation
import CryptoKit

/// Only clipboard text v1. All JSON records fit the existing 16 KiB encrypted frame.
struct ClipboardPacket: Codable {
    var kind: String
    var id: String? = nil
    var revision: UInt64? = nil
    var count: Int? = nil
    var hash: String? = nil
    var offset: Int? = nil
    var data: String? = nil
}

struct ClipboardEffects {
    var packets: [ClipboardPacket] = []
    var text: String? = nil
    var id: String? = nil
}

/// Serial, platform-independent replica. Mac assigns revisions; receiver-originated writes never
/// become local copy events. A pending Windows copy takes precedence over earlier Mac offers.
final class ClipboardReplica {
    static let maxBytes = 1_048_576
    static let chunkBytes = 8_192
    static let context = "unictrl clipboard v1"
    let authority: Bool
    private(set) var revision: UInt64 = 0
    private(set) var latestID: String?
    private(set) var localID: String?
    private var pendingLocal: String?
    private var incoming: (id: String, count: Int, hash: String, bytes: Data)?
    init(authority: Bool) { self.authority = authority }
    func cancelIncoming() { incoming = nil }
    func invalidateLocal() { incoming = nil; latestID = nil; localID = nil; pendingLocal = nil }
    func isCurrent(_ id: String) -> Bool { latestID == id && pendingLocal == nil }
    private func order(_ id: String) -> ClipboardPacket {
        revision += 1; latestID = id; incoming = nil
        return ClipboardPacket(kind: "order", id: id, revision: revision)
    }
    func copy(_ text: String, id: String = UUID().uuidString) -> [ClipboardPacket] {
        let bytes = Data(text.utf8)
        guard !bytes.isEmpty, bytes.count <= Self.maxBytes, !text.contains("\0"), UUID(uuidString: id) != nil else { return [] }
        incoming = nil; localID = id
        var result: [ClipboardPacket] = []
        if authority { result.append(order(id)) } else { pendingLocal = id }
        result.append(ClipboardPacket(kind: "begin", id: id, count: bytes.count, hash: Self.digest(bytes)))
        for offset in stride(from: 0, to: bytes.count, by: Self.chunkBytes) {
            result.append(ClipboardPacket(kind: "chunk", id: id, offset: offset,
                data: bytes.subdata(in: offset..<min(offset + Self.chunkBytes, bytes.count)).base64EncodedString()))
        }
        result.append(ClipboardPacket(kind: "end", id: id))
        return result
    }
    func receive(_ packet: ClipboardPacket) -> ClipboardEffects {
        var result = ClipboardEffects()
        guard let id = packet.id, UUID(uuidString: id) != nil else { return result }
        switch packet.kind {
        case "order":
            guard !authority, let next = packet.revision, next > revision else { return result }
            revision = next; latestID = id; incoming = nil
            if pendingLocal == id { pendingLocal = nil }
        case "begin":
            guard let count = packet.count, count > 0, count <= Self.maxBytes,
                  let hash = packet.hash, hash.count == 64, hash.allSatisfy({ $0.isHexDigit }) else { return result }
            if authority { result.packets.append(order(id)) }
            guard latestID == id, pendingLocal == nil else { return result }
            incoming = (id, count, hash, Data())
        case "chunk":
            guard var transfer = incoming, transfer.id == id, packet.offset == transfer.bytes.count,
                  let data = packet.data, data.utf8.count <= 11_000,
                  let bytes = Data(base64Encoded: data), !bytes.isEmpty, bytes.count <= Self.chunkBytes,
                  transfer.bytes.count + bytes.count <= transfer.count else {
                if incoming?.id == id { incoming = nil }; return result
            }
            transfer.bytes.append(bytes); incoming = transfer
        case "end":
            guard let transfer = incoming, transfer.id == id else { return result }
            incoming = nil
            guard isCurrent(id), transfer.bytes.count == transfer.count, Self.digest(transfer.bytes) == transfer.hash,
                  let text = String(data: transfer.bytes, encoding: .utf8), !text.contains("\0") else { return result }
            result.text = text; result.id = id
        default: break
        }
        return result
    }
    static func digest(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }
    static func message(_ packet: ClipboardPacket) -> MessageWriter? {
        guard let data = try? JSONEncoder().encode(packet), data.count < SecureChannel.maxFrame - 17 else { return nil }
        var message = MessageWriter(.clipboardData); message.bytes(data); return message
    }
    static func parse(_ message: Data) -> ClipboardPacket? {
        guard message.first == MessageType.clipboardData.rawValue, message.count < SecureChannel.maxFrame - 16 else { return nil }
        return try? JSONDecoder().decode(ClipboardPacket.self, from: message.dropFirst())
    }
}
