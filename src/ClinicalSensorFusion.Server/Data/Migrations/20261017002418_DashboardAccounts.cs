using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicalSensorFusion.Server.Data.Migrations;

/// <inheritdoc />
public partial class _20261017002418_DashboardAccounts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "dashboard_accounts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                GoogleSubject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                IsAdmin = table.Column<bool>(type: "boolean", nullable: false),
                RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DecidedBy = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_dashboard_accounts", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_dashboard_accounts_Email",
            table: "dashboard_accounts",
            column: "Email",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_dashboard_accounts_GoogleSubject",
            table: "dashboard_accounts",
            column: "GoogleSubject",
            unique: true,
            filter: "\"GoogleSubject\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_dashboard_accounts_Status",
            table: "dashboard_accounts",
            column: "Status");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "dashboard_accounts");
    }
}
