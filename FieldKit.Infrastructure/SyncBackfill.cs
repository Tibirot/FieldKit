namespace FieldKit.Infrastructure;

/// <summary>
/// The SQL a migration needs when an existing table becomes <see cref="ISyncTracked"/> (ADR-0013).
/// </summary>
/// <remarks>
/// <para>
/// <b>Written by hand four times before it was extracted, and each time the consequence of getting
/// it wrong was different.</b> Outlets: a plan nobody can see. Configuration: a visit with no steps,
/// checking out clean. Products: a device holding an empty catalogue. Assortment: rows below a
/// cursor synced devices already held, invisible to exactly the devices that had been working.
/// </para>
/// <para>
/// The mistake is always the same shape, though. <c>RowVersion</c> defaults to <c>0</c>; a feed sends
/// <c>RowVersion &gt; cursor</c>; so an un-stamped row is invisible <i>forever</i>, silently, to
/// every device — including one that has never synced, because its cursor is also zero.
/// </para>
/// <para>
/// <b>And the counter has to move.</b> Stamping at a fixed <c>1</c> is right only while the schema's
/// <c>change_sequence</c> does not yet exist. Once a schema has any tracked entity the counter is
/// already above that, and a fixed value puts the backfilled rows <i>below</i> cursors devices are
/// holding. So this advances the counter once and stamps at the new value: everything already stored
/// becomes one change, at a version strictly above anything issued before. Not historically true,
/// and it does not need to be — what it guarantees is that every device sees these rows exactly once.
/// </para>
/// </remarks>
public static class SyncBackfill
{
    /// <summary>
    /// Stamps every existing row in <paramref name="tables"/> at a fresh version, per tenant.
    /// </summary>
    /// <param name="schema">The module's schema — its <c>change_sequence</c> lives there too.</param>
    /// <param name="tables">
    /// The tables gaining a <c>RowVersion</c>, in the same migration. Passed together rather than one
    /// call each, so they share one counter tick: a device sees them as a single change, which is
    /// what they are.
    /// </param>
    public static string Sql(string schema, params string[] tables)
    {
        if (tables.Length == 0) throw new ArgumentException("Name at least one table.", nameof(tables));

        var tenants = string.Join(
            "\n                    UNION\n                    ",
            tables.Select(table => $@"SELECT ""TenantId"" FROM {schema}.{table}"));

        var stamps = string.Join("\n", tables.Select(table => $@"
            UPDATE {schema}.{table} AS target
            SET ""RowVersion"" = sequence.""Value""
            FROM {schema}.change_sequence AS sequence
            WHERE sequence.""TenantId"" = target.""TenantId"";"));

        // The counter row may not exist for a tenant whose only tracked rows are these, so it is
        // created at zero before being advanced — which is why this is an INSERT and an UPDATE
        // rather than an upsert with a computed value.
        return $@"
            INSERT INTO {schema}.change_sequence (""TenantId"", ""Value"")
            SELECT DISTINCT ""TenantId"", 0 FROM ({tenants}) AS owners
            ON CONFLICT (""TenantId"") DO NOTHING;

            UPDATE {schema}.change_sequence
            SET ""Value"" = ""Value"" + 1
            WHERE ""TenantId"" IN ({tenants});
            {stamps}";
    }
}
