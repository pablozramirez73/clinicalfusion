using System.Net;
using System.Net.Http.Json;
using ClinicalSensorFusion.Core.Sync;
using ClinicalSensorFusion.Server.Endpoints;

namespace ClinicalSensorFusion.Server.Tests;

public sealed class SecurityTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("api/v1/patients")]
    [InlineData("api/v1/dashboard/overview")]
    [InlineData("api/v1/sessions")]
    public async Task Dashboard_api_requires_login(string url)
    {
        var response = await server.CreateHttpsClient().GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Device_key_cannot_read_clinical_data()
    {
        var response = await server.CreateDeviceClient().GetAsync("api/v1/patients", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_rejects_missing_or_wrong_key()
    {
        var body = new ChunkBatch([new ChunkUpload(Core.Sensors.SensorKind.Motion, 0, 0, 1, 1, [1])]);
        var anonymous = await server.CreateHttpsClient().PostAsJsonAsync($"api/v1/ingest/sessions/{Guid.NewGuid():N}/chunks", body, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var wrong = server.CreateHttpsClient();
        wrong.DefaultRequestHeaders.Add(ClinicalSyncClient.ApiKeyHeader, "chiave-sbagliata");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.PostAsJsonAsync($"api/v1/ingest/sessions/{Guid.NewGuid():N}/chunks", body, Ct)).StatusCode);
    }

    [Fact]
    public async Task Login_with_wrong_password_fails_and_right_one_sets_a_secure_http_only_cookie()
    {
        var client = server.CreateHttpsClient();
        var bad = await client.PostAsJsonAsync("api/v1/auth/login", new LoginRequest(ServerFixture.UserName, "sbagliata"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var ok = await client.PostAsJsonAsync("api/v1/auth/login", new LoginRequest(ServerFixture.UserName, ServerFixture.Password), Ct);
        ok.EnsureSuccessStatusCode();
        var cookie = Assert.Single(ok.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        var me = await client.GetFromJsonAsync<CurrentUser>("api/v1/auth/me", Ct);
        Assert.Equal("Dott.ssa Test", me!.DisplayName);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_unknown_api_routes_are_404()
    {
        var response = await server.CreateHttpsClient().GetAsync("api/v1/non-esiste", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_endpoint_checks_the_database()
    {
        var response = await server.CreateHttpsClient().GetAsync("health", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
