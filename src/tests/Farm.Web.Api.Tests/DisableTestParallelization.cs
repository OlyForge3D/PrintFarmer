using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

// NOTE: This file temporarily disables xUnit parallelization to stabilize CI while
// we implement a robust parallel-safe metrics reset. After the next-phase fix
// (host-tokened metrics reset) is validated, this file should be removed.

