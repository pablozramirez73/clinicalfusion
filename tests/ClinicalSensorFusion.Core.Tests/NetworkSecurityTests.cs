using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClinicalSensorFusion.Core.Ai;
using ClinicalSensorFusion.Core.Security;

namespace ClinicalSensorFusion.Core.Tests;

/// <summary>Credenziali solo verso il server configurato, solo HTTPS, certificato del server riconosciuto dall'impronta.</summary>
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
    [InlineData("https://clinica.example.org/", true)]
    [InlineData("https://192.168.1.204:5180/", true)]
    [InlineData("http://192.168.1.204:5180/", false)] // anche in rete locale: niente più chiaro
    [InlineData("http://localhost:11434/", false)]
    [InlineData("http://8.8.8.8/", false)]
    public async Task Only_https_leaves_the_app(string url, bool allowed)
    {
        var inner = new Recorder();
        using var client = new HttpClient(new HttpsOnlyHandler { InnerHandler = inner });
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

    private static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=SensorFusion Clinic (rete locale)", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static readonly Uri Server = new("https://192.168.1.204:5180/");

    [Fact]
    public void A_self_signed_certificate_is_accepted_only_with_its_pin()
    {
        using var real = SelfSigned();
        using var impostor = SelfSigned();
        var pinned = new PinnedServer(Server, CertificatePin.Compute(real));
        var request = new Uri(Server, "api/v1/ingest/sessions/1");

        // Autofirmato: per il sistema è un errore di catena, ma la chiave è quella attesa.
        Assert.True(CertificatePin.Validate(request, real, SslPolicyErrors.RemoteCertificateChainErrors, pinned));
        // Un altro certificato autofirmato per lo stesso indirizzo (chi si mette in mezzo): rifiutato.
        Assert.False(CertificatePin.Validate(request, impostor, SslPolicyErrors.RemoteCertificateChainErrors, pinned));
        // L'impronta vale anche quando il sistema non trova errori: conta la chiave, non l'autorità.
        Assert.False(CertificatePin.Validate(request, impostor, SslPolicyErrors.None, pinned));
        Assert.False(CertificatePin.Validate(request, null, SslPolicyErrors.RemoteCertificateNotAvailable, pinned));
    }

    [Fact]
    public void Other_hosts_and_unpinned_servers_use_the_system_check()
    {
        using var certificate = SelfSigned();
        var pinned = new PinnedServer(Server, CertificatePin.Compute(certificate));
        var other = new Uri("https://ai.example.org/api/tags");

        Assert.True(CertificatePin.Validate(other, certificate, SslPolicyErrors.None, pinned));
        Assert.False(CertificatePin.Validate(other, certificate, SslPolicyErrors.RemoteCertificateChainErrors, pinned));
        Assert.False(CertificatePin.Validate(Server, certificate, SslPolicyErrors.RemoteCertificateChainErrors, null));
    }

    [Fact]
    public void Pins_are_read_as_the_server_script_prints_them()
    {
        using var certificate = SelfSigned();
        var pin = CertificatePin.Compute(certificate);

        Assert.True(CertificatePin.IsWellFormed(pin));
        Assert.Equal(pin, CertificatePin.Normalize($"  sha256/{pin[..10]} {pin[10..]} "));
        Assert.True(CertificatePin.Validate(Server, certificate, SslPolicyErrors.RemoteCertificateChainErrors, new PinnedServer(Server, "sha256/" + pin)));
        Assert.False(CertificatePin.IsWellFormed("non è un'impronta"));
        Assert.False(CertificatePin.IsWellFormed(Convert.ToBase64String(new byte[20]))); // SHA-1, non SHA-256
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
