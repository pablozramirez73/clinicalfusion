using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicalSensorFusion.Server.Endpoints;

namespace ClinicalSensorFusion.Server.Tests;

/// <summary>Stato del servizio, metriche, token JWT e documentazione delle API.</summary>
public sealed class ObservabilityTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> CreateBearerClientAsync()
    {
        var client = server.CreateHttpsClient();
        var response = await client.PostAsJsonAsync("api/v1/auth/token", new LoginRequest(ServerFixture.UserName, ServerFixture.Password), Ct);
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
        Assert.Equal("Bearer", token.TokenType);
        Assert.Equal(30 * 60, token.ExpiresIn);
        Assert.DoesNotContain(response.Headers, h => h.Key == "Set-Cookie");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    [Theory]
    [InlineData("health", true)]
    [InlineData("health/ready", true)]
    [InlineData("health/live", false)]
    public async Task Anonymous_probes_report_status_without_details(string path, bool withChecks)
    {
        var response = await server.CreateHttpsClient().GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        var checks = body.GetProperty("checks").EnumerateObject().Select(c => c.Name).ToArray();
        Assert.Equal(withChecks ? ["database", "audio-storage"] : [], checks);
    }

    [Fact]
    public async Task System_pages_need_a_clinician_and_show_checks_and_request_metrics()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.CreateHttpsClient().GetAsync("api/v1/system/health", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.CreateDeviceClient().GetAsync("api/v1/system/metrics", Ct)).StatusCode);

        var clinician = await server.CreateClinicianClientAsync();
        var health = (await clinician.GetFromJsonAsync<SystemHealth>("api/v1/system/health", Ct))!;
        Assert.Equal("Healthy", health.Status);
        Assert.True(health.UptimeSeconds > 0);
        Assert.Contains(health.Checks, c => c.Name == "database" && c.Status == "Healthy");
        var storage = Assert.Single(health.Checks, c => c.Name == "audio-storage");
        Assert.True(((JsonElement)storage.Data["freeBytes"]).GetInt64() > 0);

        var metrics = (await clinician.GetFromJsonAsync<SystemMetrics>("api/v1/system/metrics", Ct))!;
        // Segnali d'oro: traffico, errori e latenza per rotta arrivano da ASP.NET Core…
        Assert.Contains(metrics.Points, p => p.Name == "http.server.request.duration" && p.Kind == "histogram"
            && p.Tags.Contains("http.route=/api/v1/system/health", StringComparison.Ordinal) && p.Count >= 1);
        // …e quelli di dominio dal Core.
        Assert.Contains(metrics.Points, p => p.Name == "csf.auth.logins" && p.Tags == "method=cookie,outcome=success" && p.Sum >= 1);
    }

    [Fact]
    public async Task Api_responses_carry_a_trace_id()
    {
        var response = await server.CreateHttpsClient().GetAsync("api/v1/patients", Ct);
        Assert.NotEmpty(Assert.Single(response.Headers.GetValues("X-Trace-Id")));
    }

    [Fact]
    public async Task A_jwt_gives_the_same_access_as_the_dashboard_cookie_and_nothing_more()
    {
        var bearer = await CreateBearerClientAsync();
        var me = (await bearer.GetFromJsonAsync<CurrentUser>("api/v1/auth/me", Ct))!;
        Assert.Equal(ServerFixture.UserName, me.UserName);
        Assert.Equal("Dott.ssa Test", me.DisplayName);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("api/v1/patients", Ct)).StatusCode);

        // Un token da clinico non apre le rotte dei dispositivi.
        var ingest = await bearer.PostAsJsonAsync($"api/v1/ingest/sessions/{Guid.NewGuid():N}/chunks", new { chunks = Array.Empty<object>() }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, ingest.StatusCode);

        var audit = (await bearer.GetFromJsonAsync<PagedResult<AuditItem>>("api/v1/audit?action=auth.token", Ct))!;
        Assert.Contains(audit.Items, a => a.Actor == ServerFixture.UserName);
    }

    [Fact]
    public async Task Wrong_credentials_and_forged_tokens_are_refused()
    {
        var client = server.CreateHttpsClient();
        var bad = await client.PostAsJsonAsync("api/v1/auth/token", new LoginRequest(ServerFixture.UserName, "sbagliata"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var bearer = await CreateBearerClientAsync();
        var token = bearer.DefaultRequestHeaders.Authorization!.Parameter!;
        // Firma alterata, e token "senza firma" (alg none) con gli stessi dati.
        var parts = token.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.{new string(parts[2].Reverse().ToArray())}";
        var unsigned = $"{Convert.ToBase64String("{\"alg\":\"none\",\"typ\":\"JWT\"}"u8.ToArray()).TrimEnd('=')}.{parts[1]}.";
        foreach (var forged in (string[])[tampered, unsigned, "non-un-token"])
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/v1/patients", Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task Api_docs_are_off_unless_enabled()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await server.CreateHttpsClient().GetAsync("openapi/v1.json", Ct)).StatusCode);
        var swagger = await server.CreateHttpsClient().GetAsync("swagger/index.html", Ct);
        Assert.DoesNotContain("swagger-ui", await swagger.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Swagger_ui_and_openapi_describe_jwt_and_device_key_security()
    {
        await using var docs = new ServerFixture { Settings = { ["ApiDocs:Enabled"] = "true" } };
        var client = docs.CreateHttpsClient();

        var ui = await client.GetAsync("swagger/index.html", Ct);
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("swagger-ui", await ui.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
        Assert.False(ui.Headers.Contains("Content-Security-Policy"));

        var document = await client.GetFromJsonAsync<JsonElement>("openapi/v1.json", Ct);
        var schemes = document.GetProperty("components").GetProperty("securitySchemes");
        Assert.Equal("bearer", schemes.GetProperty("Bearer").GetProperty("scheme").GetString());
        Assert.Equal("JWT", schemes.GetProperty("Bearer").GetProperty("bearerFormat").GetString());
        Assert.Equal("X-Api-Key", schemes.GetProperty("ApiKey").GetProperty("name").GetString());

        var paths = document.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/v1/patients").GetProperty("get").GetProperty("security")[0].TryGetProperty("Bearer", out _));
        Assert.True(paths.GetProperty("/api/v1/ingest/sessions/{id}").GetProperty("put").GetProperty("security")[0].TryGetProperty("ApiKey", out _));
        Assert.False(paths.GetProperty("/api/v1/auth/token").GetProperty("post").TryGetProperty("security", out _));
    }
}
