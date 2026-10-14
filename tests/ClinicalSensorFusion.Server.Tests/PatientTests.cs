using System.Net;
using System.Net.Http.Json;
using ClinicalSensorFusion.Server.Data;
using ClinicalSensorFusion.Server.Endpoints;

namespace ClinicalSensorFusion.Server.Tests;

public sealed class PatientTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PatientInput Input(string code, uint? version = null) => new(
        code, "Mario", "Rossi", new DateOnly(1950, 5, 4), Sex.Male, "RSSMRA50E04H501Z", "+39 333 1234567",
        "mario.rossi@example.org", "Via Roma 1, Milano", "Artrosi ginocchio", null, version);

    [Fact]
    public async Task Crud_search_and_normalized_code()
    {
        var client = await server.CreateClinicianClientAsync();
        var created = await client.PostAsJsonAsync("api/v1/patients", Input(" pz-100 "), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var patient = (await created.Content.ReadFromJsonAsync<PatientDetail>(Ct))!;
        Assert.Equal("PZ-100", patient.Code);
        Assert.False(patient.IsPlaceholder);

        var duplicate = await client.PostAsJsonAsync("api/v1/patients", Input("PZ-100"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var page = await client.GetFromJsonAsync<PagedResult<PatientListItem>>("api/v1/patients?search=ross", Ct);
        Assert.Contains(page!.Items, p => p.Id == patient.Id);

        var updated = await client.PutAsJsonAsync($"api/v1/patients/{patient.Id}", Input("PZ-100", patient.Version) with { Notes = "Controllo a 3 mesi" }, Ct);
        updated.EnsureSuccessStatusCode();
        var after = (await updated.Content.ReadFromJsonAsync<PatientDetail>(Ct))!;
        Assert.Equal("Controllo a 3 mesi", after.Notes);
        Assert.NotEqual(patient.Version, after.Version);

        // Seconda modifica con la versione vecchia: conflitto (concorrenza ottimistica).
        var stale = await client.PutAsJsonAsync($"api/v1/patients/{patient.Id}", Input("PZ-100", patient.Version), Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"api/v1/patients/{patient.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"api/v1/patients/{patient.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Invalid_data_is_rejected_with_validation_problem()
    {
        var client = await server.CreateClinicianClientAsync();
        var response = await client.PostAsJsonAsync("api/v1/patients",
            Input("PZ-BAD") with { FiscalCode = "123", Email = "non-una-email", BirthDate = new DateOnly(2999, 1, 1) }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("fiscalCode", body, StringComparison.Ordinal);
        Assert.Contains("email", body, StringComparison.Ordinal);
        Assert.Contains("birthDate", body, StringComparison.Ordinal);
    }

    /// <summary>I messaggi seguono la lingua chiesta dal client; senza indicazioni restano in italiano.</summary>
    [Fact]
    public async Task Messages_follow_the_language_requested_by_the_client()
    {
        const string italian = "Il codice fiscale deve avere 16 caratteri alfanumerici.";
        const string english = "The tax code must be 16 alphanumeric characters.";

        // Un solo accesso per tutti i casi: il login ha un limite di frequenza.
        var client = await server.CreateClinicianClientAsync();
        foreach (var (acceptLanguage, expected) in ((string?, string)[])
            [(null, italian), ("it-IT,it;q=0.9", italian), ("en", english), ("de-DE,en;q=0.7,it;q=0.2", english)])
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/patients")
            {
                Content = JsonContent.Create(Input("PZ-LANG") with { FiscalCode = "123" }),
            };
            if (acceptLanguage is not null)
            {
                request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
            }

            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(expected, System.Text.RegularExpressions.Regex.Unescape(await response.Content.ReadAsStringAsync(Ct)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_huge_page_number_is_not_a_server_error()
    {
        var client = await server.CreateClinicianClientAsync();
        foreach (var path in (string[])["api/v1/patients?page=2147483647", "api/v1/audit?page=2147483647&pageSize=200"])
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path, Ct)).StatusCode);
        }
    }
}
