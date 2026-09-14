using Xunit;

// Integration fixtures share one LocalDB instance for database creation, deletion,
// and recovery. Keep fixtures serial; tests still exercise concurrent requests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
