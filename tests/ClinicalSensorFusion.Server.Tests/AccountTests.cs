using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using ClinicalSensorFusion.Server.Data;
using ClinicalSensorFusion.Server.Endpoints;
using ClinicalSensorFusion.Server.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicalSensorFusion.Server.Tests;

/// <summary>Registrazione con Google e approvazione: chi si registra non vede nulla finché non è approvato.</summary>
public sealed class AccountTests(AccountTests.Fixture fixture) : IClassFixture<AccountTests.Fixture>
{
    /// <summary>Server condiviso con il limite di accessi alzato: questi test fanno molti login e registrazioni.</summary>
    public sealed class Fixture : IAsyncDisposable
    {
        public ServerFixture Server { get; } = new() { Settings = { ["RateLimits:LoginPerMinute"] = "1000" } };

        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }

    private readonly ServerFixture server = fixture.Server;

    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    private async Task<GoogleSignInResult> GoogleAsync(string email, bool verified = true, string? subject = null)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AccountService>()
            .SignInWithGoogleAsync(email, verified, subject ?? "sub-" + email, "Persona di prova", Anonymous, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> AccountIdAsync(string email)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<ClinicalDbContext>().Accounts.Single(a => a.Email == email).Id;
    }

    [Fact]
    public async Task First_google_sign_in_creates_a_request_that_sees_nothing_until_approved()
    {
        var email = $"nuovo-{Guid.NewGuid():N}@gmail.com";

        Assert.Equal(GoogleSignInOutcome.Requested, (await GoogleAsync(email)).Outcome);
        Assert.Equal(GoogleSignInOutcome.Pending, (await GoogleAsync(email)).Outcome); // nessuna seconda richiesta, nessuna sessione

        var admin = await server.CreateClinicianClientAsync();
        var ct = TestContext.Current.CancellationToken;
        var list = await admin.GetFromJsonAsync<List<AccountItem>>("api/v1/accounts/", ct);
        var request = Assert.Single(list!, a => a.Email == email);
        Assert.Equal(AccountStatus.Pending, request.Status);

        var approved = await admin.PostAsJsonAsync($"api/v1/accounts/{request.Id}/approve", new AccountDecision(), ct);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var signedIn = await GoogleAsync(email);
        Assert.Equal(GoogleSignInOutcome.SignedIn, signedIn.Outcome);
        Assert.True(signedIn.Principal!.IsInRole(Roles.Clinician));
        Assert.False(signedIn.Principal.IsInRole(Roles.Admin));
        Assert.Equal(request.Id.ToString(), signedIn.Principal.FindFirstValue(AccountService.AccountIdClaim));
    }

    [Fact]
    public async Task Rejected_requests_and_unverified_addresses_never_enter()
    {
        var email = $"rifiutato-{Guid.NewGuid():N}@gmail.com";
        await GoogleAsync(email);
        var admin = await server.CreateClinicianClientAsync();
        var ct = TestContext.Current.CancellationToken;
        await admin.PostAsync(new Uri($"api/v1/accounts/{await AccountIdAsync(email)}/reject", UriKind.Relative), null, ct);

        Assert.Equal(GoogleSignInOutcome.Denied, (await GoogleAsync(email)).Outcome);

        var unverified = $"non-verificato-{Guid.NewGuid():N}@gmail.com";
        Assert.Equal(GoogleSignInOutcome.Denied, (await GoogleAsync(unverified, verified: false)).Outcome);
        await using var scope = server.Services.CreateAsyncScope();
        Assert.DoesNotContain(scope.ServiceProvider.GetRequiredService<ClinicalDbContext>().Accounts, a => a.Email == unverified);
    }

    [Fact]
    public async Task Only_administrators_decide()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.CreateHttpsClient().GetAsync(new Uri("api/v1/accounts/", UriKind.Relative), ct)).StatusCode);

        // Un clinico approvato ma non amministratore entra nella dashboard, non nella gestione degli accessi.
        var email = $"clinico-{Guid.NewGuid():N}@gmail.com";
        await GoogleAsync(email);
        var admin = await server.CreateClinicianClientAsync();
        await admin.PostAsJsonAsync($"api/v1/accounts/{await AccountIdAsync(email)}/approve", new AccountDecision(), ct);
        var (client, _) = await SignedInAsAsync(email);

        var me = await client.GetFromJsonAsync<CurrentUser>("api/v1/auth/me", ct);
        Assert.False(me!.IsAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri("api/v1/accounts/", UriKind.Relative), ct)).StatusCode);
    }

    [Fact]
    public async Task Revoking_access_ends_the_open_session_at_the_next_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = $"revocato-{Guid.NewGuid():N}@gmail.com";
        await GoogleAsync(email);
        var admin = await server.CreateClinicianClientAsync();
        var id = await AccountIdAsync(email);
        await admin.PostAsJsonAsync($"api/v1/accounts/{id}/approve", new AccountDecision(), ct);
        var (client, factory) = await SignedInAsAsync(email);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("api/v1/auth/me", UriKind.Relative), ct)).StatusCode);

        // L'amministratore usa lo stesso server (stesso database) della sessione aperta.
        var sameServerAdmin = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/") });
        await sameServerAdmin.PostAsJsonAsync("api/v1/auth/login", new LoginRequest(ServerFixture.UserName, ServerFixture.Password), ct);
        await sameServerAdmin.PostAsync(new Uri($"api/v1/accounts/{id}/disable", UriKind.Relative), null, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri("api/v1/auth/me", UriKind.Relative), ct)).StatusCode);
    }

    [Fact]
    public async Task Sign_up_with_email_waits_for_approval_then_signs_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = $"registrato-{Guid.NewGuid():N}@example.org";
        const string password = "una-password-lunga-123";
        var anonymous = server.CreateHttpsClient();

        var register = await anonymous.PostAsJsonAsync("api/v1/auth/register", new RegisterRequest(email, "Dott. Prova", password), ct);
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);

        // In attesa: la password è giusta ma non si entra, e la risposta lo dice.
        var pending = await anonymous.PostAsJsonAsync("api/v1/auth/login", new LoginRequest(email, password), ct);
        Assert.Equal(HttpStatusCode.Forbidden, pending.StatusCode);
        Assert.Contains(AuthEndpoints.PendingProblem, await pending.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("api/v1/auth/login", new LoginRequest(email, "sbagliata-sbagliata"), ct)).StatusCode);

        var admin = await server.CreateClinicianClientAsync();
        await admin.PostAsJsonAsync($"api/v1/accounts/{await AccountIdAsync(email)}/approve", new AccountDecision(), ct);

        var client = server.CreateHttpsClient();
        var login = await client.PostAsJsonAsync("api/v1/auth/login", new LoginRequest(email.ToUpperInvariant(), password), ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var me = await client.GetFromJsonAsync<CurrentUser>("api/v1/auth/me", ct);
        Assert.Equal(email, me!.UserName);
        Assert.Equal("Dott. Prova", me.DisplayName);
        Assert.False(me.IsAdmin);
    }

    [Fact]
    public async Task Sign_up_rejects_bad_input_and_does_not_reveal_existing_addresses()
    {
        var ct = TestContext.Current.CancellationToken;
        var anonymous = server.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await anonymous.PostAsJsonAsync("api/v1/auth/register", new RegisterRequest("non-un-indirizzo", null, "una-password-lunga-123"), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await anonymous.PostAsJsonAsync("api/v1/auth/register", new RegisterRequest("corta@example.org", null, "corta"), ct)).StatusCode);

        var email = $"doppio-{Guid.NewGuid():N}@example.org";
        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.PostAsJsonAsync("api/v1/auth/register", new RegisterRequest(email, null, "prima-password-lunga"), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.PostAsJsonAsync("api/v1/auth/register", new RegisterRequest(email, null, "seconda-password-lunga"), ct)).StatusCode);

        // Il secondo tentativo non sostituisce la password del primo.
        await using var scope = server.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        Assert.Equal(GoogleSignInOutcome.Pending, (await accounts.SignInWithPasswordAsync(email, "prima-password-lunga", ct)).Outcome);
        Assert.Equal(GoogleSignInOutcome.Denied, (await accounts.SignInWithPasswordAsync(email, "seconda-password-lunga", ct)).Outcome);
    }

    /// <summary>
    /// Sessione di un account approvato come la aprirebbe il ritorno da Google (che nei test non si può
    /// percorrere): una rotta di prova firma il cookie con il principal prodotto da <see cref="AccountService"/>.
    /// </summary>
    private async Task<(HttpClient Client, WebApplicationFactory<Program> Factory)> SignedInAsAsync(string email)
    {
        var factory = server.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(new TestSignIn())));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/") });
        var response = await client.PostAsync(new Uri("test/sign-in?email=" + Uri.EscapeDataString(email), UriKind.Relative), null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (client, factory);
    }

    private sealed class TestSignIn : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // Non app.Map: sposterebbe PathBase e il cookie varrebbe solo per /test/sign-in.
            app.Use(async (http, nextMiddleware) =>
            {
                if (!http.Request.Path.Equals("/test/sign-in", StringComparison.Ordinal))
                {
                    await nextMiddleware(http);
                    return;
                }

                var result = await http.RequestServices.GetRequiredService<AccountService>()
                    .SignInWithGoogleAsync(http.Request.Query["email"], true, "sub-" + http.Request.Query["email"], null, Anonymous);
                if (result.Outcome != GoogleSignInOutcome.SignedIn)
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, result.Principal!);
            });
            next(app);
        };
    }
}
