// WithoutHttpsCertificate is an evaluation API. If Aspire renames it, the build fails here.
#pragma warning disable ASPIRECERTIFICATES001

var builder = DistributedApplication.CreateBuilder(args);

// The ports are fixed. In dev mode Keycloak writes the URL that the client used into the iss claim,
// and each service compares iss with its authority exactly. The service ports are in their launch
// profiles.
const int serverPort = 5080; // server/Custos.Server/Properties/launchSettings.json
const int optioPort = 5090; // optio/Optio/Properties/launchSettings.json
const int keycloakPort = 8180;

// Keycloak gets the Client ID Metadata Document of Custos from optio. Keycloak runs in a
// container, so it uses the Aspire container tunnel (ASPIRE_ENABLE_CONTAINER_TUNNEL in
// launchSettings.json) and its host name. The client ID is the URL that Keycloak gets, so Optio
// announces the same URL.
const string tunnelHost = "aspire.dev.internal";
var custosClientId = $"http://{tunnelHost}:{optioPort}/.well-known/custos-client";

// The audience of the tokens of the server: its MCP endpoint, as a harness on this machine reaches
// it. The server maps the endpoint only with the Discord connection on.
var serverResource = $"http://localhost:{serverPort}/mcp";

// The secret of the client optio, for the token exchange. Aspire makes a new value on each start,
// so no user secret is necessary.
var optioSecret = builder.AddParameter(
    "optio-client-secret",
    new GenerateParameterDefault { MinLength = 32, Special = false },
    secret: true
);

// No volume: Keycloak imports the realm on every start. What the console changes lasts one run.
// The realm file is the development realm of deploy/, so that one realm file exists.
var keycloak = builder
    .AddKeycloak("keycloak", keycloakPort)
    .WithImageTag("26.8.2")
    .WithArgs("--features=cimd,resource-indicators")
    .WithEnvironment("CUSTOS_MCP_RESOURCE", serverResource)
    .WithEnvironment("OPTIO_CLIENT_SECRET", optioSecret)
    .WithRealmImport(
        Path.Combine(builder.AppHostDirectory, "..", "..", "keycloak", "custos-realm.json")
    )
    .WithoutHttpsCertificate();

// PostgreSQL 18, the version that the table definitions in issue #1 were tested on. No volume: the
// data lasts one run. The database name is CustosDbContext.ConnectionName of the server.
var postgres = builder.AddPostgres("postgres").WithImageTag("18");
var database = postgres.AddDatabase("custos");

// The database of Optio, for the settings of the guilds. The database name and the connection name
// are OptioDbContext.ConnectionName. The resource name differs, because the project is "optio".
var optioDatabase = postgres.AddDatabase("optio-db", databaseName: "optio");

// The event broker: NATS with JetStream and the users of deploy/nats/nats.conf. A plain container,
// because AddNats adds a single user, which NATS refuses together with a list of users. No volume:
// the events last one run. Aspire makes a new password for each user on each start.
string[] brokerUsers = ["custos", "optio"];
var brokerPasswords = brokerUsers.ToDictionary(
    user => user,
    user =>
        builder.AddParameter(
            $"events-{user}-password",
            new GenerateParameterDefault { MinLength = 32, Special = false },
            secret: true
        )
);
var events = builder
    .AddContainer("events", "nats", "2.15.3-alpine")
    .WithBindMount(
        Path.Combine(builder.AppHostDirectory, "..", "..", "nats", "nats.conf"),
        "/etc/nats/nats.conf",
        isReadOnly: true
    )
    .WithArgs("-c", "/etc/nats/nats.conf")
    .WithEnvironment("CUSTOS_PASSWORD", brokerPasswords["custos"])
    .WithEnvironment("OPTIO_PASSWORD", brokerPasswords["optio"])
    .WithEndpoint(targetPort: 4222, name: "tcp", scheme: "tcp");

// The connection string of a broker user. A service does not wait for the broker: an upload works
// without it, and the client of each service connects again by itself.
var brokerAddress = events.GetEndpoint("tcp").Property(EndpointProperty.HostAndPort);

ReferenceExpression Broker(string user) =>
    ReferenceExpression.Create($"nats://{user}:{brokerPasswords[user]}@{brokerAddress}");

// The launch profile gives the port and the Development environment. appsettings.Development.json
// of the server holds the localhost URLs of the realm and of the MCP endpoint.
var server = builder
    .AddProject<Projects.Custos_Server>("server")
    .WithEnvironment("Mcp__Enabled", "true")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("ConnectionStrings__events", Broker("custos"))
    .WithHttpHealthCheck("/health/ready");

// The parameters of outside accounts are optional: the AppHost adds a parameter only when the
// configuration has a value, for example in the user secrets. Without it, the system starts without
// that account (ADR 0008). Aspire refuses an empty parameter value.
IResourceBuilder<ParameterResource>? OptionalParameter(string name, bool secret = false) =>
    string.IsNullOrEmpty(builder.Configuration[$"Parameters:{name}"])
        ? null
        : builder.AddParameter(name, secret: secret);

// The bot of the server, and the user of a token without the claim discord_id:
//   dotnet user-secrets set Parameters:discord-token <bot token>
//   dotnet user-secrets set Parameters:discord-user-id <Discord user ID of the user dev>
var discordToken = OptionalParameter("discord-token", secret: true);
var discordUserId = OptionalParameter("discord-user-id");
if (discordToken is not null)
{
    server.WithEnvironment("Discord__Token", discordToken);
}
if (discordUserId is not null)
{
    server.WithEnvironment("Custos__DevelopmentDiscordUserId", discordUserId);
}

// appsettings.Development.json of Optio holds the localhost URLs of Optio, the realm, and the
// upload API of the server.
var optio = builder
    .AddProject<Projects.Optio>("optio")
    .WithEnvironment("Optio__CustosClientId", custosClientId)
    .WithEnvironment("Optio__OptioClientSecret", optioSecret)
    .WithReference(server)
    .WithReference(optioDatabase, connectionName: "optio")
    .WaitFor(optioDatabase)
    .WithEnvironment("ConnectionStrings__events", Broker("optio"))
    .WithHttpHealthCheck("/health/ready");

// The bot of Optio for the stream, of its own Discord application:
//   dotnet user-secrets set Parameters:optio-discord-token <bot token>
if (OptionalParameter("optio-discord-token", secret: true) is { } optioDiscordToken)
{
    optio.WithEnvironment("Optio__DiscordToken", optioDiscordToken);
}
if (discordUserId is not null)
{
    optio.WithEnvironment("Optio__DevelopmentDiscordUserId", discordUserId);
}

// The query, only with a key of the Claude API in the user secrets:
//   dotnet user-secrets set Parameters:claude-api-key <key>
// The MCP service is the server, with the example system prompt of the repository.
if (OptionalParameter("claude-api-key", secret: true) is { } claudeApiKey)
{
    optio
        .WithEnvironment("Optio__ClaudeApiKey", claudeApiKey)
        .WithEnvironment(
            "Optio__SystemPrompt",
            File.ReadAllText(
                Path.Combine(
                    builder.AppHostDirectory,
                    "..",
                    "..",
                    "..",
                    "optio",
                    "docs",
                    "system-prompt.example.txt"
                )
            )
        )
        .WithEnvironment("Optio__McpServices__0__Name", "custos")
        .WithEnvironment(
            "Optio__McpServices__0__Url",
            ReferenceExpression.Create($"{server.GetEndpoint("http")}/mcp")
        )
        .WithEnvironment("Optio__McpServices__0__ReadScope", "custos:read")
        .WithEnvironment("Optio__McpServices__0__WriteScope", "custos:write");
}

// The tunnel serves only an endpoint that a container references. This reference also gives the
// realm the host name of Optio as Keycloak sees it (aspire.dev.internal). Optio does not wait for
// Keycloak: Keycloak needs the address of Optio first, and Optio gets the metadata of Keycloak
// only on the first request that needs it.
keycloak.WithEnvironment(
    "CUSTOS_CIMD_HOST",
    optio.GetEndpoint("http").Property(EndpointProperty.Host)
);

builder.Build().Run();
