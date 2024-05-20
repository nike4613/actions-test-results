using System.Collections.Immutable;
using System.Xml;
using Schemas.VisualStudio.TeamTest;

namespace ActionsTestResultAction
{
    internal sealed class TestResultCollector
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
                    var run = new TestRun(test, testResult.TestName, runDuration, outcome)
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
        public string Name { get; }

        public TimeSpan Duration { get; }
        public TestOutcome Outcome { get; }

        public string? StdOut { get; init; }
        public string? StdErr { get; init; }

        public string? ExceptionMessage { get; init; }
        public string? ExceptionStackTrace { get; init; }

        public ImmutableArray<string> ExtraMessages { get; init; }

        public TestRun(Test test, string name, TimeSpan duration, TestOutcome outcome)
        {
            Test = test;
            Name = name;
            Duration = duration;
            Outcome = outcome;
        }
    }
}
