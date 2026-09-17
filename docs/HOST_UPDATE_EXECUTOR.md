# Host update executor foundation

The safe-executor core supplies immutable release identity contracts, exact six-target validation, a bounded process/installation lock, a durable hash-chained journal, and a checkpoint-aware state machine. It is a foundation only; it does not enable automatic updates or claim deployment completion.

Concrete adapters still required are admission/drain, all-writer fencing, coordinated database/blob/config backup, provider migration ownership, pinned-image application, health/readiness verification, and verified rollback/restore. These adapters must persist evidence before and after side effects and keep uncertain outcomes in `RecoveryRequired`.
