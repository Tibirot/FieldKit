using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Configuration.Migrations
{
    /// <inheritdoc />
    public partial class InitialConfigSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "config");

            migrationBuilder.CreateTable(
                name: "field_definition",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Entity = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    options = table.Column<string[]>(type: "text[]", nullable: false),
                    MaxLength = table.Column<int>(type: "integer", nullable: true),
                    Minimum = table.Column<double>(type: "double precision", nullable: true),
                    Maximum = table.Column<double>(type: "double precision", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_definition", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "config",
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

            migrationBuilder.CreateIndex(
                name: "IX_field_definition_TenantId_Entity",
                schema: "config",
                table: "field_definition",
                columns: new[] { "TenantId", "Entity" });

            migrationBuilder.CreateIndex(
                name: "IX_field_definition_TenantId_Entity_Key",
                schema: "config",
                table: "field_definition",
                columns: new[] { "TenantId", "Entity", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_message_ProcessedOnUtc",
                schema: "config",
                table: "outbox_message",
                column: "ProcessedOnUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "field_definition",
                schema: "config");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "config");
        }
    }
}
