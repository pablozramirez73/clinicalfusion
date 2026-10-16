using System.Net;
using System.Text;
using ClinicalSensorFusion.Server.Endpoints;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicalSensorFusion.Server.Tests;

/// <summary>L'app raggiunge Ollama solo attraverso il server, in HTTPS e con la chiave del dispositivo.</summary>
public sealed class AiProxyTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    /// <summary>Ollama finto: registra le richieste e risponde come farebbe quello vero.</summary>
    private sealed class FakeOllama : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            var json = request.RequestUri.AbsolutePath.EndsWith("tags", StringComparison.Ordinal)
                ? """{"models":[{"name":"llama3.2"}]}"""
                : "{\"message\":{\"content\":\"ciao\"},\"done\":false}\n{\"done\":true}\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/x-ndjson") };
        }
    }

    private (HttpClient Device, HttpClient Anonymous, FakeOllama Ollama) Start()
    {
        var ollama = new FakeOllama();
        var factory = server.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddHttpClient(AiProxyEndpoints.HttpClientName, c => c.BaseAddress = new Uri("http://ollama.test:11434/"))
                .ConfigurePrimaryHttpMessageHandler(() => ollama)));
        var device = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost/") });
        device.DefaultRequestHeaders.Add(Core.Sync.ClinicalSyncClient.ApiKeyHeader, ServerFixture.DeviceKey);
        var anonymous = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost/") });
        return (device, anonymous, ollama);
    }

    [Fact]
    public async Task Models_and_chat_are_forwarded_to_ollama()
    {
        var (device, _, ollama) = Start();
        var ct = TestContext.Current.CancellationToken;

        var tags = await device.GetAsync(new Uri("api/v1/ai/api/tags", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, tags.StatusCode);
        Assert.Contains("llama3.2", await tags.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        using var prompt = new StringContent("""{"model":"llama3.2","messages":[]}""", Encoding.UTF8, "application/json");
        var chat = await device.PostAsync(new Uri("api/v1/ai/api/chat", UriKind.Relative), prompt, ct);
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        Assert.Contains("\"done\":true", await chat.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        Assert.Equal(["/api/tags", "/api/chat"], ollama.Requests.Select(r => r.Path));
        Assert.Contains("llama3.2", ollama.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_device_key_nothing_reaches_ollama()
    {
        var (_, anonymous, ollama) = Start();
        var response = await anonymous.GetAsync(new Uri("api/v1/ai/api/tags", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(ollama.Requests);
    }

    [Theory]
    [InlineData("api/v1/ai/api/pull")] // scaricare modelli: non è una chiamata dell'app
    [InlineData("api/v1/ai/api/delete")]
    public async Task Only_the_calls_the_app_uses_are_forwarded(string path)
    {
        var (device, _, ollama) = Start();
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await device.PostAsync(new Uri(path, UriKind.Relative), body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(ollama.Requests);
    }
}
