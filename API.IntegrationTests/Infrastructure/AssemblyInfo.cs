using Xunit;

// -------------------------------------------------------------------------------------------------
// Assembly-wide xUnit test-parallelization policy (CR-01 support).
//
// After CR-01, each integration test class consumes ContainerFixture as a PER-CLASS
// IClassFixture<ContainerFixture> (rather than a single shared ICollectionFixture). That makes every
// class its own implicit xUnit test collection, and by default xUnit runs distinct collections IN
// PARALLEL. With per-class fixtures that would start many isolated PostgreSQL + Redis container pairs
// simultaneously and exhaust host resources (CPU/RAM/Docker daemon connections), reintroducing the
// very flakiness the isolation change is meant to remove.
//
// Disabling parallelization makes the classes run SEQUENTIALLY (as the previous shared-collection
// design effectively did), so at most one ordinary class's PostgreSQL + Redis pair — plus, briefly, a
// Resilience/FailClosedTests pair — is alive at any moment, while every class still receives its OWN
// disposable containers (AAP §0.10.1; binding Rule 1). Isolation and determinism are preserved; only
// wall-clock time increases.
// -------------------------------------------------------------------------------------------------
[assembly: CollectionBehavior(DisableTestParallelization = true)]
