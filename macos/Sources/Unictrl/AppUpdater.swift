import AppKit
import Security

/// Signed release feed; background download followed by an explicit restart, preserving live input.
@MainActor final class AppUpdater: ObservableObject {
    static let shared = AppUpdater()
    @Published var automatic = UserDefaults.standard.object(forKey: "automaticUpdates") as? Bool ?? true {
        didSet { UserDefaults.standard.set(automatic, forKey: "automaticUpdates"); if automatic { check() } }
    }
    @Published private(set) var status = "Updates come from your personal fork"
    @Published private(set) var busy = false
    @Published private(set) var preparedVersion: String?
    @Published private(set) var downloadProgress: Double?
    private var operation: Task<Void, Never>?
    private var candidate: URL?
    private var work: URL?
    private var timer: Timer?
    func start() {
        guard timer == nil else { return }
        timer = Timer(timeInterval: 21_600, repeats: true) { [weak self] _ in
            Task { @MainActor in if self?.automatic == true { self?.check() } }
        }
        RunLoop.main.add(timer!, forMode: .common)
        if automatic { check() }
    }
    func check() {
        guard !busy, candidate == nil else { return }
        busy = true; status = "Checking for updates…"
        operation = Task {
            defer { busy = false; downloadProgress = nil; operation = nil }
            do {
                var request = URLRequest(url: UpdateProtocol.feed); request.timeoutInterval = 30; request.cachePolicy = .reloadIgnoringLocalCacheData
                let (data, response) = try await URLSession.shared.data(for: request)
                if (response as? HTTPURLResponse)?.statusCode == 404 { status = "No published update yet"; return }
                guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw UpdateProtocol.failure("Update service unavailable") }
                let release = try UpdateProtocol.parse(data)
                let current = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
                guard UpdateProtocol.newer(release.version, than: current) else { status = "Up to date · \(current)"; return }
                status = "Downloading \(release.version)…"
                var download = URLRequest(url: URL(string: release.macos.url)!); download.timeoutInterval = 300
                downloadProgress = 0
                let downloader = UpdateDownload(expected: release.macos.size) { [weak self] fraction in
                    Task { @MainActor in
                        guard let self, self.busy, self.downloadProgress != nil, self.operation?.isCancelled == false else { return }
                        self.downloadProgress = fraction; self.status = "Downloading \(release.version) · \(Int(fraction * 100))%"
                    }
                }
                let temporary = try await downloader.download(download)
                defer { try? FileManager.default.removeItem(at: temporary) }
                try Task.checkCancellation()
                status = "Verifying update…"; downloadProgress = nil
                try UpdateProtocol.verify(temporary, asset: release.macos)
                let installed = Bundle.main.bundleURL.standardizedFileURL
                guard installed.pathExtension == "app", FileManager.default.isWritableFile(atPath: installed.deletingLastPathComponent().path) else {
                    throw UpdateProtocol.failure("Move Unictrl to a writable Applications folder before updating")
                }
                let root = installed.deletingLastPathComponent().appendingPathComponent(".Unictrl-update-" + UUID().uuidString)
                try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
                work = root
                let archive = root.appendingPathComponent("update.zip")
                try FileManager.default.moveItem(at: temporary, to: archive)
                let app = try await Task.detached(priority: .utility) { try Self.prepare(archive: archive, root: root, installed: installed, expected: release.version) }.value
                try Task.checkCancellation()
                candidate = app; preparedVersion = release.version; status = "\(release.version) ready · restart to install"
            } catch {
                if let work { try? FileManager.default.removeItem(at: work) }; work = nil
                status = Task.isCancelled ? "Download cancelled" : "Update failed: \(error.localizedDescription)"
            }
        }
    }
    func cancelDownload() { operation?.cancel() }
    nonisolated static func run(_ command: String, _ args: [String]) throws -> String {
        let task = Process(); task.executableURL = URL(fileURLWithPath: command); task.arguments = args
        let output = Pipe(); task.standardOutput = output; task.standardError = FileHandle.nullDevice
        try task.run(); let data = output.fileHandleForReading.readDataToEndOfFile(); task.waitUntilExit()
        guard task.terminationStatus == 0 else { throw UpdateProtocol.failure("Update validation failed: " + command) }
        return String(decoding: data, as: UTF8.self)
    }
    nonisolated static func prepare(archive: URL, root: URL, installed: URL, expected: String) throws -> URL {
        let names = try run("/usr/bin/zipinfo", ["-1", archive.path]).split(separator: "\n")
        guard !names.isEmpty, names.count < 5000, names.allSatisfy({
            ($0 == "Unictrl.app/" || $0.hasPrefix("Unictrl.app/")) && !$0.split(separator: "/").contains("..") && !$0.contains("\\")
        }) else { throw UpdateProtocol.failure("Invalid update archive paths") }
        let listing = try run("/usr/bin/zipinfo", ["-l", archive.path])
        guard !listing.split(separator: "\n").contains(where: { $0.hasPrefix("l") }) else { throw UpdateProtocol.failure("Unexpected update symlink") }
        _ = try run("/usr/bin/ditto", ["-x", "-k", archive.path, root.path])
        let app = root.appendingPathComponent("Unictrl.app")
        guard let bundle = Bundle(url: app), bundle.bundleIdentifier == "com.enlivatech.unictrl",
              bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String == expected else { throw UpdateProtocol.failure("Update bundle mismatch") }
        var oldCode: SecStaticCode?, newCode: SecStaticCode?, requirement: SecRequirement?
        guard SecStaticCodeCreateWithPath(installed as CFURL, [], &oldCode) == errSecSuccess,
              SecCodeCopyDesignatedRequirement(oldCode!, [], &requirement) == errSecSuccess,
              SecStaticCodeCreateWithPath(app as CFURL, [], &newCode) == errSecSuccess,
              SecStaticCodeCheckValidity(newCode!, SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSStrictValidate), requirement) == errSecSuccess else {
            throw UpdateProtocol.failure("Update must use the same signing identity")
        }
        return app
    }
    func install() {
        guard let candidate, let work else { return }
        do {
            let helper = work.appendingPathComponent("install.sh")
            try Self.helper.write(to: helper, atomically: true, encoding: .utf8)
            let process = Process(); process.executableURL = URL(fileURLWithPath: "/bin/sh")
            process.arguments = [helper.path, String(ProcessInfo.processInfo.processIdentifier), Bundle.main.bundleURL.path, candidate.path, work.path]
            process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice
            try process.run(); self.candidate = nil; self.work = nil; AppController.shared.quit()
        } catch { status = "Cannot start installer: \(error.localizedDescription)" }
    }
    func installOnQuit() -> Bool {
        guard candidate != nil else { return false }
        install(); return true
    }
    // Every path is an argv value. No interpolated shell commands and no privacy database changes.
    nonisolated static let helper = """
    set -eu
    oldpid="$1"; target="$2"; source="$3"; work="$4"
    count=0
    while kill -0 "$oldpid" 2>/dev/null; do
      count=$((count+1)); [ "$count" -lt 120 ] || exit 1; sleep 0.5
    done
    backup="${target}.previous"
    [ ! -e "$backup" ] || /bin/mv "$backup" "${backup}.$(/bin/date +%s)"
    /bin/mv "$target" "$backup"
    if ! /bin/mv "$source" "$target"; then /bin/mv "$backup" "$target"; exit 1; fi
    if ! /usr/bin/open -n "$target"; then
      /bin/mv "$target" "$source"; /bin/mv "$backup" "$target"; /usr/bin/open -n "$target"; exit 1
    fi
    /bin/rm -rf "$work"
    """
}
