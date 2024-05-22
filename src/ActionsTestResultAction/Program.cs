﻿using System.Text.Json;
using System.Xml.Linq;
using ActionsTestResultAction;
using ActionsTestResultAction.Webhook;
using Octokit;
using Octokit.GraphQL;
using Schemas.VisualStudio.TeamTest;
using Serilog;
using Serilog.Events;

const string MarkerString = "<!-- GHA-Test-Results-Comment -->";

var logger = new LoggerConfiguration()
    .MinimumLevel.Is(Env.Debug ? LogEventLevel.Verbose : LogEventLevel.Information)
    .WriteTo.Sink(new GitHubActionsLogSink())
    .CreateLogger();

var gqlCheckMinimized = new Query()
    .Nodes(Octokit.GraphQL.Variable.Var("nodes"))
    .OfType<Octokit.GraphQL.Model.IssueComment>()
    .Select(c => new { c.Id, c.IsMinimized })
    .Compile();

var gqlUpdateMinimized = new Mutation()
    .MinimizeComment(Octokit.GraphQL.Variable.Var("minpay"))
    .Select(p => p.MinimizedComment.IsMinimized)
    .Compile();

try
{
    var client = Env.CreateClient(new("nike4613-actions-test-results", "1.0.0"));
    var gql = Env.CreateGQLConnection(new("nike4613-actions-test-results", "1.0.0"));

    var eventContent = await File.ReadAllTextAsync(Env.GITHUB_EVENT_PAYLOAD ?? "event.json").ConfigureAwait(false);
    var eventPayload = JsonSerializer.Deserialize(eventContent, EventJsonContext.Default.Event);
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
        logger.Debug("Loading file {File}", file);
        // only support TRX for now
        var trxSrc = await File.ReadAllTextAsync(file).ConfigureAwait(false);
        var doc = XDocument.Parse(trxSrc);

        logger.Debug("Loaded model");
        var name = Path.GetRelativePath(Environment.CurrentDirectory, file);
        results.RecordTrxTests(name, doc);
    }

    // compute the final collection
    var collection = results.Collect();

    // now report it appropriately
    logger.Debug("Found {Suites} test suites, with {Tests} tests", collection.TestSuiteRuns.Length, collection.Tests.Length);

    // always create a check
    // for our check, we'll always create diags for the relevant output tests, but once only
    try
    {
        logger.Debug("Adding check");

        var checkOutput = new NewCheckRunOutput(inputs.CheckName, collection.Format(null, TestResultFormatMode.Summary))
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

    if (eventPayload.PullRequest is not null)
    {
        // this is a PR, we always want to hide outdated comments
        var commentsToMinimize = new List<ID>();

        logger.Debug("Hiding existing PR comments");
        try
        {
            // now, lets go through the existing comments on the PR to find and hide the old one
            foreach (var comment in await client.Issue.Comment.GetAllForIssue(repoId, eventPayload.PullRequest.Number).ConfigureAwait(false))
            {
                /*
                // don't hide our new comment
                if (comment.Id == newComment.Id) continue;
                */

                logger.Verbose("Inspecting comment {Id} from {Login}", comment.Id, comment.User.Login);

                // don't touch any other users' comments
                if (comment.User.Login != Env.GITHUB_TOKEN_ACTOR)
                {
                    logger.Verbose("Does not match required user {User}", Env.GITHUB_TOKEN_ACTOR);
                    continue;
                }

                // don't touch any comments that don't look like ours
                if (!comment.Body.StartsWith(MarkerString, StringComparison.Ordinal))
                {
                    logger.Verbose("Does not start with marker string");
                    continue;
                }

                // this comment looks like one of ours; hide it (or rather, add it to a list to hide. We need to hit the GraphQL API to do so.)
                commentsToMinimize.Add(new(comment.NodeId));
            }
        }
        catch (Exception e)
        {
            logger.Error(e, "Could not get comments on PR #{PRNumber}", eventPayload.PullRequest.Number);
        }

        logger.Debug("Found {NumComments} comments to minimize", commentsToMinimize.Count);

        try
        {
            var realToMinimize = new List<ID>();

            if (commentsToMinimize.Count > 0)
            {
                // first, filter our updates by comments we care about that aren't already minimized
                var checkMinimizedResult = await gql
                    .Run(gqlCheckMinimized, new Dictionary<string, object> { { "nodes", commentsToMinimize } })
                    .ConfigureAwait(false);
                foreach (var item in checkMinimizedResult)
                {
                    if (!item.IsMinimized)
                    {
                        realToMinimize.Add(item.Id);
                    }
                }
            }

            logger.Debug("Filtered to {NumComments} to send req for", realToMinimize.Count);

            // now we can go and minimize them
            foreach (var id in realToMinimize)
            {
                var result = await gql
                    .Run(gqlUpdateMinimized, new Dictionary<string, object>
                    {
                        {
                            "minpay",
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

    switch (inputs.CommentMode)
    {
        case CommentMode.Failures:
            if (collection.AggregateRun.Outcome is TestOutcome.Failed) goto case CommentMode.Always;
            goto case CommentMode.Errors;

        case CommentMode.Errors:
            if (collection.AggregateRun.Outcome is TestOutcome.Error) goto case CommentMode.Always;
            break;

        case CommentMode.Always:
            {
                // render out the comment body
                var body = collection.Format(inputs.CommentTitle, TestResultFormatMode.Comment);

                if (inputs.CommentOnCommit && eventPayload.PullRequest is null)
                {
                    logger.Debug("Adding comment on commit");

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
                    logger.Debug("Adding comment on pull request {PRNumber}", eventPayload.PullRequest.Number);

                    try
                    {
                        var prCommentBody = MarkerString + body;

                        // first, lets create the comment
                        var newComment = await client.Issue.Comment.Create(repoId, eventPayload.PullRequest.Number, prCommentBody).ConfigureAwait(false);
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
