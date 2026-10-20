import Foundation
import Security
import CryptoKit

struct UpdateAsset: Codable { let url: String; let sha256: String; let size: Int }
struct UpdateRelease: Codable { let version: String; let macos: UpdateAsset; let windows: UpdateAsset }
enum UpdateProtocol {
    static let feed = URL(string: "https://github.com/ielfstone/unictrl-oss/releases/latest/download/update-v1.json")!
    static let publicKey = "MIIBigKCAYEA0ZI9WH2fvSbKi7pdq0XLYS7eztsiXcLa7TnshlhkANyfAE3MtHBk9Ryvnx1eckzZqCtrlVYH64M3sMQvXwme+f7I3NSvdEBs2aukSp+k5QzU8VDj5iIu8t+juop3X9zLtlQm8QykHpyYytnhk5WsvsX+UZ4zAaLBAey/2g1gpYeHR3dFQG2YNbXvQTMEFrm7ou3THnoUhPYG13wYC0T1YZs9EYluo+YVoBoQh6Oyn41MVeP53SZxIR54WVvahxt02byFtY/P506fRvH8gBD5e0w+OhinxulZJ9uMXiHxSOdpsYq5Xs1CjOEAAahNEwpwXMXkez/0uSxuBQx/fvMg2+dmr4rbp2EG+h6g6P3kmLv9JU6BQgI0s5kFEQKV/s4T23SMnbBytuH+qehotAdeptNA8aL1m2qhc1LC/SRCmj/lX/hyr3sShGH7kzyEZW3aKwUVlSyVaTh+m0AbRiZNGMt2i/mGqkrBVz+gwK5UDFLe/tjCHtRH91Y9+6GWQZNpAgMBAAE="
    struct Envelope: Codable { let payload: String; let signature: String }
    static func version(_ value: String) -> [Int]? {
        let parts = value.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { return nil }
        let numbers = parts.compactMap { part -> Int? in
            guard !part.isEmpty, part.count <= 5, part.allSatisfy({ $0.isASCII && $0.isNumber }), let number = Int(part) else { return nil }
            return number
        }
        return numbers.count == 3 ? numbers : nil
    }
    static func newer(_ candidate: String, than current: String) -> Bool {
        guard let a = version(candidate), let b = version(current) else { return false }
        return b.lexicographicallyPrecedes(a)
    }
    static func parse(_ data: Data, key: String = publicKey) throws -> UpdateRelease {
        guard data.count <= 65_536 else { throw failure("Update manifest too large") }
        let envelope = try JSONDecoder().decode(Envelope.self, from: data)
        guard let payload = Data(base64Encoded: envelope.payload), let signature = Data(base64Encoded: envelope.signature),
              let keyData = Data(base64Encoded: key),
              let rsa = SecKeyCreateWithData(keyData as CFData, [kSecAttrKeyType: kSecAttrKeyTypeRSA, kSecAttrKeyClass: kSecAttrKeyClassPublic] as CFDictionary, nil),
              SecKeyVerifySignature(rsa, .rsaSignatureMessagePKCS1v15SHA256, payload as CFData, signature as CFData, nil) else {
            throw failure("Update signature invalid")
        }
        let release = try JSONDecoder().decode(UpdateRelease.self, from: payload)
        guard version(release.version) != nil else { throw failure("Update version invalid") }
        for asset in [release.macos, release.windows] {
            guard let url = URL(string: asset.url), url.scheme == "https", url.host == "github.com", url.user == nil, url.password == nil,
                  url.path.hasPrefix("/ielfstone/unictrl-oss/releases/download/v\(release.version)/"), url.query == nil, url.fragment == nil,
                  asset.sha256.count == 64, asset.sha256.allSatisfy({ $0.isHexDigit && $0.isASCII }), asset.size > 0, asset.size <= 1_073_741_824 else {
                throw failure("Update asset invalid")
            }
        }
        return release
    }
    static func verify(_ url: URL, asset: UpdateAsset) throws {
        let handle = try FileHandle(forReadingFrom: url); defer { try? handle.close() }
        var digest = SHA256(), count = 0
        while let bytes = try handle.read(upToCount: 1_048_576), !bytes.isEmpty { count += bytes.count; digest.update(data: bytes) }
        guard count == asset.size, digest.finalize().map({ String(format: "%02x", $0) }).joined() == asset.sha256.lowercased() else {
            throw failure("Update size or checksum invalid")
        }
    }
    static func failure(_ text: String) -> NSError { NSError(domain: "UnictrlUpdate", code: 1, userInfo: [NSLocalizedDescriptionKey: text]) }
}
