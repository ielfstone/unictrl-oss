import SwiftUI
import UniformTypeIdentifiers

struct FileTransferSection: View {
    @ObservedObject var controller: AppController
    @ObservedObject var service: MacFileTransfer
    @State private var hovering = false
    var body: some View {
        Toggle("Share files", isOn: $controller.clipboardFilesEnabled).disabled(!controller.clipboardEnabled)
        if controller.clipboardFilesEnabled {
            Picker("File transfer limit", selection: $controller.fileRateMiB) {
                ForEach([1, 5, 10, 20, 50, 100], id: \.self) { Text("\($0) MiB/s").tag($0) }
            }
            VStack(spacing: 8) {
                Image(systemName: "tray.and.arrow.up").font(.title2)
                Text("Drop files here to offer them to the other computer")
                Button("Choose files…") { chooseFiles() }.disabled(service.busy)
                Text("The receiver clicks Receive before file data is sent.").font(.caption).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity).padding()
            .background(hovering ? Color.accentColor.opacity(0.12) : Color.secondary.opacity(0.06))
            .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(style: StrokeStyle(lineWidth: 1, dash: [5])).foregroundStyle(.secondary))
            .onDrop(of: [UTType.fileURL.identifier], isTargeted: $hovering) { providers in drop(providers) }
            Text(service.status).font(.caption).textSelection(.enabled)
            if service.busy { ProgressView(value: service.progress); Button("Cancel transfer") { service.cancel() } }
            if !service.incoming.isEmpty {
                Text(service.incoming).font(.caption).textSelection(.enabled)
                Button("Receive files") { service.receive() }.disabled(service.busy)
            }
            if !service.receivedURLs.isEmpty {
                HStack {
                    Button("Show received files") { service.showReceived() }
                    Button("Copy received files") { service.copyReceived() }
                }
            }
            Text("Copy/paste: 100 MiB per batch. Drop: 1 GiB per file, 4 GiB per batch, up to 32 files. Folders must be zipped first. Paste starts the download; keep the destination app active to finish pasting automatically. Files are saved in Downloads/Unictrl Received.").font(.caption).foregroundStyle(.secondary)
        }
    }
    private func chooseFiles() {
        let panel = NSOpenPanel(); panel.canChooseFiles = true; panel.canChooseDirectories = false; panel.allowsMultipleSelection = true
        panel.begin { if $0 == .OK { service.offer(panel.urls) } }
    }
    private func drop(_ providers: [NSItemProvider]) -> Bool {
        guard !service.busy, providers.count <= 32 else { return false }
        let group = DispatchGroup(), lock = NSLock()
        var urls: [(Int, URL)] = []
        for (index, provider) in providers.enumerated() {
            group.enter()
            provider.loadItem(forTypeIdentifier: UTType.fileURL.identifier, options: nil) { item, _ in
                defer { group.leave() }
                let url = (item as? URL) ?? (item as? Data).flatMap { URL(dataRepresentation: $0, relativeTo: nil) }
                if let url, url.isFileURL { lock.withLock { urls.append((index, url)) } }
            }
        }
        group.notify(queue: .main) { service.offer(urls.sorted { $0.0 < $1.0 }.map(\.1)) }
        return true
    }
}
