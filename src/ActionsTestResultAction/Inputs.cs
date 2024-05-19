﻿using System.Collections.Immutable;
using Actions.Glob;
using ActionsTestResultAction.Webhook;
using CommunityToolkit.Diagnostics;
using Humanizer.Localisation;

namespace ActionsTestResultAction
{
    internal sealed class Inputs
    {
        public const string CommitVar = "COMMIT";
        public const string CheckNameVar = "CHECK_NAME";
        public const string CommentTitleVar = "COMMENT_TITLE";
        public const string CommentModeVar = "COMMENT_MODE";
        public const string FailOnVar = "FAIL_ON";
        public const string FilesVar = "FILES";
        public const string TimeUnitVar = "TIME_UNIT";
        public const string CheckRunVar = "CHECK_RUN";
        public const string JobSummaryVar = "JOB_SUMMARY";

        public const string EventFileVar = "EVENT_FILE";
        public const string EventNameVar = "EVENT_NAME";

        public string CheckName { get; }
        public string CommentTitle { get; }
        public CommentMode CommentMode { get; }
        public FailOnEvent FailOn { get; }
        public ImmutableArray<string> Files { get; }
        public TimeUnit TimeUnit { get; }
        public bool CheckRun { get; }
        public bool JobSummary { get; }

        public string CommitSha { get; }

        private static readonly char[] separator = ['\r', '\n'];

        private Inputs(string eventName, Event eventPayload)
        {
            CheckName = Env.GetInput(CheckNameVar) ?? "Test Results";
            CommentTitle = Env.GetInput(CommentTitleVar) ?? CheckName;
            CommentMode = Env.GetInput(CommentModeVar)?.ToLowerInvariant() switch
            {
                "always" => CommentMode.Always,
                "failures" => CommentMode.Failures,
                "errors" => CommentMode.Errors,
                "off" => CommentMode.Off,
                var x => ThrowHelper.ThrowInvalidOperationException<CommentMode>($"Invalid value for comment_mode: {x}")
            };
            FailOn = Env.GetInput(FailOnVar)?.ToLowerInvariant() switch
            {
                "test failures" => FailOnEvent.TestFailures,
                "errors" => FailOnEvent.Errors,
                "nothing" or "never" => FailOnEvent.Nothing,
                var x => ThrowHelper.ThrowInvalidOperationException<FailOnEvent>($"Invalid value for comment_mode: {x}")
            };

            var filesInput = Env.GetInput(FilesVar);
            if (filesInput is null)
            {
                ThrowHelper.ThrowInvalidOperationException("Must specify value for input files");
            }

            var include = new List<string>();
            var exclude = new List<string>();
            foreach (var line in filesInput.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line is ['!', ..var rest])
                {
                    exclude.Add(rest);
                }
                else
                {
                    include.Add(line);
                }
            }

            var globber = Globber.Create(include, exclude);
            Files = globber.GlobFiles().ToImmutableArray();

            TimeUnit = Env.GetInput(TimeUnitVar) is { } tu ? Enum.Parse<TimeUnit>(tu, true) : TimeUnit.Second;
            CheckRun = Env.GetInput(CheckRunVar) is "true";
            JobSummary = Env.GetInput(JobSummaryVar) is "true";

            var commitSha = Env.GetInput(CommitVar);
            if (commitSha is null)
            {
                if (eventName is "pull_request")
                {
                    commitSha = eventPayload.PullRequest?.Head?.Sha;
                }
            }
            CommitSha = commitSha ?? Env.GITHUB_SHA ?? "";
        }

        public static Inputs Get(string eventName, Event eventPayload) => new(eventName, eventPayload);
    }

    internal enum CommentMode
    {
        Always,
        Failures,
        Errors,
        Off
    }

    internal enum FailOnEvent
    {
        TestFailures,
        Errors,
        Nothing
    }
}