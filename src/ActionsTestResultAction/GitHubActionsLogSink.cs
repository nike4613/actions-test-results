using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ActionsTestResultAction
{
    internal sealed class GitHubActionsLogSink : ILogEventSink
    {
        public const string BeginGroupProperty = "GHA-BeginGroup";
        public const string EndGroupProperty = "GHA-EndGroup";

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Properties.ContainsKey(BeginGroupProperty))
            {
                Console.WriteLine("::group::" + logEvent.RenderMessage());
                return;
            }

            string kind;
            switch (logEvent.Level)
            {
                case LogEventLevel.Debug:
                case LogEventLevel.Verbose:
                    Console.WriteLine("::debug::" + logEvent.RenderMessage());
                    if (logEvent.Exception is not null)
                    {
                        var str = logEvent.Exception.ToString();
                        foreach (var line in str.Split('\r', '\n'))
                        {
                            Console.WriteLine("::debug::" + line.Trim());
                        }
                    }
                    break;

                default:
                case LogEventLevel.Information:
                    Console.WriteLine(logEvent.RenderMessage());
                    if (logEvent.Exception is not null)
                    {
                        Console.WriteLine(logEvent.Exception);
                    }
                    break;

                case LogEventLevel.Warning:
                    kind = "warning";
                    goto KindWithProps;
                case LogEventLevel.Error:
                    kind = "error";
                    goto KindWithProps;
                case LogEventLevel.Fatal:
                    kind = "error";
                    goto KindWithProps;

                KindWithProps:
                    var file = logEvent.Properties.TryGetValue("File", out var prop) ? prop.ToString() : null;
                    var titleProp = logEvent.Properties.TryGetValue("Title", out prop) ? prop.ToString() : null;
                    var messageProp = logEvent.Properties.TryGetValue("Message", out prop) ? prop.ToString() : null;

                    var title = logEvent.RenderMessage();
                    var message = messageProp;
                    if (titleProp is not null)
                    {
                        message = title;
                        title = titleProp;
                    }

                    message ??= "";

                    if (logEvent.Exception is not null)
                    {
                        message += "\n" + logEvent.Exception.ToString();
                    }

                    foreach (var line in message.Split('\r', '\n'))
                    {
                        if (file != null)
                        {
                            Console.WriteLine($"::{kind} file={file},title={title}::{line.Trim()}");
                        }
                        else
                        {
                            Console.WriteLine($"::{kind} title={title}::{line.Trim()}");
                        }
                    }
                    break;
            }

            if (logEvent.Properties.ContainsKey(EndGroupProperty))
            {
                Console.WriteLine("::endgroup::");
            }
        }
    }

    internal static class LoggerExtensions
    {
        public readonly struct GroupContext(ILogger logger) : IDisposable
        {
            private readonly ILogger logger = logger;

            public void Dispose()
            {
                if (logger.BindProperty(GitHubActionsLogSink.EndGroupProperty, null, false, out var prop))
                {
                    logger.Write(
                        new LogEvent(
                            DateTimeOffset.Now, LogEventLevel.Information, null, MessageTemplate.Empty,
                            [prop]));
                }
            }
        }

        public static GroupContext Group(this ILogger logger, string message)
        {
            if (logger.BindMessageTemplate(message, null, out var template, out var props)
             && logger.BindProperty(GitHubActionsLogSink.BeginGroupProperty, null, false, out var extraProp))
            {
                logger.Write(
                    new LogEvent(
                        DateTimeOffset.Now, LogEventLevel.Information, null, template, [.. props, extraProp]));
            }

            return new(logger);
        }
    }
}
