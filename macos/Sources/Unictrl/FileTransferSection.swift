import SwiftUI
import UniformTypeIdentifiers

/// Daily operations live in the menu bar panel; settings contain preferences and metadata only.
struct FileTransferSection: View {
    @ObservedObject var controller: AppController
    @ObservedObject var service: MacFileTransfer
    var body: some View {
        FileTransferCard(enabled: controller.clipboardFilesEnabled && controller.clipboardEnabled, service: service) {
            controller.clipboardEnabled = true; controller.clipboardFilesEnabled = true
        }
    }
}

struct FileTransferCard: View {
    let enabled: Bool
    @ObservedObject var service: MacFileTransfer
    let enable: () -> Void
    @State private var hovering = false
    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack {
                Label("File transfer", systemImage: "arrow.up.arrow.down.square").font(.system(size: 13, weight: .semibold))
                Spacer()
                if service.busy { Button("Cancel") { service.cancel() }.controlSize(.small) }
            }
            if !enabled {
                Text("Send files to your paired computer").font(.caption).foregroundStyle(.secondary)
                Button("Enable file sharing", action: enable)
            } else {
                VStack(spacing: 5) {
                    Image(systemName: "tray.and.arrow.up").font(.title2).foregroundStyle(Color.pine)
                    Text("Drop files to send").font(.system(size: 12, weight: .medium))
                    Button("Choose files…") { chooseFiles() }.controlSize(.small).disabled(service.busy)
                    Text("1 GiB/file · recipient accepts first").font(.system(size: 10)).foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity).padding(10)
                .background(RoundedRectangle(cornerRadius: 10).fill(hovering ? Color.pine.opacity(0.16) : Color.secondary.opacity(0.06)))
                .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Color.secondary.opacity(0.4), style: StrokeStyle(lineWidth: 1, dash: [4])))
                .onDrop(of: [UTType.fileURL.identifier], isTargeted: $hovering) { providers in drop(providers) }
                if !service.incoming.isEmpty {
                    VStack(alignment: .leading, spacing: 6) {
                        Label("Incoming files", systemImage: "tray.and.arrow.down.fill").font(.system(size: 12, weight: .semibold)).foregroundStyle(Color.pine)
                        Text(service.incoming).font(.caption).lineLimit(3)
                        Button("Receive to Downloads") { service.receive() }.disabled(!service.canReceive).controlSize(.small)
                    }.padding(9).frame(maxWidth: .infinity, alignment: .leading).background(Color.pine.opacity(0.09), in: RoundedRectangle(cornerRadius: 8))
                }
                if service.busy { ProgressView(value: service.progress) }
                Text(service.status).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                if !service.notice.isEmpty {
                    HStack(alignment: .top) {
                        Image(systemName: "info.circle.fill").foregroundStyle(.orange)
                        Text(service.notice).font(.caption).fixedSize(horizontal: false, vertical: true)
                        Button { service.dismissNotice() } label: { Image(systemName: "xmark") }.buttonStyle(.plain)
                    }.padding(8).background(Color.orange.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
                }
                Text("Copy/paste ≤100 MiB · temporary cache clears after 1 hour").font(.system(size: 10)).foregroundStyle(.secondary)
            }
        }
        .padding(10).background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 12))
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

struct FileTransferPreferences: View {
    @ObservedObject var controller: AppController
    @ObservedObject var service: MacFileTransfer
    var body: some View {
        Toggle("Share files", isOn: $controller.clipboardFilesEnabled).disabled(!controller.clipboardEnabled)
        Picker("File transfer limit", selection: $controller.fileRateMiB) {
            ForEach([1, 5, 10, 20, 50, 100], id: \.self) { Text("\($0) MiB/s").tag($0) }
        }
        Text("Click the menu bar icon to send or receive files. Copy/paste: 100 MiB per batch. Panel: 1 GiB per file, 4 GiB per batch. Zip folders first. Files within 100 MiB are prepared automatically for native paste. Receive saves them to Downloads.").font(.caption).foregroundStyle(.secondary)
        DisclosureGroup("Transfer history (\(service.history.count)/100)") {
            if service.history.isEmpty { Text("No transfers yet").foregroundStyle(.secondary) }
            ForEach(service.history) { record in
                VStack(alignment: .leading, spacing: 2) {
                    Text(record.date, style: .date).font(.caption).foregroundStyle(.secondary)
                    Text(record.summary).font(.caption).textSelection(.enabled)
                }.padding(.vertical, 3)
            }
        }
        HStack {
            Button("Clear history") { service.clearHistory() }
            Button("Clear temporary clipboard files") { service.cleanCache(all: true) }.disabled(service.busy)
        }
        Text("History stores names, sizes, time and result only. Clipboard payloads expire after 1 hour; saved destination files are yours. Existing files from older versions are left in place.").font(.caption).foregroundStyle(.secondary)
    }
}
