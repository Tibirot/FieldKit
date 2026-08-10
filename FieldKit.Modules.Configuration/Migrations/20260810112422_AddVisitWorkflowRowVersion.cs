using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Configuration.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitWorkflowRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                schema: "config",
                table: "visit_workflow",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "change_sequence",
                schema: "config",
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
                schema: "config",
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
                schema: "config",
                table: "tombstone",
                columns: new[] { "TenantId", "RowVersion" });

            /*
             * Existing workflows are stamped, because the default of zero would hide them forever.
             *
             * The feed sends `RowVersion > cursor` and a device's first pull sends 0, so a workflow
             * configured before this migration would never reach a phone — and the device would fall
             * back to the empty default, running visits with none of the steps the tenant asked for.
             * A rep would simply never be shown the audit. Silent in exactly the way that matters.
             *
             * Version 1 for everything: these rows predate the counter, so there is no order among
             * them to preserve, and the counter starts above them so the next real edit gets 2.
             */
            migrationBuilder.Sql(@"UPDATE config.visit_workflow SET ""RowVersion"" = 1;");

            migrationBuilder.Sql(@"
                INSERT INTO config.change_sequence (""TenantId"", ""Value"")
                SELECT DISTINCT ""TenantId"", 1 FROM config.visit_workflow
                ON CONFLICT (""TenantId"") DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_sequence",
                schema: "config");

            migrationBuilder.DropTable(
                name: "tombstone",
                schema: "config");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "config",
                table: "visit_workflow");
        }
    }
}
