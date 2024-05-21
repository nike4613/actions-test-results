using System.Text.Json;
using ActionsTestResultAction;
using ActionsTestResultAction.Webhook;
using HamedStack.VSTest;
using Octokit;
using Schemas.VisualStudio.TeamTest;
using Serilog;
using Serilog.Events;

var logger = new LoggerConfiguration()
    .MinimumLevel.Is(Env.Debug ? LogEventLevel.Verbose : LogEventLevel.Information)
    .WriteTo.Sink(new GitHubActionsLogSink())
    .CreateLogger();

try
{
    var client = Env.CreateClient(new("nike4613/actions-test-results"));

    var eventPayload = JsonSerializer.Deserialize(
        await File.ReadAllTextAsync(Env.GITHUB_EVENT_PAYLOAD ?? "event.json").ConfigureAwait(false),
        EventJsonContext.Default.Event);
    if (eventPayload is null)
    {
        logger.Error("Event payload is null");
        return 1;
    }
    if (Env.GITHUB_REPOSITORY_ID is not { } repoIdString)
    {
        logger.Error("GITHUB_REPOSITORY_ID not specified");
        return 1;
    }
    if (!long.TryParse(repoIdString, out var repoId))
    {
        logger.Error("GITHUB_REPOSITORY_ID is not a valid number {ProvidedRepoId}", repoIdString);
        return 1;
    }

    var inputs = Inputs.Get(Env.GITHUB_EVENT_NAME ?? "", eventPayload);

    if (inputs.Files.Length == 0)
    {
        logger.Warning("No files were matched.");
        return 0;
    }

    logger.Debug("Running for {Repository}", Env.GITHUB_REPOSITORY);

    var results = new TestResultCollector();

    foreach (var file in inputs.Files)
    {
        // only support TRX for now
        var trxSrc = await File.ReadAllTextAsync(file).ConfigureAwait(false);
        var trxModel = TestSchemaManager.ConvertToTestRun(trxSrc);
        if (trxModel is null) continue;

        results.RecordTrxTests(file, trxModel);
    }

    // compute the final collection
    var collection = results.Collect();

    // now report it appropriately

    // always create a check
    // for our check, we'll always create diags for the relevant output tests, but once only
    try
    {
        var checkOutput = new NewCheckRunOutput(inputs.CheckName, collection.Format(inputs.CheckName, TestResultFormatMode.Summary))
        {
            Annotations = collection.ShowTests
                .Select(test =>
                {
                    var messageRun = test.Runs.FirstOrDefault().Run;
                    var message = messageRun?.ExceptionMessage ?? $"`{test.Test.Name}` failed";

                    return new NewCheckRunAnnotation(test.Test.Name, 0, 0, CheckAnnotationLevel.Failure, message)
                    {

                    };
                }).ToArray()
        };

        _ = await client.Check.Run.Create(repoId, new(inputs.CheckName, inputs.CommitSha)
        {
            Conclusion = new(
                collection.AggregateRun.Outcome switch
                {
                    TestOutcome.Passed => CheckConclusion.Success,
                    TestOutcome.Failed => CheckConclusion.Failure,
                    TestOutcome.Error => CheckConclusion.Failure,
                    _ => CheckConclusion.Neutral,
                }),
            Output = checkOutput,
            Status = new(CheckStatus.Completed),
        }).ConfigureAwait(false);
    }
    catch (Exception e)
    {
        logger.Error(e, "Could not create check");
    }

    switch (inputs.CommentMode)
    {
        case CommentMode.Failures:
            if (collection.AggregateRun.Outcome is TestOutcome.Failed) goto case CommentMode.Errors;
            break;

        case CommentMode.Errors:
            if (collection.AggregateRun.Outcome is TestOutcome.Error) goto case CommentMode.Always;
            break;

        case CommentMode.Always:
            {
                // render out the comment body
                var body = collection.Format(inputs.CommentTitle, TestResultFormatMode.Comment);

                if (inputs.CommentOnCommit && eventPayload.PullRequest is null)
                {
                    // user wants us to comment on commit, and this isn't part of a PR
                    try
                    {
                        _ = await client.Repository.Comment.Create(repoId, inputs.CommitSha, new(body)).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        logger.Error(e, "Could not comment on commit {CommitSha}", inputs.CommitSha);
                    }
                }
                else if (eventPayload.PullRequest is not null)
                {
                    // this is a PR, we want to comment on it as well, but only if we haven't already commented; in that case, we want to mark the old one as obsolete
                    try
                    {
                        const string MarkerString = "<!-- GHA-Test-Results-Comment -->\n";
                        var prCommentBody = MarkerString + body;

                        // first, lets create the comment
                        var newComment = await client.Issue.Comment.Create(repoId, eventPayload.PullRequest.Number, prCommentBody).ConfigureAwait(false);

                        var selfUser = await client.User.Current().ConfigureAwait(false);

                        // now, lets go through the existing comments on the PR to find and hide the old one
                        foreach (var comment in await client.Issue.Comment.GetAllForIssue(repoId, eventPayload.PullRequest.Number).ConfigureAwait(false))
                        {
                            // don't hide our new comment
                            if (comment.Id == newComment.Id) continue;

                            // don't touch any other users' comments
                            if (comment.User.Id != selfUser.Id) continue;

                            // don't touch any comments that don't look like ours
                            if (!comment.Body.StartsWith(MarkerString, StringComparison.Ordinal)) continue;

                            // this comment looks like one of ours; hide it (or rather, add it to a list to hide. We need to hit the GraphQL API to do so.)
                        }
                    }
                    catch (Exception e)
                    {
                        logger.Error(e, "Could not comment on PR #{PRNumber}", eventPayload.PullRequest.Number);
                    }
                }
            }
            break;

        default:
        case CommentMode.Off:
            break;
    }

    return 0;
}
catch (Exception e)
{
    logger.Error(e, "An error occurred while executing");
    return 1;
}
