using System.Net;
using System.Net.Http.Json;
using ClinicalSensorFusion.Server.Endpoints;
using ClinicalSensorFusion.Server.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace ClinicalSensorFusion.Server.Tests;

/// <summary>Accesso singolo con Google: spento senza configurazione, e solo per account associati a un utente.</summary>
public sealed class GoogleSignInTests
{
    private static ServerFixture WithGoogle() => new()
    {
        Settings =
        {
            ["Authentication:Google:ClientId"] = "test-client.apps.googleusercontent.com",
            ["Authentication:Google:ClientSecret"] = "test-secret",
        },
    };

    private static HttpClient NoRedirects(ServerFixture server) =>
        server.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/"), AllowAutoRedirect = false });

    [Fact]
    public async Task Without_configuration_google_is_off()
    {
        await using var server = new ServerFixture();
        var client = NoRedirects(server);
        var ct = TestContext.Current.CancellationToken;

        var providers = await client.GetFromJsonAsync<AuthProviders>("api/v1/auth/providers", ct);
        Assert.Equal(new AuthProviders(true, false), providers);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri("api/v1/auth/google", UriKind.Relative), ct)).StatusCode);
    }

    [Fact]
    public async Task With_configuration_the_login_goes_to_google_and_comes_back_here()
    {
        await using var server = WithGoogle();
        var client = NoRedirects(server);
        var ct = TestContext.Current.CancellationToken;

        Assert.True((await client.GetFromJsonAsync<AuthProviders>("api/v1/auth/providers", ct))!.Google);

        var start = await client.GetAsync(new Uri("api/v1/auth/google?returnUrl=%2Fpatients", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var google = start.Headers.Location!;
        Assert.Equal("accounts.google.com", google.Host);
        var query = System.Web.HttpUtility.ParseQueryString(google.Query);
        Assert.Equal("test-client.apps.googleusercontent.com", query["client_id"]);
        Assert.Equal("https://localhost/signin-google", query["redirect_uri"]);
        Assert.Contains("email", query["scope"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coming_back_without_a_google_result_opens_no_session()
    {
        await using var server = WithGoogle();
        var client = NoRedirects(server);
        var ct = TestContext.Current.CancellationToken;

        var complete = await client.GetAsync(new Uri("api/v1/auth/google/complete?returnUrl=%2F", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.Redirect, complete.StatusCode);
        Assert.Equal("/login?error=google", complete.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri("api/v1/auth/me", UriKind.Relative), ct)).StatusCode);
    }

    private static DashboardUserService Users(params DashboardUser[] users) =>
        new(new StaticOptions(new DashboardOptions { Users = [.. users] }));

    private sealed class StaticOptions(DashboardOptions value) : IOptionsMonitor<DashboardOptions>
    {
        public DashboardOptions CurrentValue => value;

        public DashboardOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<DashboardOptions, string?> listener) => null;
    }

    [Fact]
    public void Only_a_verified_and_listed_google_account_enters()
    {
        var anna = new DashboardUser { UserName = "anna", GoogleEmail = "Anna.Rossi@gmail.com" };
        var users = Users(anna, new DashboardUser { UserName = "demo", PasswordHash = DashboardUserService.HashPassword("x") });

        Assert.Same(anna, users.FindByGoogle(" anna.rossi@gmail.com ", emailVerified: true));
        Assert.Null(users.FindByGoogle("anna.rossi@gmail.com", emailVerified: false)); // Google non garantisce l'indirizzo
        Assert.Null(users.FindByGoogle("altro@gmail.com", emailVerified: true)); // account Google valido ma non associato
        Assert.Null(users.FindByGoogle(null, emailVerified: true));
    }

    [Fact]
    public void A_google_only_user_cannot_log_in_with_a_password()
    {
        var users = Users(new DashboardUser { UserName = "anna", GoogleEmail = "anna.rossi@gmail.com" });
        Assert.Null(users.Validate("anna", string.Empty));
        Assert.Null(users.Validate("anna", "qualunque"));
    }

    [Theory]
    [InlineData("/patients", "/patients")]
    [InlineData("/", "/")]
    [InlineData("//evil.example.org/", "/")] // indirizzo di un altro sito scritto come percorso
    [InlineData("/\\evil.example.org", "/")]
    [InlineData("https://evil.example.org/", "/")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    public void The_return_address_stays_on_this_site(string? returnUrl, string expected) =>
        Assert.Equal(expected, AuthEndpoints.LocalPath(returnUrl));
}
