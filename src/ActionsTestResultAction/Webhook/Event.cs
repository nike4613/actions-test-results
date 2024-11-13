using System.Text.Json.Serialization;

namespace ActionsTestResultAction.Webhook
{
    [JsonSourceGenerationOptions(
        AllowTrailingCommas = true,
        GenerationMode = JsonSourceGenerationMode.Default,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        UseStringEnumConverter = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(Event))]
    internal sealed partial class EventJsonContext : JsonSerializerContext;

    internal sealed class Event
    {
        public PullRequest? PullRequest { get; set; }
    }

    internal sealed class PullRequest
    {
        public string Url { get; set; } = "";
        public long Id { get; set; }
        public int Number { get; set; }
        public PRHead Head { get; set; } = new();
    }

    internal sealed class PRHead
    {
        public string Sha { get; set; } = "";
    }
}
