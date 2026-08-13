using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Audit.Migrations
{
    /// <inheritdoc />
    public partial class PhotoUploadConfirmed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UploadedAtUtc",
                schema: "audit",
                table: "audit_photo",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UploadedAtUtc",
                schema: "audit",
                table: "audit_photo");
        }
    }
}
