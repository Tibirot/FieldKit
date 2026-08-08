using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Journey.Migrations
{
    /// <inheritdoc />
    public partial class InitialJourneySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "journey");

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "journey",
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
                name: "outlet_frequency",
                schema: "journey",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OutletId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitsPerCycle = table.Column<int>(type: "integer", nullable: false),
                    CycleLengthDays = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outlet_frequency", x => x.Id);
                    table.CheckConstraint("ck_outlet_frequency_cycle", "\"CycleLengthDays\" >= 1 AND \"CycleLengthDays\" <= 365");
                    table.CheckConstraint("ck_outlet_frequency_visits", "\"VisitsPerCycle\" >= 1");
                });

            migrationBuilder.CreateTable(
                name: "segment_frequency",
                schema: "journey",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Segment = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    VisitsPerCycle = table.Column<int>(type: "integer", nullable: false),
                    CycleLengthDays = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_segment_frequency", x => x.Id);
                    table.CheckConstraint("ck_segment_frequency_cycle", "\"CycleLengthDays\" >= 1 AND \"CycleLengthDays\" <= 365");
                    table.CheckConstraint("ck_segment_frequency_visits", "\"VisitsPerCycle\" >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_message_ProcessedOnUtc",
                schema: "journey",
                table: "outbox_message",
                column: "ProcessedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_outlet_frequency_TenantId_OutletId",
                schema: "journey",
                table: "outlet_frequency",
                columns: new[] { "TenantId", "OutletId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_segment_frequency_TenantId_Segment",
                schema: "journey",
                table: "segment_frequency",
                columns: new[] { "TenantId", "Segment" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "journey");

            migrationBuilder.DropTable(
                name: "outlet_frequency",
                schema: "journey");

            migrationBuilder.DropTable(
                name: "segment_frequency",
                schema: "journey");
        }
    }
}
