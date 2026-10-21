import Foundation
import Testing
@testable import Unictrl

@MainActor @Suite struct FilePickerCoordinatorTests {
    private final class Picker: FileChoosing {
        var urls = [URL(fileURLWithPath: "/test/file.txt")]
        var shown = 0, foregrounded = 0
        var completion: ((Bool) -> Void)?
        func begin(completion: @escaping (Bool) -> Void) { shown += 1; self.completion = completion }
        func bringToFront() { foregrounded += 1 }
    }
    @Test func reopeningMenuRaisesTheSamePendingPicker() {
        var made = 0; let picker = Picker()
        let coordinator = FilePickerCoordinator { made += 1; return picker }
        coordinator.chooseFiles { _ in }; coordinator.chooseFiles { _ in }; coordinator.chooseFiles { _ in }
        #expect(made == 1); #expect(picker.shown == 1); #expect(picker.foregrounded == 3)
    }
    @Test func cancellationAllowsANewPickerWithoutSendingFiles() {
        let first = Picker(), second = Picker(); var made = 0, sent = 0
        let coordinator = FilePickerCoordinator { made += 1; return made == 1 ? first : second }
        coordinator.chooseFiles { _ in sent += 1 }; first.completion?(false)
        coordinator.chooseFiles { _ in sent += 1 }
        #expect(made == 2); #expect(second.shown == 1); #expect(sent == 0)
        first.completion?(true) // A stale first dialog must not close or submit the second.
        coordinator.chooseFiles { _ in sent += 1 }; #expect(made == 2); #expect(sent == 0)
    }
    @Test func acceptedSelectionIsSubmittedOnceAndCanBeReopened() {
        let first = Picker(), second = Picker(); var made = 0, selected: [[URL]] = []
        let coordinator = FilePickerCoordinator { made += 1; return made == 1 ? first : second }
        coordinator.chooseFiles { selected.append($0) }; first.completion?(true); first.completion?(true)
        #expect(selected == [first.urls])
        coordinator.chooseFiles { selected.append($0) }; #expect(made == 2); #expect(second.foregrounded == 1)
    }
}
