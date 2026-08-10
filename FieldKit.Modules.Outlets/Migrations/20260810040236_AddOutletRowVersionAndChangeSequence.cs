using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Outlets.Migrations
{
    /// <inheritdoc />
    public partial class AddOutletRowVersionAndChangeSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                schema: "outlets",
                table: "outlet",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "change_sequence",
                schema: "outlets",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_sequence", x => x.TenantId);
                });

            // Backfill, so rows that existed before this migration are syncable.
            //
            // Without it every outlet sits at version 0, a delta asks for `> cursor`, and a device
            // that has pulled once never sees any of them again — invisible until somebody happens
            // to edit them. Numbering is per tenant and ordered by creation: arbitrary but stable.
            // What matters is that each row gets a distinct version and the counter starts above all
            // of them, so the next real change sorts after the backfill rather than colliding.
            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT "Id",
                           row_number() OVER (PARTITION BY "TenantId" ORDER BY "CreatedAtUtc", "Id") AS version
                    FROM outlets.outlet
                )
                UPDATE outlets.outlet AS o
                SET "RowVersion" = numbered.version
                FROM numbered
                WHERE o."Id" = numbered."Id";
                """);

            migrationBuilder.Sql("""
                INSERT INTO outlets.change_sequence ("TenantId", "Value")
                SELECT "TenantId", MAX("RowVersion")
                FROM outlets.outlet
                GROUP BY "TenantId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_sequence",
                schema: "outlets");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "outlets",
                table: "outlet");
        }
    }
}
