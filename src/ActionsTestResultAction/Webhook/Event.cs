using System.Text.Json.Serialization;

namespace ActionsTestResultAction.Webhook
{
    [JsonSourceGenerationOptions(
        AllowTrailingCommas = true,
        GenerationMode = JsonSourceGenerationMode.Default,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        UseStringEnumConverter = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
    [JsonSerializable(typeof(Event))]
    internal partial class EventJsonContext : JsonSerializerContext
    {
    }

    internal class Event
    {
        public PullRequest? PullRequest { get; set; }
    }

    internal sealed class PullRequest
    {
        public string Url { get; set; } = "";
        public PRHead Head { get; set; } = new();
    }

    internal sealed class PRHead
    {
        public string Sha { get; set; } = "";
    }
}
