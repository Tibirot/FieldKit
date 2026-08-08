using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Visit.Migrations
{
    /// <inheritdoc />
    public partial class InitialVisitSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "visit");

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "visit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Content = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredOnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedOnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_message", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "visit",
                schema: "visit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OutletId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PlannedVisitId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CheckedInAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CheckInLatitude = table.Column<double>(type: "double precision", nullable: true),
                    CheckInLongitude = table.Column<double>(type: "double precision", nullable: true),
                    CheckInDistanceMetres = table.Column<double>(type: "double precision", nullable: true),
                    WasInsideGeofence = table.Column<bool>(type: "boolean", nullable: false),
                    GeofenceOverrideReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visit", x => x.Id);
                    table.CheckConstraint("ck_visit_checkin_point", "(\"CheckInLatitude\" IS NULL) = (\"CheckInLongitude\" IS NULL)");
                    table.CheckConstraint("ck_visit_override_reason", "\"WasInsideGeofence\" = false OR \"GeofenceOverrideReason\" IS NULL");
                });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_message_ProcessedOnUtc",
                schema: "visit",
                table: "outbox_message",
                column: "ProcessedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_visit_TenantId_OutletId_CheckedInAtUtc",
                schema: "visit",
                table: "visit",
                columns: new[] { "TenantId", "OutletId", "CheckedInAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_visit_TenantId_UserId_CheckedInAtUtc",
                schema: "visit",
                table: "visit",
                columns: new[] { "TenantId", "UserId", "CheckedInAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "visit");

            migrationBuilder.DropTable(
                name: "visit",
                schema: "visit");
        }
    }
}
