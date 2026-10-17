using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicalSensorFusion.Server.Data.Migrations;

/// <inheritdoc />
public partial class _20261017003654_DashboardAccountPassword : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PasswordHash",
            table: "dashboard_accounts",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PasswordHash",
            table: "dashboard_accounts");
    }
}
