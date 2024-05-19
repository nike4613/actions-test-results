using ActionsTestResultAction;
using Serilog;
using Serilog.Events;

var logger = new LoggerConfiguration()
    .MinimumLevel.Is(Env.Debug ? LogEventLevel.Verbose : LogEventLevel.Information)
    .WriteTo.Sink(new GitHubActionsLogSink())
    .CreateLogger();

var client = Env.CreateClient(new("nike4613/actions-test-results"));
var inputs = Inputs.Get();

logger.Debug("Running for {Repository}", Env.GITHUB_REPOSITORY);
