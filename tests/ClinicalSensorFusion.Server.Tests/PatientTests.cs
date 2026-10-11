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
}
