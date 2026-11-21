// WithoutHttpsCertificate is an evaluation API. If Aspire renames it, the build fails here.
#pragma warning disable ASPIRECERTIFICATES001

var builder = DistributedApplication.CreateBuilder(args);

// The ports are fixed. In dev mode Keycloak writes the URL that the client used into the iss claim,
// and the server compares iss with Custos:Authority exactly. The server port is in its launch
// profile.
const int apiPort = 5080; // The same as in Custos.Server/Properties/launchSettings.json.
const int keycloakPort = 8180;

// Keycloak gets the Client ID Metadata Document from the server. Keycloak runs in a container, so
// it uses the Aspire container tunnel (ASPIRE_ENABLE_CONTAINER_TUNNEL in launchSettings.json) and
// its host name. The client ID is the URL that Keycloak gets, so the server announces the same URL.
const string tunnelHost = "aspire.dev.internal";
var clientId = $"http://{tunnelHost}:{apiPort}/.well-known/custos-client";

// The MCP endpoint, as a harness on this machine reaches it. A token for MCP has it in aud. The
// server maps the endpoint only with the Discord connection on.
var mcpResource = $"http://localhost:{apiPort}/mcp";

// No volume: Keycloak imports the realm on every start. What the console changes lasts one run.
var keycloak = builder
    .AddKeycloak("keycloak", keycloakPort)
    .WithImageTag("26.8.2")
    .WithArgs("--features=cimd,resource-indicators")
    .WithEnvironment("CUSTOS_MCP_RESOURCE", mcpResource)
    .WithRealmImport(Path.Combine(builder.AppHostDirectory, "keycloak", "custos-realm.json"))
    .WithoutHttpsCertificate();

// PostgreSQL 18, the version that the table definitions in issue #1 were tested on. No volume: the
// data lasts one run. The database name is CustosDbContext.ConnectionName of the server.
var database = builder.AddPostgres("postgres").WithImageTag("18").AddDatabase("custos");

// The launch profile gives the port and the Development environment. appsettings.Development.json
// of the server holds the localhost URLs of the server and of the realm.
var api = builder
    .AddProject<Projects.Custos_Server>("api")
    .WithEnvironment("Custos__ClientId", clientId)
    .WithEnvironment("Mcp__Enabled", "true")
    .WithEnvironment("Mcp__Resource", mcpResource)
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health/ready");

// The two parameters are optional, and the AppHost always registers them, so that the dashboard
// lists them. A parameter without a value sets no environment variable, so that a variable of the
// same name in the environment of the AppHost (Discord__Token) reaches the server. Set the values
// in the user secrets of this project:
//   dotnet user-secrets set Parameters:discord-token <bot token>
//   dotnet user-secrets set Parameters:discord-user-id <Discord user ID of the user dev>
// discord-user-id is for a token without the claim discord_id.
WithOptionalParameter("Discord__Token", "discord-token", secret: true);
WithOptionalParameter("Custos__DevelopmentDiscordUserId", "discord-user-id");

// The tunnel serves only an endpoint that a container references. This reference also gives the
// realm the host name of the server as Keycloak sees it (aspire.dev.internal). The server does not
// wait for Keycloak: Keycloak needs the address of the server first, and the server gets the
// metadata of Keycloak only on the first request that needs it.
keycloak.WithEnvironment(
    "CUSTOS_CIMD_HOST",
    api.GetEndpoint("http").Property(EndpointProperty.Host)
);

builder.Build().Run();

// AddParameter fails for a key that is not in the configuration. This one gives an empty value.
void WithOptionalParameter(string variable, string name, bool secret = false)
{
    var key = $"Parameters:{name}";
    var parameter = builder.AddParameter(
        name,
        () => builder.Configuration[key] ?? string.Empty,
        secret: secret
    );
    if (!string.IsNullOrEmpty(builder.Configuration[key]))
        api.WithEnvironment(variable, parameter);
}
