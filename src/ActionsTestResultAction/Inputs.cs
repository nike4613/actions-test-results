using System.Collections.Immutable;
using Actions.Glob;
using ActionsTestResultAction.Webhook;
using CommunityToolkit.Diagnostics;

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
        public const string UseEmojisVar = "USE_EMOJIS";
        public const string CommentOnCommitVar = "COMMENT_ON_COMMIT";
        public const string GistTokenVar = "GIST_TOKEN";

        public const string EventFileVar = "EVENT_FILE";
        public const string EventNameVar = "EVENT_NAME";

        public string CheckName { get; }
        public string CommentTitle { get; }
        public string? GistToken { get; }
        public CommentMode CommentMode { get; }
        public ImmutableArray<string> Files { get; }
        public bool UseEmojis { get; }
        public bool CommentOnCommit { get; }
        public string CommitSha { get; }

        private static readonly char[] separator = ['\r', '\n'];

        private Inputs(string eventName, Event eventPayload)
        {
            CheckName = Env.GetInput(CheckNameVar) ?? "Test Results";
            var title = Env.GetInput(CommentTitleVar);
            if (string.IsNullOrWhiteSpace(CommentTitle))
            {
                title = CheckName;
            }
            CommentTitle = title!;
            GistToken = Env.GetInput(GistTokenVar);
            if (string.IsNullOrEmpty(GistToken))
            {
                GistToken = null;
            }
            CommentMode = Env.GetInput(CommentModeVar)?.ToUpperInvariant() switch
            {
                "ALWAYS" => CommentMode.Always,
                "FAILURES" => CommentMode.Failures,
                "ERRORS" => CommentMode.Errors,
                "OFF" => CommentMode.Off,
                var x => ThrowHelper.ThrowInvalidOperationException<CommentMode>($"Invalid value for comment_mode: {x}")
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
                if (line is ['!', .. var rest])
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

            UseEmojis = Env.GetInput(UseEmojisVar) is "true";

            CommentOnCommit = Env.GetInput(CommentOnCommitVar) is "true";

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

}
