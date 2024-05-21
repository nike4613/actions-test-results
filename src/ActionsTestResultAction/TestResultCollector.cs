using System.Collections.Immutable;
using System.Xml;
using Schemas.VisualStudio.TeamTest;

namespace ActionsTestResultAction
{
    internal sealed partial class TestResultCollector
    {
        private readonly Dictionary<Guid, Test> testMap = new();
        private readonly List<TestSuiteRun> suiteRuns = new();

        // TOOD: process this data somehow

        public void RecordTrxTests(TestRunType trxModel)
        {
            var id = Guid.Parse(trxModel.Id);

            TimeSpan duration = default;
            if (trxModel.Times is [{ } times])
            {
                var startTime = DateTimeOffset.Parse(times.Start!, null);
                var endTime = DateTimeOffset.Parse(times.Finish!, null);
                duration = endTime - startTime;
            }

            // first, make sure we've created the Test instances for all of the tests defined in this TRX
            foreach (var defSet in trxModel.TestDefinitions)
            {
                foreach (var test in defSet.UnitTest)
                {
                    var testId = Guid.Parse(test.Id);
                    if (!testMap.TryGetValue(testId, out _))
                    {
                        string? className = null;
                        string? methodName = null;
                        if (test.TestMethod is { } method)
                        {
                            className = method.ClassName;
                            methodName = method.Name;
                        }

                        var testObj = new Test(testId, test.Name, className, methodName);
                        testMap.Add(testId, testObj);
                    }
                }
            }

            var overallOutcome = TestOutcome.Passed;
            var total = 0;
            var executed = 0;
            var passed = 0;
            var failed = 0;
            var errored = 0;

            // now we can go through the actual results and process them
            var builder = ImmutableArray.CreateBuilder<TestRun>();
            foreach (var resultSet in trxModel.Results)
            {
                foreach (var testResult in resultSet.UnitTestResult)
                {
                    var testId = Guid.Parse(testResult.TestId);
                    var runDuration = testResult.Duration is { } dur ? TimeSpan.Parse(dur) : default;

                    string? exceptionMessage = null;
                    string? exceptionStack = null;
                    string? stdout = null;
                    string? stderr = null;
                    var extraMessages = ImmutableArray<string>.Empty;

                    if (testResult.Output is [{ } output, ..])
                    {
                        stdout = GetStringForObject(output.StdOut);
                        stderr = GetStringForObject(output.StdErr);
                        if (output.ErrorInfo is { } errorInfo)
                        {
                            exceptionMessage = GetStringForObject(errorInfo.Message);
                            exceptionStack = GetStringForObject(errorInfo.StackTrace);
                        }
                        else if (output.Exception is { } exception)
                        {
                            exceptionMessage = GetStringForObject(exception);
                        }

                        extraMessages = output.TextMessages
                            .Select(GetStringForObject)
                            .Where(s => s is not null)
                            .ToImmutableArray()!;
                    }

                    // get the test reference
                    if (!testMap.TryGetValue(testId, out var test))
                    {
                        // this shouldn't happen, but lets not die on it
                        test = new(testId, testResult.TestName, null, null);
                        testMap.Add(testId, test);
                    }

                    // create the run object
                    var outcome = Enum.Parse<TestOutcome>(testResult.Outcome ?? nameof(TestOutcome.Inconclusive));
                    var run = new TestRun(test, id, testResult.TestName, runDuration, outcome)
                    {
                        StdOut = stdout,
                        StdErr = stderr,
                        ExceptionMessage = exceptionMessage,
                        ExceptionStackTrace = exceptionStack,
                        ExtraMessages = extraMessages
                    };

                    // add the run to the relevant lists
                    test.Runs.Add(run);
                    builder.Add(run);

                    // update the overall outcome
                    total++;
                    executed++;
                    switch (outcome)
                    {
                        case TestOutcome.Passed:
                        case TestOutcome.PassedButRunAborted:
                            passed++;
                            break;
                        case TestOutcome.Failed:
                            failed++;
                            if (overallOutcome is not TestOutcome.Error and not TestOutcome.Aborted)
                            {
                                overallOutcome = TestOutcome.Failed;
                            }
                            break;
                        case TestOutcome.Error:
                            errored++;
                            if (overallOutcome is not TestOutcome.Aborted)
                            {
                                overallOutcome = TestOutcome.Error;
                            }
                            break;

                        case TestOutcome.Aborted:
                            overallOutcome = TestOutcome.Aborted;
                            break;

                        default:
                        case TestOutcome.Timeout:
                        case TestOutcome.NotRunnable:
                        case TestOutcome.NotExecuted:
                        case TestOutcome.Disconnected:
                        case TestOutcome.Warning:
                        case TestOutcome.Pending:
                        case TestOutcome.InProgress:
                        case TestOutcome.Completed:
                        case TestOutcome.Inconclusive:
                            if (overallOutcome is not TestOutcome.Aborted and not TestOutcome.Error and not TestOutcome.Failed)
                            {
                                overallOutcome = TestOutcome.Inconclusive;
                            }
                            break;
                    }
                }
            }

            // try to get summary information
            if (trxModel.ResultSummary is [{ } summary, ..])
            {
                overallOutcome = Enum.Parse<TestOutcome>(summary.Outcome);

                if (summary.Counters is [{ } counter, ..])
                {
                    if (counter.Total is { } ct) total = ct;
                    if (counter.Executed is { } xt) executed = xt;
                    if (counter.Passed is { } pt) passed = pt;
                    if (counter.Failed is { } ft) failed = ft;
                    if (counter.Error is { } et) errored = et;
                }
            }

            // we've now processed the tests, we just need to create the suite run instance
            var suiteRun = new TestSuiteRun(duration, id, builder.DrainToImmutable(), overallOutcome)
            {
                Total = total,
                Executed = executed,
                Passed = passed,
                Failed = failed,
                Errored = errored,
            };

            suiteRuns.Add(suiteRun);
        }

        private static string? GetStringForObject(object? obj)
        {
            switch (obj)
            {
                case null:
                    return null;

                case string s:
                    return s;

                case XmlNode node:
                    return node.InnerText;

                default:
                    return obj.ToString();
            }
        }

        public TestResultCollection Collect(bool showDifferentFailingRuns = false, int maxRunsToShow = 2)
        {
            var totalTests = 0;
            var totalExecuted = 0;
            var totalPassed = 0;
            var totalFailed = 0;
            var totalErrored = 0;

            // go through all suites and aggregate them
            var testSuitesBuilder = ImmutableArray.CreateBuilder<TestSuiteRun>();
            foreach (var suite in suiteRuns)
            {
                testSuitesBuilder.Add(suite);

                totalTests += suite.Total;
                totalExecuted += suite.Executed;
                totalPassed += suite.Passed;
                totalFailed += suite.Failed;
                totalErrored += suite.Errored;
            }

            // now go through all tests, and work out which ones should be shown
            var testsBuilder = ImmutableArray.CreateBuilder<Test>();
            var showTestsBuilder = ImmutableArray.CreateBuilder<ShownTest>();
            foreach (var test in testMap.Values)
            {
                testsBuilder.Add(test);

                var showReason = ShowReason.None;
                var anyPassed = false;
                var showRunsBuilder = ImmutableArray.CreateBuilder<TestRun>();
                foreach (var run in test.Runs)
                {
                    switch (run.Outcome)
                    {
                        case TestOutcome.Passed:
                            anyPassed = true;
                            continue;
                        case TestOutcome.Failed:
                            if (showReason is not ShowReason.Errored)
                            {
                                showReason = ShowReason.FailingSometimes;
                            }
                            showRunsBuilder.Add(run);
                            continue;
                        case TestOutcome.Error:
                            showReason = ShowReason.Errored;
                            // insert erroring tests AT the front to make sure we show them preferentially
                            showRunsBuilder.Insert(0, run);
                            continue;
                        default: continue;
                    }
                }

                if (showReason == ShowReason.None)
                {
                    // no notable runs; skip
                    continue;
                }

                if (!anyPassed && showReason is ShowReason.FailingSometimes)
                {
                    showReason = ShowReason.FailingAlways;
                }

                var hasHiddenNotableRuns = false;
                // now we want to filter shown tests according to parameters
                if (showDifferentFailingRuns)
                {
                    // filter for unique failure messages
                    var set = new HashSet<(string? Message, string? Stack)>();
                    for (var i = 0; i < showRunsBuilder.Count; i++)
                    {
                        var run = showRunsBuilder[i];
                        // filter according to the set
                        if (!set.Add((run.ExceptionMessage, run.ExceptionStackTrace)))
                        {
                            // this message was already added, remove it
                            showRunsBuilder.RemoveAt(i--);
                            hasHiddenNotableRuns = true;
                        }
                    }
                }
                else
                {
                    // filter all but 1 run of each outcome kind
                    var set = new HashSet<TestOutcome>();
                    for (var i = 0; i < showRunsBuilder.Count; i++)
                    {
                        var run = showRunsBuilder[i];
                        // filter according to the set
                        if (!set.Add(run.Outcome))
                        {
                            // this outcome was already found, remove this instance
                            showRunsBuilder.RemoveAt(i--);
                            hasHiddenNotableRuns = true;
                        }
                    }
                }

                // filter to the max runs to show
                for (var i = maxRunsToShow; i < showRunsBuilder.Count; i++)
                {
                    showRunsBuilder.RemoveAt(i--);
                    hasHiddenNotableRuns = true;
                }

                // record it
                showTestsBuilder.Add(new(showReason, test, showRunsBuilder.DrainToImmutable(), hasHiddenNotableRuns));
            }

            // create the resulting collection
            return new(testsBuilder.DrainToImmutable(), testSuitesBuilder.DrainToImmutable())
            {
                Total = totalTests,
                Executed = totalExecuted,
                Passed = totalPassed,
                Failed = totalFailed,
                Errored = totalErrored,

                ShowTests = showTestsBuilder.DrainToImmutable(),
            };
        }
    }

    internal enum ShowReason
    {
        None = 0,
        FailingAlways,
        FailingSometimes,
        Errored,
    }

    internal sealed class TestSuiteRun
    {
        public TimeSpan Duration { get; }
        public Guid Id { get; }

        public ImmutableArray<TestRun> TestRuns { get; }
        public TestOutcome Outcome { get; }

        public int Total { get; init; }
        public int Executed { get; init; }
        public int Skipped => Total - Executed;
        public int Passed { get; init; }
        public int Failed { get; init; }
        public int Errored { get; init; }
        public int Other => Executed - Passed - Failed - Errored;

        public TestSuiteRun(TimeSpan duration, Guid id, ImmutableArray<TestRun> testRuns, TestOutcome outcome)
        {
            Duration = duration;
            Id = id;
            TestRuns = testRuns;
            Outcome = outcome;
        }
    }

    internal sealed class Test
    {
        // TODO: do we want to try to keep an idea of a "suite"?
        public Guid Id { get; }
        public string Name { get; }
        public string? ClassName { get; }
        public string? MethodName { get; }

        internal readonly List<TestRun> Runs = new();

        public Test(Guid id, string name, string? className, string? methodName)
        {
            Id = id;
            Name = name;
            ClassName = className;
            MethodName = methodName;
        }
    }

    internal sealed class TestRun
    {
        public Test Test { get; }
        public Guid TestSuite { get; }
        public string Name { get; }

        public TimeSpan Duration { get; }
        public TestOutcome Outcome { get; }

        public string? StdOut { get; init; }
        public string? StdErr { get; init; }

        public string? ExceptionMessage { get; init; }
        public string? ExceptionStackTrace { get; init; }

        public ImmutableArray<string> ExtraMessages { get; init; }

        public TestRun(Test test, Guid testSuite, string name, TimeSpan duration, TestOutcome outcome)
        {
            Test = test;
            TestSuite = testSuite;
            Name = name;
            Duration = duration;
            Outcome = outcome;
        }
    }
}
