using System.Net.Http.Json;
using ClinicalSensorFusion.Core.Sync;
using ClinicalSensorFusion.Server.Endpoints;
using ClinicalSensorFusion.Server.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace ClinicalSensorFusion.Server.Tests;

/// <summary>Server in memoria con database PostgreSQL dedicato e temporaneo.</summary>
public sealed class ServerFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string DeviceKey = "test-device-key-0123456789";
    public const string OtherDeviceKey = "test-device-key-altro-telefono";
    public const string UserName = "dottoressa";
    /// <summary>Generata a ogni esecuzione: nessuna credenziale fissa nel repository.</summary>
    public static readonly string Password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));

    private static readonly string PasswordHash = DashboardUserService.HashPassword(Password);

    private readonly string _database = "csf_test_" + Guid.NewGuid().ToString("N")[..12];

    public string AudioRoot { get; } = Path.Combine(Path.GetTempPath(), "csf-audio-" + Guid.NewGuid().ToString("N"));

    public bool SeedDemo { get; init; }

    /// <summary>Impostazioni aggiuntive (es. limiti di frequenza ridotti).</summary>
    public Dictionary<string, string> Settings { get; init; } = [];

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("CSF_TEST_POSTGRES") ?? "Host=localhost;Username=postgres";

    public string ConnectionString => new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = _database }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Clinical", ConnectionString);
        builder.UseSetting("AudioStorage:Root", AudioRoot);
        builder.UseSetting("Demo:Seed", SeedDemo ? "true" : "false");
        builder.UseSetting("Ingest:Devices:0:Name", "iphone-test");
        builder.UseSetting("Ingest:Devices:0:KeySha256", ApiKeyAuthenticationHandler.Hash(DeviceKey));
        builder.UseSetting("Ingest:Devices:1:Name", "android-test");
        builder.UseSetting("Ingest:Devices:1:KeySha256", ApiKeyAuthenticationHandler.Hash(OtherDeviceKey));
        builder.UseSetting("Dashboard:Users:0:UserName", UserName);
        builder.UseSetting("Dashboard:Users:0:DisplayName", "Dott.ssa Test");
        builder.UseSetting("Dashboard:Users:0:PasswordHash", PasswordHash);
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
    }

    /// <summary>Client HTTPS (i cookie di sessione sono Secure fuori da Development).</summary>
    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/") });

    public HttpClient CreateDeviceClient(string key = DeviceKey)
    {
        var client = CreateHttpsClient();
        client.DefaultRequestHeaders.Add(ClinicalSyncClient.ApiKeyHeader, key);
        return client;
    }

    public async Task<HttpClient> CreateClinicianClientAsync()
    {
        var client = CreateHttpsClient();
        var response = await client.PostAsJsonAsync("api/v1/auth/login", new LoginRequest(UserName, Password));
        response.EnsureSuccessStatusCode();
        return client;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await cmd.ExecuteNonQueryAsync();
        if (Directory.Exists(AudioRoot))
        {
            Directory.Delete(AudioRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
