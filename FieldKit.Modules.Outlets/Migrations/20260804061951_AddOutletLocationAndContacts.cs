using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FieldKit.Modules.Outlets.Migrations
{
    /// <inheritdoc />
    public partial class AddOutletLocationAndContacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfilled to UTC rather than EF's generated "" — hand-edited on purpose.
            //
            // An empty string is not a valid IANA zone, so every existing outlet would have been
            // left in a state the API rejects: readable, apparently fine, and unsaveable the moment
            // anyone edited it. UTC is at least a real zone, so those rows stay valid while being
            // obviously a placeholder to anyone who looks. Tenants with outlets predating this
            // column must set their real zones — a visit's business day resolves in them.
            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                schema: "outlets",
                table: "outlet",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "UTC");

            migrationBuilder.AddColumn<string>(
                name: "address_city",
                schema: "outlets",
                table: "outlet",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "address_country_code",
                schema: "outlets",
                table: "outlet",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "address_postal_code",
                schema: "outlets",
                table: "outlet",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "address_street",
                schema: "outlets",
                table: "outlet",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "latitude",
                schema: "outlets",
                table: "outlet",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "longitude",
                schema: "outlets",
                table: "outlet",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "outlet_contact",
                schema: "outlets",
                columns: table => new
                {
                    OutletId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outlet_contact", x => new { x.OutletId, x.Id });
                    table.ForeignKey(
                        name: "FK_outlet_contact_outlet_OutletId",
                        column: x => x.OutletId,
                        principalSchema: "outlets",
                        principalTable: "outlet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_settings",
                schema: "outlets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ValidateGeoCoordinates = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_settings_TenantId",
                schema: "outlets",
                table: "tenant_settings",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outlet_contact",
                schema: "outlets");

            migrationBuilder.DropTable(
                name: "tenant_settings",
                schema: "outlets");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                schema: "outlets",
                table: "outlet");

            migrationBuilder.DropColumn(
                name: "address_city",
                schema: "outlets",
                table: "outlet");

            migrationBuilder.DropColumn(
                name: "address_country_code",
                schema: "outlets",
                table: "outlet");

            migrationBuilder.DropColumn(
                name: "address_postal_code",
                schema: "outlets",
                table: "outlet");

            migrationBuilder.DropColumn(
                name: "address_street",
                schema: "outlets",
                table: "outlet");

            migrationBuilder.DropColumn(
                name: "latitude",
                schema: "outlets",
                table: "outlet");

            migrationBuilder.DropColumn(
                name: "longitude",
                schema: "outlets",
                table: "outlet");
        }
    }
}
