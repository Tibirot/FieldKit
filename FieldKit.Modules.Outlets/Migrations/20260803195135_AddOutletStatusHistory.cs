using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Outlets.Migrations
{
    /// <inheritdoc />
    public partial class AddOutletStatusHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outlet_status_change",
                schema: "outlets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OutletId = table.Column<Guid>(type: "uuid", nullable: false),
                    From = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    To = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outlet_status_change", x => x.Id);
                    table.ForeignKey(
                        name: "FK_outlet_status_change_outlet_OutletId",
                        column: x => x.OutletId,
                        principalSchema: "outlets",
                        principalTable: "outlet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_outlet_status_change_OutletId",
                schema: "outlets",
                table: "outlet_status_change",
                column: "OutletId");

            migrationBuilder.CreateIndex(
                name: "IX_outlet_status_change_TenantId_OutletId_CreatedAtUtc",
                schema: "outlets",
                table: "outlet_status_change",
                columns: new[] { "TenantId", "OutletId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outlet_status_change",
                schema: "outlets");
        }
    }
}
