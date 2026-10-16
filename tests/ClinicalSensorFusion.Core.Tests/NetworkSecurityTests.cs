using System.Net;
using ClinicalSensorFusion.Core.Ai;
using ClinicalSensorFusion.Core.Security;

namespace ClinicalSensorFusion.Core.Tests;

/// <summary>Credenziali solo verso il server configurato e niente HTTP in chiaro fuori dalla rete locale.</summary>
public class NetworkSecurityTests
{
    private const string Header = "X-Api-Key";

    /// <summary>Registra le richieste che arrivano in fondo alla catena e risponde con lo stato indicato.</summary>
    private sealed class Recorder(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = new HttpResponseMessage(status) { RequestMessage = request };
            if ((int)status is >= 300 and < 400)
            {
                response.Headers.Location = new Uri("https://login.example.org/");
            }

            return Task.FromResult(response);
        }
    }

    private static readonly ScopedCredentials Key =
        new(new Uri("https://clinica.example.org/"), [new(Header, "segreto")]);

    private static (HttpClient Client, Recorder Inner) Client(ScopedCredentials? credentials)
    {
        var inner = new Recorder();
        var handler = new ScopedCredentialsHandler(_ => Task.FromResult(credentials)) { InnerHandler = inner };
        return (new HttpClient(handler), inner);
    }

    [Theory]
    [InlineData("https://clinica.example.org/api/v1/ingest/sessions/1", true)]
    [InlineData("https://CLINICA.example.org/", true)]
    [InlineData("https://clinica.example.org:443/x", true)]
    [InlineData("http://clinica.example.org/", false)] // schema diverso
    [InlineData("https://clinica.example.org:8443/", false)] // porta diversa
    [InlineData("https://altro.example.org/", false)]
    [InlineData("https://clinica.example.org.attacker.net/", false)]
    public void Credentials_cover_only_the_configured_origin(string url, bool covered) =>
        Assert.Equal(covered, Key.Covers(new Uri(url)));

    [Fact]
    public async Task Key_goes_only_to_the_configured_server()
    {
        var (client, inner) = Client(Key);
        var ct = TestContext.Current.CancellationToken;

        await client.GetAsync(new Uri("https://clinica.example.org/api/v1/health"), ct);
        await client.GetAsync(new Uri("https://altro.example.org/api/v1/health"), ct);

        Assert.Equal(["segreto"], inner.Requests[0].Headers.GetValues(Header));
        Assert.False(inner.Requests[1].Headers.Contains(Header));
    }

    [Fact]
    public async Task Without_credentials_nothing_is_added()
    {
        var (client, inner) = Client(null);
        await client.GetAsync(new Uri("https://clinica.example.org/"), TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(inner.Requests).Headers.Contains(Header));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.204", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("mac-studio", true)]
    [InlineData("mac-studio.local", true)]
    [InlineData("[fd00::1]", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("example.org", false)]
    public void Local_hosts_are_recognised(string host, bool local) =>
        Assert.Equal(local, LocalNetwork.IsLocalHost(host));

    [Theory]
    [InlineData("http://192.168.1.204:5180/", true)]
    [InlineData("https://clinica.example.org/", true)]
    [InlineData("http://8.8.8.8/", false)] // un IP pubblico scritto per esteso: ATS di iOS non lo copre
    [InlineData("http://clinica.example.org/", false)]
    public async Task Cleartext_is_refused_outside_the_local_network(string url, bool allowed)
    {
        var inner = new Recorder();
        using var client = new HttpClient(new LocalCleartextHandler { InnerHandler = inner });
        var send = () => client.GetAsync(new Uri(url), TestContext.Current.CancellationToken);

        if (allowed)
        {
            await send();
            Assert.Single(inner.Requests);
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(send);
            Assert.Empty(inner.Requests);
        }
    }

    [Fact]
    public async Task A_redirect_from_ollama_is_reported_not_followed()
    {
        var inner = new Recorder(HttpStatusCode.Found);
        var client = new OllamaClient(new HttpClient(inner));

        var error = await Assert.ThrowsAsync<OllamaException>(() =>
            client.GetModelsAsync(new Uri("https://ai.example.org/"), TestContext.Current.CancellationToken));

        Assert.Contains("Cloudflare", error.Message, StringComparison.Ordinal);
        Assert.Single(inner.Requests);
    }
}
