using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Visit.Migrations
{
    /// <inheritdoc />
    public partial class VisitCheckOut : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CheckOutLatitude",
                schema: "visit",
                table: "visit",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CheckOutLongitude",
                schema: "visit",
                table: "visit",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CheckedOutAtUtc",
                schema: "visit",
                table: "visit",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                schema: "visit",
                table: "visit",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeReason",
                schema: "visit",
                table: "visit",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_visit_checked_out",
                schema: "visit",
                table: "visit",
                sql: "(\"Status\" = 'CheckedOut') = (\"CheckedOutAtUtc\" IS NOT NULL) AND (\"Status\" = 'CheckedOut') = (\"Outcome\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_visit_checkout_point",
                schema: "visit",
                table: "visit",
                sql: "(\"CheckOutLatitude\" IS NULL) = (\"CheckOutLongitude\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_visit_outcome_reason",
                schema: "visit",
                table: "visit",
                sql: "(\"Outcome\" IS NOT DISTINCT FROM 'NonProductive') = (\"OutcomeReason\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_visit_checked_out",
                schema: "visit",
                table: "visit");

            migrationBuilder.DropCheckConstraint(
                name: "ck_visit_checkout_point",
                schema: "visit",
                table: "visit");

            migrationBuilder.DropCheckConstraint(
                name: "ck_visit_outcome_reason",
                schema: "visit",
                table: "visit");

            migrationBuilder.DropColumn(
                name: "CheckOutLatitude",
                schema: "visit",
                table: "visit");

            migrationBuilder.DropColumn(
                name: "CheckOutLongitude",
                schema: "visit",
                table: "visit");

            migrationBuilder.DropColumn(
                name: "CheckedOutAtUtc",
                schema: "visit",
                table: "visit");

            migrationBuilder.DropColumn(
                name: "Outcome",
                schema: "visit",
                table: "visit");

            migrationBuilder.DropColumn(
                name: "OutcomeReason",
                schema: "visit",
                table: "visit");
        }
    }
}
