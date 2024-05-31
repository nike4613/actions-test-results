using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using Schemas.VisualStudio.TeamTest;
using static System.Net.Mime.MediaTypeNames;

namespace ActionsTestResultAction
{
    internal sealed partial class TestResultCollector
    {
        private readonly Dictionary<Guid, Test> testMap = new();
        private readonly Dictionary<Guid, TestSuiteRun> suiteRuns = new();

        #region TRX names
        private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        // Note: for some reason, attributes don't get the xmlns
        private static readonly XName Id = "id";
        private static readonly XName Name = "name";
        private static readonly XName TrxClassName = "className";
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
        private static readonly XName TestRun = Trx + "TestRun";
        private static readonly XName UnitTest = Trx + "UnitTest";
        private static readonly XName UnitTestResult = Trx + "UnitTestResult";
        private static readonly XName TestMethod = Trx + "TestMethod";
        private static readonly XName Exception = Trx + "Exception";
        private static readonly XName TextMessages = Trx + "TextMessages";

        private static readonly XName TrxResults = Trx + "Results";
        private static readonly XName Output = Trx + "Output";
        private static readonly XName StdOut = Trx + "StdOut";
        private static readonly XName StdErr = Trx + "StdErr";
        private static readonly XName ErrorInfo = Trx + "ErrorInfo";
        private static readonly XName TrxMessage = Trx + "Message";
        private static readonly XName TrxStackTrace = Trx + "StackTrace";
        #endregion

        #region JUnit names
        // Elements
        private static readonly XName TestSuites = "testsuites";
        private static readonly XName TestSuite = "testsuite";
        private static readonly XName TestCase = "testcase";
        private static readonly XName SystemOut = "system-out";
        private static readonly XName SystemErr = "system-err";
        private static readonly XName Failure = "failure";
        private static readonly XName Properties = "properties";
        private static readonly XName Property = "property";

        // Attributes
        private static readonly XName Tests = "tests";
        private static readonly XName Failures = "failures";
        private static readonly XName Skipped = "skipped"; // also an element
        private static readonly XName Errors = "errors";
        private static readonly XName Time = "time";
        private static readonly XName Value = "value";
        private static readonly XName JUnitMessage = "message";
        private static readonly XName JUnitClassName = "classname";
        #endregion

        #region NUnit names
        // NOTE: We limit our processing here to what xunit.console emits for nunit test results

        // Elements
        private static readonly XName NUnitTestResults = "test-results";
        private static readonly XName NUnitTestSuite = "test-suite";
        private static readonly XName NUnitResults = "results";
        private static readonly XName NUnitTestCase = "test-case";
        private static readonly XName NUnitMessage = JUnitMessage;
        private static readonly XName NUnitStackTrace = "stack-trace";
        private static readonly XName NUnitReason = "reason";

        // Attributes
        private static readonly XName Result = "result";

        // TODO: implement processing of NUnit
        #endregion

        public void RecordXmlTests(string xmlName, XDocument xdoc)
        {
            if (xdoc.Root is null) return; // empty file

            // lets inspect it to try to guess what format it is

            // first, lets try to find the default xmlns
            var defaultNs = xdoc.Root.GetDefaultNamespace();
            if (defaultNs == Trx)
            {
                RecordTrxTests(xmlName, xdoc);
                return;
            }

            // next, lets look at the name of the root node
            if (xdoc.Root.Name == TestRun)
            {
                // still looks like a TRX
                RecordTrxTests(xmlName, xdoc);
                return;
            }

            if (xdoc.Root.Name == TestSuites || xdoc.Root.Name == TestSuite)
            {
                // looks like a JUnit XML
                RecordJUnitTests(xmlName, xdoc);
                return;
            }
        }

        public void RecordTrxTests(string trxName, XDocument trxModel)
        {
            // Trx root is TestRun, with xmlns = Trx

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
                    if (test.Attribute(Name) is not { Value: { } name }) continue;

                    var testId = (Guid)idStr;

                    if (!testMap.TryGetValue(testId, out _))
                    {
                        string? className = null;
                        string? methodName = null;
                        if (test.Element(TestMethod) is { } method)
                        {
                            className = (string?)method.Attribute(TrxClassName);
                            methodName = (string?)method.Attribute(Name);
                        }

                        var collateId = GuidOfString(name);
                        var testObj = new Test(collateId, name, className, methodName);
                        testMap.Add(testId, testObj);
                        testMap.Add(collateId, testObj);
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
            foreach (var resultSet in root.Elements(TrxResults))
            {
                foreach (var testResult in resultSet.Elements(UnitTestResult))
                {
                    if (testResult.Attribute(TestId) is not { } testIdAttr) continue;
                    if (testResult.Attribute(TestName) is not { Value: { } testName }) continue;
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
                            exceptionMessage = (string?)errorInfo.Element(TrxMessage);
                            exceptionStack = (string?)errorInfo.Element(TrxStackTrace);
                        }
                        else if (output.Element(Exception) is { } exception)
                        {
                            exceptionMessage = (string?)exception;
                        }

                        extraMessages = output
                            .Elements(TextMessages)
                            .Select(e => ((string?)e)?.ReplaceLineEndings())
                            .Where(s => s is not null)
                            .ToImmutableArray()!;
                    }

                    var testCollateId = GuidOfString(testName);

                    // get the test reference
                    if (!testMap.TryGetValue(testId, out var test) && !testMap.TryGetValue(testCollateId, out test))
                    {
                        // this shouldn't happen, but lets not die on it
                        test = new(testCollateId, testName, null, null);
                        testMap.Add(testCollateId, test);
                        testMap.Add(testId, test);
                    }

                    // create the run object
                    var outcome = Enum.Parse<TestOutcome>((string?)outcomeAttr ?? nameof(TestOutcome.Inconclusive));
                    var run = new TestRun(test, id, testName, runDuration, outcome)
                    {
                        StdOut = stdout?.ReplaceLineEndings(),
                        StdErr = stderr?.ReplaceLineEndings(),
                        ExceptionMessage = exceptionMessage?.ReplaceLineEndings(),
                        ExceptionStackTrace = exceptionStack?.ReplaceLineEndings(),
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

        private unsafe static Guid GuidOfString(string str)
        {
            var numGuids = SHA256.HashSizeInBytes / sizeof(Guid);

            Span<Guid> guids = stackalloc Guid[numGuids];
            var guidBytes = MemoryMarshal.Cast<Guid, byte>(guids);

            var result = SHA256.TryHashData(MemoryMarshal.Cast<char, byte>(str), guidBytes, out _);
            Debug.Assert(result);

            return guids[str.Length & 1];
        }

        public void RecordJUnitTests(string xmlName, XDocument model)
        {
            if (model.Root is null) return;

            var id = Guid.NewGuid();

            // note: our primary goal here is processing JUnit files generated by xunit.console

            var root = model.Root;
            var rootProps = root.Element(Properties);

            var overallName = xmlName;
            var totalTime = double.NaN;
            var defTotal = 0;
            var defFailures = 0;
            var defErrors = 0;
            var defSkipped = 0;
            TestOutcome? outcome = null;

            if (root.Attribute(Name) is { } nameAttr)
            {
                overallName = nameAttr.Value;
            }

            // figure out where the containing suites are
            IEnumerable<XElement> suiteNodes;
            if (root.Name == TestSuites)
            {
                suiteNodes = root.Elements(TestSuite);
            }
            else
            {
                suiteNodes = [root];
            }

            // now iterate over the suites, but more importantly, the tests within those suites
            var builder = ImmutableArray.CreateBuilder<TestRun>();
            foreach (var suiteNode in suiteNodes)
            {
                // a test suite consists of a list of test cases
                foreach (var testCase in suiteNode.Elements(TestCase))
                {
                    // each test case has shockingly little information we care about
                    if (testCase.Attribute(Name) is not { Value: var testName }) continue;
                    var testId = GuidOfString(testName);

                    var classname = (string?)testCase.Attribute(JUnitClassName);

                    defTotal++;

                    // get this run's data
                    var testTime = double.NaN;
                    var testOutcome = TestOutcome.Passed;

                    if (testCase.Attribute(Time) is { } timeElem)
                    {
                        testTime = (double)timeElem;
                    }

                    string? stdout = null;
                    string? stderr = null;

                    string? message = null;
                    string? stackTrace = null;

                    if (testCase.Element(SystemOut) is { } systemOutElem)
                    {
                        stdout = systemOutElem.Value;
                    }
                    if (testCase.Element(SystemErr) is { } systemErrElem)
                    {
                        stderr = systemErrElem.Value;
                    }

                    if (testCase.Element(Failure) is { } failureElem)
                    {
                        testOutcome = TestOutcome.Failed;
                        defFailures++;

                        message = (string?)failureElem.Attribute(JUnitMessage);
                        stackTrace = failureElem.Value;
                    }
                    else if (testCase.Element(Error) is { } errorElem)
                    {
                        testOutcome = TestOutcome.Error;
                        defErrors++;

                        message = (string?)errorElem.Attribute(JUnitMessage);
                        stackTrace = errorElem.Value;
                    }

                    // get the test reference
                    if (!testMap.TryGetValue(testId, out var test))
                    {
                        test = new(testId, testName, classname, null);
                        testMap.Add(testId, test);
                    }

                    var run = new TestRun(test, id, testName, TimeSpan.FromSeconds(testTime), testOutcome)
                    {
                        StdOut = stdout?.ReplaceLineEndings(),
                        StdErr = stderr?.ReplaceLineEndings(),
                        ExceptionMessage = message?.ReplaceLineEndings(),
                        ExceptionStackTrace = stackTrace?.ReplaceLineEndings(),
                    };

                    test.Runs.Add(run);
                    builder.Add(run);
                }
            }

            // extract top-level information
            if (root.Attribute(Time) is { } timeAttr)
            {
                totalTime = (double)timeAttr;
            }

            if (root.Attribute(Tests) is { } testsAttr)
            {
                defTotal = (int)testsAttr;
            }

            if (root.Attribute(Failures) is { } failuresAttr)
            {
                defFailures = (int)failuresAttr;
            }

            if (root.Attribute(Errors) is { } errorsAttr)
            {
                defErrors = (int)errorsAttr;
            }

            if (root.Attribute(Skipped) is { } skippedAttr)
            {
                defSkipped = (int)skippedAttr;
            }
            else if (rootProps is not null)
            {
                // no skipped attribute, try to get properties value that xunit generates
                skippedAttr = rootProps.Elements(Property).FirstOrDefault(p => p.Attribute(Name) is { Value: "skipped" })?.Attribute(Value);
                if (skippedAttr is not null)
                {
                    defSkipped = (int)skippedAttr;
                }
            }

            if (rootProps is not null)
            {
                var resultAttr = rootProps.Elements(Property).FirstOrDefault(p => p.Attribute(Name) is { Value: "result" })?.Attribute(Value);
                if (resultAttr is not null)
                {
                    outcome = resultAttr.Value switch
                    {
                        "Success" => TestOutcome.Passed,
                        "Failure" => TestOutcome.Failed,
                        _ => null
                    };
                }
            }

            outcome ??= defErrors != 0 ? TestOutcome.Error : defFailures != 0 ? TestOutcome.Failed : TestOutcome.Passed;

            var suiteRun = new TestSuiteRun(TimeSpan.FromSeconds(totalTime), id, xmlName, builder.DrainToImmutable(), outcome.GetValueOrDefault())
            {
                Total = defTotal,
                Executed = defTotal - defSkipped,
                Passed = defTotal - defSkipped - defFailures - defErrors,
                Failed = defFailures,
                Errored = defErrors,
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

        public ImmutableArray<string> ExtraMessages { get; init; } = [];

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
