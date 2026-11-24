// WithoutHttpsCertificate is an evaluation API. If Aspire renames it, the build fails here.
#pragma warning disable ASPIRECERTIFICATES001

var builder = DistributedApplication.CreateBuilder(args);

// The ports are fixed. In dev mode Keycloak writes the URL that the client used into the iss claim,
// and each service compares iss with its authority exactly. The service ports are in their launch
// profiles.
const int serverPort = 5080; // server/Custos.Server/Properties/launchSettings.json
const int optioPort = 5090; // Optio/Properties/launchSettings.json
const int keycloakPort = 8180;

// Keycloak gets the Client ID Metadata Document of Custos from optio. Keycloak runs in a
// container, so it uses the Aspire container tunnel (ASPIRE_ENABLE_CONTAINER_TUNNEL in
// launchSettings.json) and its host name. The client ID is the URL that Keycloak gets, so optio
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
// The realm file is in the server project, so that one realm file exists.
var keycloak = builder
    .AddKeycloak("keycloak", keycloakPort)
    .WithImageTag("26.8.2")
    .WithArgs("--features=cimd,resource-indicators")
    .WithEnvironment("CUSTOS_MCP_RESOURCE", serverResource)
    .WithEnvironment("OPTIO_CLIENT_SECRET", optioSecret)
    .WithRealmImport(
        Path.Combine(
            builder.AppHostDirectory,
            "..",
            "..",
            "server",
            "keycloak",
            "custos-realm.json"
        )
    )
    .WithoutHttpsCertificate();

// PostgreSQL 18, the version that the table definitions in issue #1 were tested on. No volume: the
// data lasts one run. The database name is CustosDbContext.ConnectionName of the server.
var postgres = builder.AddPostgres("postgres").WithImageTag("18");
var database = postgres.AddDatabase("custos");

// The database of optio, for the settings of the guilds. The name is OptioDbContext.ConnectionName.
var optioDatabase = postgres.AddDatabase("optio");

// The event broker (ADR 0007), with the users and the exchanges of the definitions file. The
// broker imports the file at the start, and then has only the users of the file. Thus the
// resource user is custos, so that the connection string of the server names a user that exists.
// The passwords of the file are for development only.
var events = builder
    .AddRabbitMQ(
        "events",
        builder.AddParameter("events-user", "custos"),
        builder.AddParameter("events-password", "custos", secret: true)
    )
    .WithImageTag("4.3-alpine")
    .WithBindMount(
        Path.Combine(builder.AppHostDirectory, "..", "rabbitmq", "definitions.json"),
        "/etc/rabbitmq/definitions.json",
        isReadOnly: true
    )
    .WithContainerFiles(
        "/etc/rabbitmq/conf.d",
        [
            new ContainerFile
            {
                Name = "20-definitions.conf",
                Contents = "load_definitions = /etc/rabbitmq/definitions.json\n",
            },
        ]
    );

// The launch profile gives the port and the Development environment. appsettings.Development.json
// of the server holds the localhost URLs of the realm and of the MCP endpoint.
var server = builder
    .AddProject<Projects.Custos_Server>("server")
    .WithEnvironment("Mcp__Enabled", "true")
    .WithReference(database)
    .WaitFor(database)
    // The server does not wait for the broker: an upload works without it.
    .WithReference(events)
    .WithHttpHealthCheck("/health/ready");

// The AppHost always registers the two parameters. Aspire asks for a value that is not set in the
// dashboard, and the user secrets can hold it:
//   dotnet user-secrets set Parameters:discord-token <bot token>
//   dotnet user-secrets set Parameters:discord-user-id <Discord user ID of the user dev>
// discord-user-id is for a token without the claim discord_id.
var discordUserId = builder.AddParameter("discord-user-id");
server
    .WithEnvironment("Discord__Token", builder.AddParameter("discord-token", secret: true))
    .WithEnvironment("Custos__DevelopmentDiscordUserId", discordUserId);

// appsettings.Development.json of optio holds the localhost URLs of optio, the realm, and the
// upload API of the server.
var optio = builder
    .AddProject<Projects.Optio>("optio")
    .WithEnvironment("Optio__CustosClientId", custosClientId)
    .WithEnvironment("Optio__OptioClientSecret", optioSecret)
    .WithReference(server)
    // optio reads the streams as the user optio of the definitions file, and does not wait for
    // the broker: its consumer connects again itself.
    .WithEnvironment(
        "ConnectionStrings__events",
        ReferenceExpression.Create(
            $"amqp://optio:optio@{events.GetEndpoint("tcp").Property(EndpointProperty.HostAndPort)}"
        )
    )
    // The bot of optio for the stream. It can be the bot of the server in development:
    //   dotnet user-secrets set Parameters:optio-discord-token <bot token>
    .WithEnvironment(
        "Optio__DiscordToken",
        builder.AddParameter("optio-discord-token", secret: true)
    )
    .WithEnvironment("Optio__DevelopmentDiscordUserId", discordUserId)
    .WithReference(optioDatabase)
    .WaitFor(optioDatabase)
    // The query: the key of the model, the example system prompt, and the MCP endpoint of the
    // server. Without the key, optio has no query_endpoint:
    //   dotnet user-secrets set Parameters:optio-model-key <key>
    .WithEnvironment("Optio__ModelApiKey", builder.AddParameter("optio-model-key", secret: true))
    .WithEnvironment(
        "Optio__SystemPrompt",
        File.ReadAllText(
            Path.Combine(builder.AppHostDirectory, "..", "docs", "system-prompt.example.txt")
        )
    )
    .WithEnvironment("Optio__McpServices__0__Name", "custos")
    .WithEnvironment("Optio__McpServices__0__Url", serverResource)
    .WithEnvironment("Optio__McpServices__0__ReadScope", "custos:read")
    .WithHttpHealthCheck("/health/ready");

// The tunnel serves only an endpoint that a container references. This reference also gives the
// realm the host name of optio as Keycloak sees it (aspire.dev.internal). optio does not wait for
// Keycloak: Keycloak needs the address of optio first, and optio gets the metadata of Keycloak
// only on the first request that needs it.
keycloak.WithEnvironment(
    "CUSTOS_CIMD_HOST",
    optio.GetEndpoint("http").Property(EndpointProperty.Host)
);

builder.Build().Run();
