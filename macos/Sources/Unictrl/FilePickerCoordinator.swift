import AppKit

@MainActor protocol FileChoosing: AnyObject {
    var urls: [URL] { get }
    func begin(completion: @escaping (Bool) -> Void)
    func bringToFront()
}

/// Owned by the file service, rather than a transient MenuBarExtra view. Reopening the menu
/// therefore cannot create a second panel while the first file selection is still pending.
@MainActor final class FilePickerCoordinator {
    private let makePicker: () -> any FileChoosing
    private var active: (any FileChoosing)?
    init(makePicker: (() -> any FileChoosing)? = nil) { self.makePicker = makePicker ?? { SystemFileChooser() } }
    func chooseFiles(selected: @escaping ([URL]) -> Void) {
        if let active { active.bringToFront(); return }
        let picker = makePicker(); active = picker
        picker.begin { [weak self, weak picker] accepted in
            guard let self, let picker, self.active === picker else { return }
            self.active = nil
            if accepted { selected(picker.urls) }
        }
        if active === picker { picker.bringToFront() }
    }
}

@MainActor private final class SystemFileChooser: FileChoosing {
    private let panel: NSOpenPanel = {
        let panel = NSOpenPanel(); panel.canChooseFiles = true; panel.canChooseDirectories = false
        panel.allowsMultipleSelection = true; return panel
    }()
    var urls: [URL] { panel.urls }
    func begin(completion: @escaping (Bool) -> Void) {
        NSApp.activate(ignoringOtherApps: true)
        panel.begin { completion($0 == .OK) }
    }
    func bringToFront() {
        NSApp.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(nil); panel.orderFrontRegardless()
        // Activation can complete on the next run-loop turn when launched from MenuBarExtra.
        DispatchQueue.main.async { [weak self] in
            guard let self, self.panel.isVisible else { return }
            NSApp.activate(ignoringOtherApps: true); self.panel.makeKeyAndOrderFront(nil)
        }
    }
}
