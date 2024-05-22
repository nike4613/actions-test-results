global using GQLConnection = Octokit.GraphQL.Connection;
global using GQLProductHeaderValue = Octokit.GraphQL.ProductHeaderValue;
using Octokit;


namespace ActionsTestResultAction
{
    internal static class Env
    {
        public static readonly string? GITHUB_SERVER_URL = Environment.GetEnvironmentVariable(nameof(GITHUB_SERVER_URL));
        public static readonly string? GITHUB_API_URL = Environment.GetEnvironmentVariable(nameof(GITHUB_API_URL));
        public static readonly string? GITHUB_GRAPHQL_URL = Environment.GetEnvironmentVariable(nameof(GITHUB_GRAPHQL_URL));

        public static readonly Uri ApiUri = GITHUB_API_URL is not null ? new Uri(GITHUB_API_URL) : GitHubClient.GitHubApiUrl;
        public static readonly Uri GraphQLUri = GITHUB_GRAPHQL_URL is not null ? new Uri(GITHUB_GRAPHQL_URL) : GQLConnection.GithubApiUri;

        public static readonly string? GITHUB_TOKEN = GetInput(nameof(GITHUB_TOKEN)) ?? Environment.GetEnvironmentVariable(nameof(GITHUB_TOKEN));
        public static readonly string? GITHUB_TOKEN_ACTOR = GetInput(nameof(GITHUB_TOKEN_ACTOR)) ?? Environment.GetEnvironmentVariable(nameof(GITHUB_TOKEN_ACTOR));
        public static readonly string? GITHUB_EVENT_NAME = GetInput(Inputs.EventNameVar) ?? Environment.GetEnvironmentVariable(nameof(GITHUB_EVENT_NAME));
        public static readonly string? GITHUB_EVENT_PAYLOAD = GetInput(Inputs.EventFileVar) ?? Environment.GetEnvironmentVariable(nameof(GITHUB_EVENT_PAYLOAD));
        public static readonly string? GITHUB_OUTPUT = Environment.GetEnvironmentVariable(nameof(GITHUB_OUTPUT));

        public static readonly string? GITHUB_REPOSITORY = Environment.GetEnvironmentVariable(nameof(GITHUB_REPOSITORY));
        public static readonly string? GITHUB_REPOSITORY_ID = Environment.GetEnvironmentVariable(nameof(GITHUB_REPOSITORY_ID));
        public static readonly string? GITHUB_REPOSITORY_OWNER = Environment.GetEnvironmentVariable(nameof(GITHUB_REPOSITORY_OWNER));
        public static readonly string? GITHUB_REPOSITORY_OWNER_ID = Environment.GetEnvironmentVariable(nameof(GITHUB_REPOSITORY_OWNER_ID));

        public static readonly string? GITHUB_SHA = Environment.GetEnvironmentVariable(nameof(GITHUB_SHA));

        public static readonly bool OnActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        public static readonly bool Debug = Environment.GetEnvironmentVariable("RUNNER_DEBUG") == "1";
        public static readonly string TmpDir = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();

        private sealed class CredentialStore : ICredentialStore
        {
            public static readonly CredentialStore Instance = new();

            public Task<Credentials> GetCredentials()
                => Task.FromResult(GITHUB_TOKEN is not null ? new Credentials(GITHUB_TOKEN) : Credentials.Anonymous);
        }

        public static Connection CreateConnection(ProductHeaderValue product)
            => new(product, ApiUri, CredentialStore.Instance);

        public static GQLConnection CreateGQLConnection(GQLProductHeaderValue product)
            => new(product, GraphQLUri, GITHUB_TOKEN);

        public static GitHubClient CreateClient(ProductHeaderValue product)
            => new(CreateConnection(product));

        public static string? GetInput(string name) => Environment.GetEnvironmentVariable("INPUT_" + name.ToUpperInvariant());

    }
}
