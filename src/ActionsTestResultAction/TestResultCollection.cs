using System.Collections.Immutable;

namespace ActionsTestResultAction
{
    internal readonly record struct ShownTest(ShowReason Reason, Test Test, ImmutableArray<TestRun> Runs, bool HasHiddenNotableRuns);

    internal sealed class TestResultCollection
    {
        public ImmutableArray<Test> Tests { get; }
        public ImmutableArray<TestSuiteRun> TestSuiteRuns { get; }

        public int Total { get; init; }
        public int Executed { get; init; }
        public int Skipped => Total - Executed;
        public int Passed { get; init; }
        public int Failed { get; init; }
        public int Errored { get; init; }
        public int Other => Executed - Passed - Failed - Errored;

        public ImmutableArray<ShownTest> ShowTests { get; init; } = ImmutableArray<ShownTest>.Empty;

        public TestResultCollection(ImmutableArray<Test> tests, ImmutableArray<TestSuiteRun> testSuiteRuns)
        {
            Tests = tests;
            TestSuiteRuns = testSuiteRuns;
        }
    }
}
