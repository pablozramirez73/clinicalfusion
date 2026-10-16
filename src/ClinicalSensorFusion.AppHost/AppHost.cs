// Orchestrazione per lo sviluppo con .NET Aspire: dotnet run --project src/ClinicalSensorFusion.AppHost
// L'avvio classico (dotnet run sul server, scripts/run-server-lan.sh, docker compose) resta valido: qui si
// impostano solo variabili d'ambiente che il server legge già.
var builder = DistributedApplication.CreateBuilder(args);

// Segreti come parametri di Aspire, mai nel repository: user-secrets dell'AppHost (Parameters:<nome>),
// variabili Parameters__<nome>, oppure la richiesta della dashboard di Aspire al primo avvio.
// Si passano solo gli hash, come per l'avvio classico (vedi docs/server.md):
//   dotnet run --project src/ClinicalSensorFusion.Server -- hash-password <password>
//   dotnet run --project src/ClinicalSensorFusion.Server -- new-device-key <nome>
var dashboardUser = builder.AddParameter("dashboard-user", "demo");
var dashboardPasswordHash = builder.AddParameter("dashboard-password-hash", secret: true);
var deviceName = builder.AddParameter("device-name", "iphone-aspire");
var deviceKeySha256 = builder.AddParameter("device-key-sha256", secret: true);

// PostgreSQL in un container, con la porta sull'host scelta da Aspire: non si scontra con un PostgreSQL
// installato sul computer né con quello di docker compose (entrambi su 5432). La password la genera Aspire e la
// conserva negli user-secrets dell'AppHost; i dati restano nel volume tra un avvio e l'altro.
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("clinical-sensor-fusion-pgdata");
var clinical = postgres.AddDatabase("clinical");

// Profilo "aspire" (porta 5181): lascia libera la 5180 del profilo http e di scripts/run-server-lan.sh.
var server = builder.AddProject<Projects.ClinicalSensorFusion_Server>("server", launchProfileName: "aspire")
    .WithReference(clinical, connectionName: "Clinical")
    .WaitFor(clinical)
    .WithEnvironment("Dashboard__Users__0__UserName", dashboardUser)
    .WithEnvironment("Dashboard__Users__0__DisplayName", dashboardUser)
    .WithEnvironment("Dashboard__Users__0__PasswordHash", dashboardPasswordHash)
    .WithEnvironment("Ingest__Devices__0__Name", deviceName)
    .WithEnvironment("Ingest__Devices__0__KeySha256", deviceKeySha256)
    .WithHttpHealthCheck("/health/ready");

// Dashboard React con Vite e hot reload; inoltra /api, /health e /fhir al server (vite.config.ts, SERVER_URL).
builder.AddViteApp("dashboard", "../ClinicalSensorFusion.Dashboard")
    .WithEnvironment("SERVER_URL", server.GetEndpoint("http"))
    .WithReference(server)
    .WaitFor(server);

builder.Build().Run();
