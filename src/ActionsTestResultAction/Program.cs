using System.Text.Json;
using ActionsTestResultAction;
using ActionsTestResultAction.Webhook;
using HamedStack.VSTest;
using Octokit;
using Octokit.GraphQL;
using Schemas.VisualStudio.TeamTest;
using Serilog;
using Serilog.Events;

var logger = new LoggerConfiguration()
    .MinimumLevel.Is(Env.Debug ? LogEventLevel.Verbose : LogEventLevel.Information)
    .WriteTo.Sink(new GitHubActionsLogSink())
    .CreateLogger();

var gqlCheckMinimized = new Query()
    .Nodes(Octokit.GraphQL.Variable.Var("Nodes"))
    .OfType<Octokit.GraphQL.Model.IssueComment>()
    .Select(c => new { c.Id, c.IsMinimized })
    .Compile();

var gqlUpdateMinimized = new Mutation()
    .MinimizeComment(Octokit.GraphQL.Variable.Var("MinimizePayload"))
    .Select(p => p.MinimizedComment.IsMinimized)
    .Compile();

try
{
    var client = Env.CreateClient(new("nike4613/actions-test-results"));
    var gql = Env.CreateGQLConnection(new("nike4613/actions-test-results"));

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
                    var commentsToMinimize = new HashSet<string>();

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
                            _ = commentsToMinimize.Add(comment.NodeId);
                        }
                    }
                    catch (Exception e)
                    {
                        logger.Error(e, "Could not comment on PR #{PRNumber}", eventPayload.PullRequest.Number);
                    }

                    try
                    {
                        var realToMinimize = new List<ID>();

                        // first, filter our updates by comments we care about that aren't already minimized
                        var checkMinimizedResult = await gql
                            .Run(gqlCheckMinimized, new Dictionary<string, object> { { "Nodes", commentsToMinimize } })
                            .ConfigureAwait(false);
                        foreach (var item in checkMinimizedResult)
                        {
                            if (!item.IsMinimized)
                            {
                                realToMinimize.Add(item.Id);
                            }
                        }

                        // now we can go and minimize them
                        foreach (var id in realToMinimize)
                        {
                            var result = await gql
                                .Run(gqlUpdateMinimized, new Dictionary<string, object>
                                {
                                    {
                                        "MinimizePayload",
                                        new Octokit.GraphQL.Model.MinimizeCommentInput()
                                        {
                                            Classifier = Octokit.GraphQL.Model.ReportedContentClassifiers.Outdated,
                                            ClientMutationId = "nike4613/actions-test-results",
                                            SubjectId = id,
                                        }
                                    }
                                }).ConfigureAwait(false);
                            if (!result)
                            {
                                logger.Warning("Could not minimize comment with node ID {ID}", id.Value);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        logger.Error(e, "Could not minimize comments on PR #{PRNumber}", eventPayload.PullRequest.Number);
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
