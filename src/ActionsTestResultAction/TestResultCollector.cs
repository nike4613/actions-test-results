using System.Collections.Immutable;
using System.Xml.Linq;
using Schemas.VisualStudio.TeamTest;

namespace ActionsTestResultAction
{
    internal sealed partial class TestResultCollector
    {
        private readonly Dictionary<Guid, Test> testMap = new();
        private readonly Dictionary<Guid, TestSuiteRun> suiteRuns = new();

        // TOOD: process this data somehow

        private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        // Note: for some reason, attributes don't get the xmlns
        private static readonly XName Id = "id";
        private static readonly XName Name = "name";
        private static readonly XName Storage = "storage";
        private static readonly XName ClassName = "className";
        private static readonly XName Outcome = "outcome";
        private static readonly XName Total = "total";
        private static readonly XName Executed = "executed";
        private static readonly XName Passed = "passed";
        private static readonly XName Failed = "failed";
        private static readonly XName Error = "error";
        private static readonly XName TestId = "testId";
        private static readonly XName TestName = "testName";
        private static readonly XName Duration = "duration";
        private static readonly XName Start = "start";
        private static readonly XName Finish = "finish";

        private static readonly XName Counters = Trx + "Counters";
        private static readonly XName ResultSummary = Trx + "ResultSummary";

        private static readonly XName Times = Trx + "Times";

        private static readonly XName TestDefinitions = Trx + "TestDefinitions";
        private static readonly XName UnitTest = Trx + "UnitTest";
        private static readonly XName UnitTestResult = Trx + "UnitTestResult";
        private static readonly XName TestMethod = Trx + "TestMethod";
        private static readonly XName Exception = Trx + "Exception";
        private static readonly XName TextMessages = Trx + "TextMessages";

        private static readonly XName Results = Trx + "Results";
        private static readonly XName Output = Trx + "Output";
        private static readonly XName StdOut = Trx + "StdOut";
        private static readonly XName StdErr = Trx + "StdErr";
        private static readonly XName ErrorInfo = Trx + "ErrorInfo";
        private static readonly XName Message = Trx + "Message";
        private static readonly XName StackTrace = Trx + "StackTrace";

        public void RecordTrxTests(string trxName, XDocument trxModel)
        {
            var id = Guid.NewGuid();

            var root = trxModel.Root;
            if (root is null) return;

            TimeSpan duration = default;
            if (root.Elements(Times).FirstOrDefault() is { } times
                && (string?)times.Attribute(Start) is { } startTimeStr
                && (string?)times.Attribute(Finish) is { } endTimeStr)
            {
                var startTime = DateTimeOffset.Parse(startTimeStr, null);
                var endTime = DateTimeOffset.Parse(endTimeStr, null);
                duration = endTime - startTime;
            }

            // first, make sure we've created the Test instances for all of the tests defined in this TRX
            foreach (var defSet in root.Elements(TestDefinitions))
            {
                foreach (var test in defSet.Elements(UnitTest))
                {
                    if (test.Attribute(Id) is not { } idStr) continue;
                    if (test.Attribute(Name) is not { } name) continue;

                    var testId = (Guid)idStr;

                    if (!testMap.TryGetValue(testId, out _))
                    {
                        string? className = null;
                        string? methodName = null;
                        if (test.Element(TestMethod) is { } method)
                        {
                            className = (string?)method.Attribute(ClassName);
                            methodName = (string?)method.Attribute(Name);
                        }

                        var testObj = new Test(testId, (string)name, className, methodName);
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
            foreach (var resultSet in root.Elements(Results))
            {
                foreach (var testResult in resultSet.Elements(UnitTestResult))
                {
                    if (testResult.Attribute(TestId) is not { } testIdAttr) continue;
                    if (testResult.Attribute(TestName) is not { } testNameAttr) continue;
                    if (testResult.Attribute(Outcome) is not { } outcomeAttr) continue;
                    var testId = (Guid)testIdAttr;

                    var runDuration = testResult.Attribute(Duration) is { } durAttr
                        ? TimeSpan.Parse((string)durAttr) : default;

                    string? exceptionMessage = null;
                    string? exceptionStack = null;
                    string? stdout = null;
                    string? stderr = null;
                    var extraMessages = ImmutableArray<string>.Empty;

                    if (testResult.Element(Output) is { } output)
                    {
                        stdout = (string?)output.Element(StdOut);
                        stderr = (string?)output.Element(StdErr);
                        if (output.Element(ErrorInfo) is { } errorInfo)
                        {
                            exceptionMessage = (string?)errorInfo.Element(Message);
                            exceptionStack = (string?)errorInfo.Element(StackTrace);
                        }
                        else if (output.Element(Exception) is { } exception)
                        {
                            exceptionMessage = (string?)exception;
                        }

                        extraMessages = output
                            .Elements(TextMessages)
                            .Select(e => (string?)e)
                            .Where(s => s is not null)
                            .ToImmutableArray()!;
                    }

                    // get the test reference
                    if (!testMap.TryGetValue(testId, out var test))
                    {
                        // this shouldn't happen, but lets not die on it
                        test = new(testId, (string?)testNameAttr ?? "", null, null);
                        testMap.Add(testId, test);
                    }

                    // create the run object
                    var outcome = Enum.Parse<TestOutcome>((string?)outcomeAttr ?? nameof(TestOutcome.Inconclusive));
                    var run = new TestRun(test, id, (string?)testNameAttr ?? "", runDuration, outcome)
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

                        case TestOutcome.NotRunnable:
                        case TestOutcome.NotExecuted:
                            executed--; // wasn't actually executed
                            goto default;

                        default:
                        case TestOutcome.Timeout:
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
            if (root.Element(ResultSummary) is { } summary)
            {
                overallOutcome = Enum.Parse<TestOutcome>((string?)summary.Attribute(Outcome) ?? nameof(TestOutcome.Inconclusive));

                if (summary.Element(Counters) is { } counter)
                {
                    if (counter.Attribute(Total) is { } ct) total = (int)ct;
                    if (counter.Attribute(Executed) is { } xt) executed = (int)xt;
                    if (counter.Attribute(Passed) is { } pt) passed = (int)pt;
                    if (counter.Attribute(Failed) is { } ft) failed = (int)ft;
                    if (counter.Attribute(Error) is { } et) errored = (int)et;
                }
            }

            // we've now processed the tests, we just need to create the suite run instance
            var suiteRun = new TestSuiteRun(duration, id, trxName, builder.DrainToImmutable(), overallOutcome)
            {
                Total = total,
                Executed = executed,
                Passed = passed,
                Failed = failed,
                Errored = errored,
            };

            suiteRuns.Add(id, suiteRun);
        }

        public TestResultCollection Collect(bool showDifferentFailingRuns = true, bool differenceIncludeStack = false, int maxRunsToShow = 5)
        {
            var totalTests = 0;
            var totalExecuted = 0;
            var totalPassed = 0;
            var totalFailed = 0;
            var totalErrored = 0;
            var aggTests = 0;
            var aggExecuted = 0;
            var aggPassed = 0;
            var aggFailed = 0;
            var aggErrored = 0;

            // go through all suites and aggregate them
            var testSuitesBuilder = ImmutableArray.CreateBuilder<TestSuiteRun>();
            foreach (var suite in suiteRuns.Values)
            {
                testSuitesBuilder.Add(suite);

                totalTests += suite.Total;
                totalExecuted += suite.Executed;
                totalPassed += suite.Passed;
                totalFailed += suite.Failed;
                totalErrored += suite.Errored;
            }

            var overallOutcome = TestOutcome.Passed;

            // now go through all tests, and work out which ones should be shown
            var testsBuilder = ImmutableArray.CreateBuilder<Test>();
            var showTestsBuilder = ImmutableArray.CreateBuilder<ShownTest>();
            foreach (var test in testMap.Values)
            {
                testsBuilder.Add(test);

                var showReason = ShowReason.None;
                var anyPassed = false;
                var anyExecuted = false;
                var showRunsBuilder = ImmutableArray.CreateBuilder<(TestRun Test, HashSet<string> ExtraSources)>();
                foreach (var run in test.Runs)
                {
                    switch (run.Outcome)
                    {
                        case TestOutcome.Passed:
                            anyPassed = true;
                            anyExecuted = true;
                            continue;
                        case TestOutcome.Failed:
                            anyExecuted = true;
                            if (showReason is not ShowReason.Errored)
                            {
                                showReason = ShowReason.FailingSometimes;
                            }
                            showRunsBuilder.Add((run, new()));
                            continue;
                        case TestOutcome.Error:
                            anyExecuted = true;
                            showReason = ShowReason.Errored;
                            // insert erroring tests AT the front to make sure we show them preferentially
                            showRunsBuilder.Insert(0, (run, new()));
                            continue;
                        default: continue;
                    }
                }

                aggTests++;
                if (anyExecuted)
                {
                    aggExecuted++;
                }
                switch (showReason)
                {
                    case ShowReason.None:
                        aggPassed++;
                        // no notable runs; nothing to show
                        continue;
                    default:
                    case ShowReason.FailingSometimes:
                    case ShowReason.FailingAlways:
                        if (overallOutcome is not TestOutcome.Error)
                        {
                            overallOutcome = TestOutcome.Failed;
                        }
                        aggFailed++;
                        break;
                    case ShowReason.Errored:
                        overallOutcome = TestOutcome.Error;
                        aggErrored++;
                        break;
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
                    var set = new Dictionary<(string? Message, string? Stack), int>();
                    for (var i = 0; i < showRunsBuilder.Count; i++)
                    {
                        var (run, extraList) = showRunsBuilder[i];

                        // filter according to the set
                        var t = (run.ExceptionMessage, differenceIncludeStack ? run.ExceptionStackTrace : null);
                        if (set.TryGetValue(t, out var index))
                        {
                            // this message was already added, remove it
                            showRunsBuilder.RemoveAt(i--);

                            var targetExtraSet = showRunsBuilder[index].ExtraSources;
                            foreach (var extra in extraList)
                            {
                                _ = targetExtraSet.Add(extra);
                            }
                        }
                        else
                        {
                            set.Add(t, i);
                        }
                    }
                }
                else
                {
                    // filter all but 1 run of each outcome kind
                    var set = new Dictionary<TestOutcome, int>();
                    for (var i = 0; i < showRunsBuilder.Count; i++)
                    {
                        var (run, extraList) = showRunsBuilder[i];

                        // filter according to the set
                        if (set.TryGetValue(run.Outcome, out var index))
                        {
                            // this message was already added, remove it
                            showRunsBuilder.RemoveAt(i--);

                            var targetExtraSet = showRunsBuilder[index].ExtraSources;
                            foreach (var extra in extraList)
                            {
                                _ = targetExtraSet.Add(extra);
                            }
                        }
                        else
                        {
                            set.Add(run.Outcome, i);
                        }
                    }
                }

                // filter to the max runs to show
                for (var i = maxRunsToShow; i < showRunsBuilder.Count; i++)
                {
                    showRunsBuilder.RemoveAt(i--);
                    hasHiddenNotableRuns = true;
                }

                var showRunsBuilder2 = ImmutableArray.CreateBuilder<(TestRun Run, string Suite, ImmutableArray<string> Sources)>();
                foreach (var run in showRunsBuilder)
                {
                    showRunsBuilder2.Add((run.Test, suiteRuns[run.Test.TestSuite].Name, run.ExtraSources.ToImmutableArray()));
                }

                showRunsBuilder2.Sort((a, b) => a.Suite.CompareTo(b.Suite));

                // record it
                showTestsBuilder.Add(new(showReason, test, showRunsBuilder2.DrainToImmutable(), hasHiddenNotableRuns));
            }

            showTestsBuilder.Sort((a, b) => a.Test.Name.CompareTo(b.Test.Name));

            // create the resulting collection
            var aggregateSuite = new TestSuiteRun(default, default, "", ImmutableArray<TestRun>.Empty, overallOutcome)
            {
                Total = aggTests,
                Executed = aggExecuted,
                Passed = aggPassed,
                Failed = aggFailed,
                Errored = aggErrored,
            };

            return new(testsBuilder.DrainToImmutable(), testSuitesBuilder.DrainToImmutable(), aggregateSuite)
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
        public string Name { get; }

        public ImmutableArray<TestRun> TestRuns { get; }
        public TestOutcome Outcome { get; }

        public int Total { get; init; }
        public int Executed { get; init; }
        public int Skipped => Total - Executed;
        public int Passed { get; init; }
        public int Failed { get; init; }
        public int Errored { get; init; }
        public int Other => Executed - Passed - Failed - Errored;

        public TestSuiteRun(TimeSpan duration, Guid id, string name, ImmutableArray<TestRun> testRuns, TestOutcome outcome)
        {
            Duration = duration;
            Id = id;
            Name = name;
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
