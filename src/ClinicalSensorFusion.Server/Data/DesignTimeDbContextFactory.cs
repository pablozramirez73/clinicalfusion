using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClinicalSensorFusion.Server.Data;

/// <summary>Usata solo da <c>dotnet ef migrations</c>: nessuna connessione reale viene aperta.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ClinicalDbContext>
{
    public ClinicalDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ClinicalDbContext>()
            .UseNpgsql("Host=localhost;Database=clinical_design")
            .Options);
}
