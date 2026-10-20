import AppKit

/// A non-activating notice: visible while pasting without stealing the destination app's focus.
enum FileNotice {
    private static var panel: NSPanel?
    private static var dismiss: DispatchWorkItem?
    static func show(_ message: String) {
        dismiss?.cancel(); panel?.close()
        guard let screen = NSScreen.main else { return }
        let frame = screen.visibleFrame
        let window = NSPanel(contentRect: NSRect(x: frame.maxX - 390, y: frame.maxY - 130, width: 370, height: 100), styleMask: [.nonactivatingPanel, .borderless], backing: .buffered, defer: false)
        window.level = .floating; window.isOpaque = false; window.backgroundColor = .clear; window.hasShadow = true
        let effect = NSVisualEffectView(frame: NSRect(x: 0, y: 0, width: 370, height: 100))
        effect.material = .popover; effect.wantsLayer = true; effect.layer?.cornerRadius = 12
        let title = NSTextField(labelWithString: "Unictrl · File transfer"); title.font = .boldSystemFont(ofSize: 13); title.frame = NSRect(x: 16, y: 70, width: 338, height: 18)
        let detail = NSTextField(wrappingLabelWithString: message); detail.font = .systemFont(ofSize: 12); detail.frame = NSRect(x: 16, y: 10, width: 338, height: 55)
        effect.addSubview(title); effect.addSubview(detail); window.contentView = effect; window.orderFrontRegardless(); panel = window
        let work = DispatchWorkItem { if panel === window { panel?.close(); panel = nil } }; dismiss = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 9, execute: work)
    }
}
