import Foundation

/// A bounded download to disk. Cancellation never leaves a partial update behind.
final class UpdateDownload: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var task: URLSessionDataTask?
    private var cancelled = false
    private var continuation: CheckedContinuation<URL, Error>?
    private var session: URLSession?
    private var file: FileHandle?
    let destination = FileManager.default.temporaryDirectory.appendingPathComponent("Unictrl-download-" + UUID().uuidString)
    private var failure: Error?
    private var received = 0
    private let configuration: URLSessionConfiguration
    private let expected: Int
    private let progress: (Double) -> Void
    init(expected: Int, configuration: URLSessionConfiguration = .ephemeral, progress: @escaping (Double) -> Void) { self.expected = expected; self.configuration = configuration; self.progress = progress }
    func cancel() { let active = lock.withLock { cancelled = true; return task }; active?.cancel() }
    func download(_ request: URLRequest) async throws -> URL {
        try await withTaskCancellationHandler(operation: {
            try await withCheckedThrowingContinuation { done in
                lock.lock()
                if cancelled { lock.unlock(); done.resume(throwing: CancellationError()); return }
                do {
                    FileManager.default.createFile(atPath: destination.path, contents: nil, attributes: [.posixPermissions: 0o600])
                    file = try FileHandle(forWritingTo: destination)
                    continuation = done
                    let config = configuration
                    config.timeoutIntervalForRequest = 30; config.timeoutIntervalForResource = 600
                    let session = URLSession(configuration: config, delegate: self, delegateQueue: nil)
                    self.session = session; let task = session.dataTask(with: request); self.task = task
                    lock.unlock(); task.resume()
                } catch { lock.unlock(); try? FileManager.default.removeItem(at: destination); done.resume(throwing: error) }
            }
        }, onCancel: { self.cancel() })
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard (response as? HTTPURLResponse)?.statusCode == 200,
              response.expectedContentLength == -1 || response.expectedContentLength == expected else {
            completionHandler(.cancel); return
        }
        completionHandler(.allow)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        do {
            received += data.count
            guard received <= expected else { throw UpdateProtocol.failure("Update download exceeds declared size") }
            try file?.write(contentsOf: data); progress(Double(received) / Double(expected))
        } catch { failure = error; dataTask.cancel() }
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        try? file?.close(); file = nil
        let result: Result<URL, Error>
        if lock.withLock({ cancelled }) { result = .failure(CancellationError()) }
        else if let failure { result = .failure(failure) }
        else if let error { result = .failure(error) }
        else if received != expected { result = .failure(UpdateProtocol.failure("Incomplete update download")) }
        else { result = .success(destination) }
        if case .failure = result { try? FileManager.default.removeItem(at: destination) }
        let done = lock.withLock { let value = continuation; continuation = nil; self.task = nil; return value }
        done?.resume(with: result); session.finishTasksAndInvalidate(); self.session = nil
    }
}
