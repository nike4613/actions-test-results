using System.Text.Json;
using ActionsTestResultAction;
using ActionsTestResultAction.Webhook;
using Serilog;
using Serilog.Events;

var logger = new LoggerConfiguration()
    .MinimumLevel.Is(Env.Debug ? LogEventLevel.Verbose : LogEventLevel.Information)
    .WriteTo.Sink(new GitHubActionsLogSink())
    .CreateLogger();

try
{
    var client = Env.CreateClient(new("nike4613/actions-test-results"));

    var eventPayload = JsonSerializer.Deserialize(File.ReadAllText(Env.GITHUB_EVENT_PAYLOAD ?? "event.json"), EventJsonContext.Default.Event);
    if (eventPayload is null)
    {
        logger.Error("Event payload is null");
        return 1;
    }
    var inputs = Inputs.Get(Env.GITHUB_EVENT_NAME ?? "", eventPayload);

    if (inputs.Files.Length == 0)
    {
        logger.Warning("No files were matched.");
        return 0;
    }

    logger.Debug("Running for {Repository}", Env.GITHUB_REPOSITORY);


    return 0;
}
catch (Exception e)
{
    logger.Error(e, "An error occurred while executing");
    return 1;
}
