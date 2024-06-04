using System.Collections.Immutable;
using System.Text;
using Schemas.VisualStudio.TeamTest;

namespace ActionsTestResultAction
{
    internal readonly record struct ShownTest(
        ShowReason Reason,
        Test Test,
        ImmutableArray<(TestRun Run, string RunSuite, ImmutableArray<string> Suites)> Runs,
        ImmutableArray<string> Suites,
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

        public string Format(string? title, TestResultFormatMode mode, int listMaxSuites = 7)
        {
            var sb = new StringBuilder();
            var mb = new MarkdownBuilder(sb);

            if (!string.IsNullOrWhiteSpace(title))
            {
                _ = mb.AppendLine($"# {title}").AppendLine();
            }

            // first, lets emit a table containing totals
            var hasErrors = Errored > 0 || AggregateRun.Errored > 0;
            var errorTableHdrExt1 = hasErrors ? " ---: |" : "";
            var errorTableHdrExt2 = hasErrors ? " Errored |" : "";
            var errorTableUniqExt = hasErrors ? $"{AggregateRun.Errored} |" : "";
            var errorTableTotalExt = hasErrors ? $"{Errored} |" : "";

            _ = mb.AppendLine($"""

            | | Total | Skipped | Passed | Failed |{errorTableHdrExt2}
            | ---: | ---: | ---: | ---: | ---: |{errorTableHdrExt1}
            | Unique | {AggregateRun.Total} | {AggregateRun.Skipped} | {AggregateRun.Passed} | {AggregateRun.Failed} |{errorTableUniqExt}
            | Total | {Total} | {Skipped} | {Passed} | {Failed} |{errorTableTotalExt}

            """);

            if (mode is not TestResultFormatMode.Summary)
            {
                _ = mb.AppendLine($"### Failing{(hasErrors ? " or Erroring" : "")} runs").AppendLine();

                foreach (var showTest in ShowTests)
                {
                    if (sb.Length > 65536 - (8 * 1024))
                    {
                        _ = mb.AppendLine().AppendLine("*Remaining tests skipped, because comment is too long.*");
                        break;
                    }

                    _ = mb.AppendLine($"""
                        <details>
                        <summary>❌ {showTest.Test.Name}</summary>

                        """).IncreaseIndent();

                    _ = mb.AppendLine();

                    if (showTest.Test.ClassName is not null)
                    {
                        _ = mb.Append($"Class Name: `{showTest.Test.ClassName}` | ");
                    }
                    if (showTest.Test.MethodName is not null)
                    {
                        _ = mb.Append($"Method Name: `{showTest.Test.MethodName}` | ");
                    }

                    var showReason = showTest.Reason switch
                    {
                        ShowReason.None => "???",
                        ShowReason.FailingAlways => "is always failing",
                        ShowReason.FailingSometimes => "is sometimes failing",
                        ShowReason.Errored => "errored in at least one run",
                        var x => x.ToString(),
                    };

                    _ = mb
                        .AppendLine($"*This test {showReason}.*")
                        .AppendLine();

                    if (showTest.Suites.Length > 0)
                    {
                        _ = mb.AppendLine($"<details><summary>Failures present in</summary>")
                            .AppendLine().IncreaseIndent()
                            .AppendLine();

                        var i = 0;
                        for (; i < showTest.Suites.Length && i < listMaxSuites; i++)
                        {
                            _ = mb.AppendLine($"- {showTest.Suites[i]}");
                        }

                        if (i < showTest.Suites.Length)
                        {
                            _ = mb.AppendLine($"*and {showTest.Suites.Length - i} more");
                        }

                        _ = mb.AppendLine().DecreaseIndent().AppendLine("</details>");
                    }


                    foreach (var (run, mainSuite, extraSuites) in showTest.Runs)
                    {
                        if (sb.Length > 65536 - (6 * 1024))
                        {
                            _ = mb.AppendLine().AppendLine("*Remaining runs skipped, because comment is too long.*");
                            break;
                        }

                        var markerSymbol = run.Outcome is TestOutcome.Error
                            ? "❗"
                            : run.Outcome is TestOutcome.Failed
                            ? "❌"
                            : $"❓ ({run.Outcome})";
                        _ = mb.AppendLine($"<details><summary>{markerSymbol} {mainSuite} {run.Name}</summary>").AppendLine().IncreaseIndent();

                        if (run.Duration != default)
                        {
                            _ = mb.AppendLine().AppendLine($"*Took {run.Duration}*").AppendLine();
                        }

                        if (extraSuites.Length > 0)
                        {
                            _ = mb.AppendLine($"<details><summary>Failure also present in</summary>")
                                .AppendLine().IncreaseIndent()
                                .AppendLine();

                            var i = 0;
                            for (; i < extraSuites.Length && i < listMaxSuites; i++)
                            {
                                _ = mb.AppendLine($"- {extraSuites[i]}");
                            }

                            if (i < extraSuites.Length)
                            {
                                _ = mb.AppendLine($"*and {extraSuites.Length - i} more");
                            }

                            _ = mb.AppendLine().DecreaseIndent().AppendLine("</details>");
                        }

                        if (run.ExceptionMessage is not null)
                        {
                            _ = mb.AppendLine($"""
                                Exception message:

                                ```
                                {run.ExceptionMessage}
                                ```

                                """).AppendLine();
                        }

                        if (run.ExceptionStackTrace is not null)
                        {
                            _ = mb.AppendLine($"""
                                Stack trace:

                                ```
                                {run.ExceptionStackTrace}
                                ```

                                """).AppendLine();
                        }

                        if (run.StdOut is not null)
                        {
                            _ = mb.AppendLine($"""
                                <details>
                                <summary>Test Standard Output</summary>

                                """)
                                .IncreaseIndent()
                                .AppendLine($"""
                                ```
                                {run.StdOut}
                                ```

                                """)
                                .DecreaseIndent()
                                .AppendLine("</details>")
                                .AppendLine();
                        }

                        if (run.StdErr is not null)
                        {
                            _ = mb.AppendLine($"""
                                <details>
                                <summary>Test Standard Error</summary>

                                """)
                                .IncreaseIndent()
                                .AppendLine($"""
                                ```
                                {run.StdErr}
                                ```

                                """)
                                .DecreaseIndent()
                                .AppendLine("</details>")
                                .AppendLine();
                        }

                        _ = mb.DecreaseIndent().AppendLine("</details>");
                    }

                    _ = mb.DecreaseIndent().AppendLine("</details>");
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
