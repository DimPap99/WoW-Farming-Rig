using Xunit.Sdk;
using Xunit.v3;

// ContinentDB is static and filled by every planner and pathing service a test creates;
// its dictionaries are not safe to fill from two threads. Tests that exercise concurrency
// do so inside one test, against one planner.
[assembly: Parallelization(Mode = ParallelMode.None)]
