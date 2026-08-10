using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Outlets.Migrations
{
    /// <inheritdoc />
    public partial class AddTombstonesAndChannelRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                schema: "outlets",
                table: "channel",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "tombstone",
                schema: "outlets",
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

            // Same backfill reasoning as the outlet row version: a channel left at 0 is below every
            // device's cursor and would never reach one. Channels are numbered after the outlets
            // already numbered by the previous migration, so the counter keeps moving forward.
            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT c."Id",
                           COALESCE(s."Value", 0)
                             + row_number() OVER (PARTITION BY c."TenantId" ORDER BY c."CreatedAtUtc", c."Id") AS version
                    FROM outlets.channel AS c
                    LEFT JOIN outlets.change_sequence AS s ON s."TenantId" = c."TenantId"
                )
                UPDATE outlets.channel AS c
                SET "RowVersion" = numbered.version
                FROM numbered
                WHERE c."Id" = numbered."Id";
                """);

            migrationBuilder.Sql("""
                INSERT INTO outlets.change_sequence ("TenantId", "Value")
                SELECT "TenantId", MAX("RowVersion") FROM outlets.channel GROUP BY "TenantId"
                ON CONFLICT ("TenantId") DO UPDATE
                SET "Value" = GREATEST(change_sequence."Value", EXCLUDED."Value");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_tombstone_TenantId_RowVersion",
                schema: "outlets",
                table: "tombstone",
                columns: new[] { "TenantId", "RowVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tombstone",
                schema: "outlets");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "outlets",
                table: "channel");
        }
    }
}
