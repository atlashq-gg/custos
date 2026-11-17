// WithoutHttpsCertificate is an evaluation API. If Aspire renames it, the build fails here.
#pragma warning disable ASPIRECERTIFICATES001

var builder = DistributedApplication.CreateBuilder(args);

// The ports are fixed. In dev mode Keycloak writes the URL that the client used into the iss claim, and
// the server compares iss with Custos:Authority exactly. The server port is in its launch profile.
const int apiPort = 5080; // The same as in Custos.Server/Properties/launchSettings.json.
const int keycloakPort = 8180;

// Keycloak gets the Client ID Metadata Document from the server. Keycloak runs in a container, so it uses
// the Aspire container tunnel (ASPIRE_ENABLE_CONTAINER_TUNNEL in launchSettings.json) and its host name.
// The client ID is the URL that Keycloak gets, so the server announces the same URL.
const string tunnelHost = "aspire.dev.internal";
var clientId = $"http://{tunnelHost}:{apiPort}/custos/client-metadata.json";

// No volume: Keycloak imports the realm on every start. What the console changes lasts one run.
var keycloak = builder.AddKeycloak("keycloak", keycloakPort)
    .WithImageTag("26.8.0")
    .WithArgs("--features=cimd")
    .WithRealmImport(Path.Combine(builder.AppHostDirectory, "keycloak", "custos-realm.json"))
    .WithoutHttpsCertificate();

// The launch profile gives the port and the Development environment. appsettings.Development.json of the
// server holds the localhost URLs of the server and of the realm.
var api = builder.AddProject<Projects.Custos_Server>("api")
    .WithEnvironment("Custos__ClientId", clientId)
    .WithHttpHealthCheck("/health/alive");

// The tunnel serves only an endpoint that a container references. This reference also gives the realm
// the host name of the server as Keycloak sees it (aspire.dev.internal). The server does not wait for
// Keycloak: Keycloak needs the address of the server first, and the server gets the metadata of Keycloak
// only on the first request that needs it.
keycloak.WithEnvironment("CUSTOS_CIMD_HOST", api.GetEndpoint("http").Property(EndpointProperty.Host));

builder.Build().Run();
