import Foundation
import Testing
@testable import Unictrl

private final class UpdateFixtureProtocol: URLProtocol, @unchecked Sendable {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: "HTTP/1.1", headerFields: ["Content-Length": "65536"])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(repeating: 42, count: request.url!.path == "/slow" ? 32768 : 65536))
        if request.url!.path == "/slow" { return }
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() { }
}

@Suite(.serialized) struct UpdateDownloadTests {
    private func configuration() -> URLSessionConfiguration { let c = URLSessionConfiguration.ephemeral; c.protocolClasses = [UpdateFixtureProtocol.self]; return c }
    @Test func boundedDownloadWritesDiskAndReportsProgress() async throws {
        let progress = AsyncStream<Double>.makeStream()
        let download = UpdateDownload(expected: 65536, configuration: configuration()) { progress.continuation.yield($0) }
        let result = try await download.download(URLRequest(url: URL(string: "https://fixture.invalid/update")!))
        defer { try? FileManager.default.removeItem(at: result) }
        #expect(try Data(contentsOf: result) == Data(repeating: 42, count: 65536))
        for await fraction in progress.stream { #expect(fraction == 1); break }; progress.continuation.finish()
    }
    @Test func cancelDuringDownloadRemovesPartialFile() async throws {
        let progress = AsyncStream<Double>.makeStream()
        let download = UpdateDownload(expected: 65536, configuration: configuration()) { progress.continuation.yield($0) }
        let job = Task { try await download.download(URLRequest(url: URL(string: "https://fixture.invalid/slow")!)) }
        for await fraction in progress.stream { #expect(fraction == 0.5); break }
        job.cancel()
        do { _ = try await job.value; Issue.record("Cancelled download succeeded") } catch is CancellationError { }
        #expect(!FileManager.default.fileExists(atPath: download.destination.path))
        progress.continuation.finish()
    }

    @Test func preCancelledDownloadDoesNotStart() async throws {
        let download = UpdateDownload(expected: 10) { _ in }
        download.cancel()
        do { _ = try await download.download(URLRequest(url: URL(string: "https://example.invalid/update")!)); Issue.record("Cancelled update started") }
        catch is CancellationError { }
    }
}
