using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Outlets.Migrations
{
    /// <summary>
    /// Makes a channel name and an outlet code unique <i>ignoring case</i>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The indexes these replace were <c>(TenantId, Name)</c> and <c>(TenantId, Code)</c>, which
    /// Postgres compares case-sensitively — so <c>HoReCa</c> and <c>Horeca</c> could both exist, as
    /// could <c>OUT-1</c> and <c>out-1</c>. Two rows for one thing, and the <see cref="Channel"/>
    /// entity has documented since it was written that "two channels with one name are a data-entry
    /// accident". The guarantee was stated and never enforced.
    /// </para>
    /// <para>
    /// Written as SQL because the uniqueness is over an expression and EF has no fluent API for that.
    /// The consequence worth knowing: these indexes are invisible to the model snapshot, so they will
    /// not appear in a scaffolded migration and must be maintained here.
    /// </para>
    /// <para>
    /// <b>This migration fails on a database that already holds such a pair</b>, and that is the
    /// correct behavior — it means real rows have to be merged, which is a decision for whoever owns
    /// the data rather than something a migration should quietly pick a winner for.
    /// </para>
    /// </remarks>
    public partial class AddCaseInsensitiveUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outlet_TenantId_Code",
                schema: "outlets",
                table: "outlet");

            migrationBuilder.DropIndex(
                name: "IX_channel_TenantId_Name",
                schema: "outlets",
                table: "channel");

            // lower(), not citext: one column each, and an expression index keeps the change to the
            // two places that need it rather than altering a type whose comparison semantics every
            // future query would inherit. The stored value is untouched — only the comparison ignores
            // case, so a channel keeps the capitalisation whoever created it chose.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_channel_TenantId_Name_ci"
                    ON outlets.channel ("TenantId", lower("Name"));
                """);

            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_outlet_TenantId_Code_ci"
                    ON outlets.outlet ("TenantId", lower("Code"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX outlets."IX_outlet_TenantId_Code_ci";""");
            migrationBuilder.Sql("""DROP INDEX outlets."IX_channel_TenantId_Name_ci";""");

            migrationBuilder.CreateIndex(
                name: "IX_outlet_TenantId_Code",
                schema: "outlets",
                table: "outlet",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_channel_TenantId_Name",
                schema: "outlets",
                table: "channel",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }
    }
}
