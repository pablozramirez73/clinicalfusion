using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicalSensorFusion.Server.Data.Migrations;

/// <inheritdoc />
public partial class _20261013052953_IngestOwnershipAndAuditContext : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "OwnerDevice",
            table: "sessions",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "IpAddress",
            table: "audit_log",
            type: "character varying(45)",
            maxLength: 45,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "UserAgent",
            table: "audit_log",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "OwnerDevice",
            table: "sessions");

        migrationBuilder.DropColumn(
            name: "IpAddress",
            table: "audit_log");

        migrationBuilder.DropColumn(
            name: "UserAgent",
            table: "audit_log");
    }
}
