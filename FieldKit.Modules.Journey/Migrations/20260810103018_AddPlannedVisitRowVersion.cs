using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Journey.Migrations
{
    /// <inheritdoc />
    public partial class AddPlannedVisitRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                schema: "journey",
                table: "planned_visit",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "change_sequence",
                schema: "journey",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_sequence", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "tombstone",
                schema: "journey",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tombstone", x => new { x.TenantId, x.EntityType, x.EntityId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_tombstone_TenantId_RowVersion",
                schema: "journey",
                table: "tombstone",
                columns: new[] { "TenantId", "RowVersion" });

            /*
             * Existing calls are stamped, because the default of zero would hide them forever.
             *
             * The feed sends rows with `RowVersion > cursor`, and a device's first pull sends
             * cursor 0 — so a plan published before this migration would be invisible to every
             * device until somebody happened to edit one of its calls. Silent, and permanent for
             * plans nobody touches.
             *
             * Version 1 for everything: the rows predate the counter, so there is no order among
             * them to preserve, and one version means one page rather than a needless N.
             */
            migrationBuilder.Sql(@"UPDATE journey.planned_visit SET ""RowVersion"" = 1;");

            // And the counter starts above them, so the next real change gets 2 rather than
            // colliding with the backfill and arriving at a version a device has already banked.
            migrationBuilder.Sql(@"
                INSERT INTO journey.change_sequence (""TenantId"", ""Value"")
                SELECT DISTINCT ""TenantId"", 1 FROM journey.planned_visit
                ON CONFLICT (""TenantId"") DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_sequence",
                schema: "journey");

            migrationBuilder.DropTable(
                name: "tombstone",
                schema: "journey");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "journey",
                table: "planned_visit");
        }
    }
}
