using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Outlets.Migrations
{
    /// <inheritdoc />
    public partial class InitialOutletsSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "outlets");

            migrationBuilder.CreateTable(
                name: "channel",
                schema: "outlets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "outlets",
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
                name: "outlet",
                schema: "outlets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Segment = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Banner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outlet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_outlet_channel_ChannelId",
                        column: x => x.ChannelId,
                        principalSchema: "outlets",
                        principalTable: "channel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_channel_TenantId_Name",
                schema: "outlets",
                table: "channel",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_message_ProcessedOnUtc",
                schema: "outlets",
                table: "outbox_message",
                column: "ProcessedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_outlet_ChannelId",
                schema: "outlets",
                table: "outlet",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_outlet_TenantId_ChannelId",
                schema: "outlets",
                table: "outlet",
                columns: new[] { "TenantId", "ChannelId" });

            migrationBuilder.CreateIndex(
                name: "IX_outlet_TenantId_Code",
                schema: "outlets",
                table: "outlet",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outlet_TenantId_Status",
                schema: "outlets",
                table: "outlet",
                columns: new[] { "TenantId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "outlets");

            migrationBuilder.DropTable(
                name: "outlet",
                schema: "outlets");

            migrationBuilder.DropTable(
                name: "channel",
                schema: "outlets");
        }
    }
}
