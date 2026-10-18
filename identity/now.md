# Current Focus: Printer Sensors & Klipper Emulator Fidelity

**Status:** The six original children of epic #3405 are complete. The follow-up to extend the Moonraker emulator with standard Klipper sensor objects is unblocked and is next. Dallas is also handling read-only research for #3426; the host lacks Xcode, so simulator reproduction remains deferred to an Xcode host.

## In Progress

- #3434 — Use virtual-klipper-printer as a reference to extend the existing Moonraker emulator. Keep the fast deterministic emulator, its clock, fault injection, and multi-backend use; do not make Docker a runtime/fast-CI dependency.
- #3426 — Dallas to research the iPad FilamentCoverage CI disconnect from existing CI evidence and code, without attempting Xcode reproduction.

## Recently Completed

- #3406–#3411 — Original printer sensors/fans epic implementation is complete.
- #3418 — React sensor type follow-up was covered by merged PR #3416 and closed as complete.