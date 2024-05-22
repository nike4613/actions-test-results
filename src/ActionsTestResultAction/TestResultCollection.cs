using System.Collections.Immutable;
using System.Text;
using Schemas.VisualStudio.TeamTest;

namespace ActionsTestResultAction
{
    internal readonly record struct ShownTest(
        ShowReason Reason,
        Test Test,
        ImmutableArray<(TestRun Run, string RunSuite, ImmutableArray<string> Suites)> Runs,
        bool HasHiddenNotableRuns);

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

        public TestSuiteRun AggregateRun { get; }

        public ImmutableArray<ShownTest> ShowTests { get; init; } = ImmutableArray<ShownTest>.Empty;

        public TestResultCollection(ImmutableArray<Test> tests, ImmutableArray<TestSuiteRun> testSuiteRuns, TestSuiteRun aggregateRun)
        {
            Tests = tests;
            TestSuiteRuns = testSuiteRuns;
            AggregateRun = aggregateRun;
        }

        public string Format(string? title, TestResultFormatMode mode)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(title))
            {
                _ = sb.AppendLine($"# {title}").AppendLine();
            }

            // first, lets emit a table containing totals
            var hasErrors = Errored > 0 || AggregateRun.Errored > 0;
            var errorTableHdrExt1 = hasErrors ? " ---: |" : "";
            var errorTableHdrExt2 = hasErrors ? " Errored |" : "";
            var errorTableUniqExt = hasErrors ? $"{AggregateRun.Errored} |" : "";
            var errorTableTotalExt = hasErrors ? $"{Errored} |" : "";

            _ = sb.AppendLine($"""

            | | Total | Skipped | Passed | Failed |{errorTableHdrExt2}
            | ---: | ---: | ---: | ---: | ---: |{errorTableHdrExt1}
            | Unique | {AggregateRun.Total} | {AggregateRun.Skipped} | {AggregateRun.Passed} | {AggregateRun.Failed} |{errorTableUniqExt}
            | Total | {Total} | {Skipped} | {Passed} | {Failed} |{errorTableTotalExt}

            """);

            if (mode is not TestResultFormatMode.Summary)
            {
                _ = sb.AppendLine($"### Failing{(hasErrors ? " or Erroring" : "")} runs").AppendLine();

                foreach (var showTest in ShowTests)
                {
                    _ = sb.AppendLine($"""
                        <details>
                        <summary>❌ {showTest.Test.Name}</summary>

                        """);

                    _ = sb.AppendLine();

                    if (showTest.Test.ClassName is not null)
                    {
                        _ = sb.Append($"Class Name: `{showTest.Test.ClassName}` | ");
                    }
                    if (showTest.Test.MethodName is not null)
                    {
                        _ = sb.Append($"Method Name: `{showTest.Test.MethodName}` | ");
                    }

                    var showReason = showTest.Reason switch
                    {
                        ShowReason.None => "???",
                        ShowReason.FailingAlways => "is always failing",
                        ShowReason.FailingSometimes => "is sometimes failing",
                        ShowReason.Errored => "errored in at least one run",
                        var x => x.ToString(),
                    };

                    _ = sb
                        .AppendLine($"*This test {showReason}.*")
                        .AppendLine();


                    foreach (var (run, mainSuite, extraSuites) in showTest.Runs)
                    {
                        var markerSymbol = run.Outcome is TestOutcome.Error
                            ? "❗"
                            : run.Outcome is TestOutcome.Failed
                            ? "❌"
                            : $"❓ ({run.Outcome})";
                        _ = sb.AppendLine($"<details><summary>➞ {markerSymbol} {mainSuite} {run.Name}</summary>");


                        if (extraSuites.Length > 0)
                        {
                            _ = sb.AppendLine();
                            for (var i = 0; i < extraSuites.Length; i++)
                            {
                                var suite = extraSuites[i];
                                if (i > 0) _ = sb.Append(" | ");
                                _ = sb.Append("`" + suite + "`");
                            }
                            _ = sb.AppendLine().AppendLine();
                        }

                        if (run.Duration != default)
                        {
                            _ = sb.AppendLine().AppendLine($"*Took {run.Duration}*").AppendLine();
                        }

                        if (run.ExceptionMessage is not null)
                        {
                            _ = sb.AppendLine($"""
                                Exception message:

                                ```
                                {run.ExceptionMessage}
                                ```

                                """).AppendLine();
                        }

                        if (run.ExceptionStackTrace is not null)
                        {
                            _ = sb.AppendLine($"""
                                Stack trace:

                                ```
                                {run.ExceptionStackTrace}
                                ```

                                """).AppendLine();
                        }

                        if (run.StdOut is not null)
                        {
                            _ = sb.AppendLine($"""
                                <details>
                                <summary>Test Standard Output</summary>

                                ```
                                {run.StdOut}
                                ```

                                </details>
                                """).AppendLine();
                        }

                        if (run.StdErr is not null)
                        {
                            _ = sb.AppendLine($"""
                                <details>
                                <summary>Test Standard Error</summary>

                                ```
                                {run.StdErr}
                                ```

                                </details>
                                """).AppendLine();
                        }

                        _ = sb.AppendLine("</details>");
                    }

                    _ = sb.AppendLine("</details>");
                }
            }

            return sb.ToString();
        }
    }

    internal enum TestResultFormatMode
    {
        Comment,
        Summary,
    }
}
