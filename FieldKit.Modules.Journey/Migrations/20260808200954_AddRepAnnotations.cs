using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Journey.Migrations
{
    /// <inheritdoc />
    public partial class AddRepAnnotations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CycleLengthDays",
                schema: "journey",
                table: "planned_visit",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "NotVisitedReason",
                schema: "journey",
                table: "planned_visit",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "RescheduledFrom",
                schema: "journey",
                table: "planned_visit",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                schema: "journey",
                table: "planned_visit",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "journey",
                table: "planned_visit",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planned_visit_reason",
                schema: "journey",
                table: "planned_visit",
                sql: "(\"Status\" = 'NotVisited') = (\"NotVisitedReason\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_planned_visit_reason",
                schema: "journey",
                table: "planned_visit");

            migrationBuilder.DropColumn(
                name: "CycleLengthDays",
                schema: "journey",
                table: "planned_visit");

            migrationBuilder.DropColumn(
                name: "NotVisitedReason",
                schema: "journey",
                table: "planned_visit");

            migrationBuilder.DropColumn(
                name: "RescheduledFrom",
                schema: "journey",
                table: "planned_visit");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "journey",
                table: "planned_visit");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "journey",
                table: "planned_visit");
        }
    }
}
