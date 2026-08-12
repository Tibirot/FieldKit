using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace FieldKit.Infrastructure;

/// <summary>
/// Tells EF Core that a <see cref="Guid"/> primary key is <b>supplied by us, always</b> — so a key
/// that is already set is not mistaken for a row that already exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this ends, which had five occurrences.</b> When a new child is discovered on an
/// <i>already-tracked</i> parent's navigation — a workflow's replaced steps, a survey's replaced
/// questions, a weight set's replaced weights, a rep's unplanned call over HTTP and again over
/// <c>/sync/push</c> — EF attaches it with
/// <c>AttachGraph(entry, Added, Modified, forceStateWhenUnknownKey: true)</c>. That picks
/// <see cref="EntityState.Added"/> when the key is <i>unknown</i> and
/// <see cref="EntityState.Modified"/> when it is set. Every aggregate here names its own children
/// with <c>Guid.CreateVersion7()</c> before EF ever sees them, so the key is always set: EF concluded
/// the row existed and issued an <c>UPDATE</c> that matched nothing — surfacing as a
/// <c>DbUpdateConcurrencyException</c> on the HTTP paths and a 500 on the push path.
/// </para>
/// <para>
/// <b>Why "key is set" was evidence at all.</b> EF's default for a <see cref="Guid"/> key is
/// <see cref="ValueGenerated.OnAdd"/> — it offers to invent one — and a <i>generated</i> key that
/// nevertheless holds a value can only have come from the store. That inference is sound, and its
/// premise is simply false here. Saying <see cref="ValueGenerated.Never"/> withdraws the premise
/// rather than working around the conclusion, which is why the five
/// <c>db.Set&lt;TChild&gt;().AddRange(parent.Children)</c> lines could all be deleted.
/// </para>
/// <para>
/// <b>It does not fire on a brand-new root.</b> <c>db.Set&lt;T&gt;().Add(newAggregate)</c> paints the
/// whole graph <see cref="EntityState.Added"/> whatever the keys hold, because both of that call's
/// target states are <c>Added</c>. The sixth "occurrence" — order lines and submissions in W11
/// slice 3 — was a copy of the workaround into a place that never needed it, and removing it changes
/// no behaviour.
/// </para>
/// <para>
/// <b>Guid keys only.</b> An <c>int</c> identity key genuinely is store-generated, and telling EF
/// otherwise would have it insert zeros. The narrow rule is the safe one; the broad convention would
/// be a trap the day this codebase gains its first sequence.
/// </para>
/// </remarks>
public sealed class ClientGeneratedKeyConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            foreach (var key in entityType.GetKeys())
            {
                foreach (var property in key.Properties)
                {
                    if (property.ClrType != typeof(Guid)) continue;
                    if (property.ValueGenerated is not ValueGenerated.OnAdd) continue;

                    property.Builder.ValueGenerated(ValueGenerated.Never);
                }
            }
        }
    }
}

/// <summary>
/// Refuses to insert a row whose <see cref="Guid"/> key was never set, rather than letting a zeroed
/// key reach Postgres.
/// </summary>
/// <remarks>
/// <para>
/// <b>The safety net <see cref="ClientGeneratedKeyConvention"/> takes away.</b> Under EF's default,
/// an entity created without naming itself still got a key — EF invented one on the way to the
/// database. Withdrawing <see cref="ValueGenerated.OnAdd"/> withdraws that too, and the failure it
/// leaves behind is quiet and awful: the first such insert stores
/// <c>00000000-0000-0000-0000-000000000000</c> and succeeds, and only the <i>second</i> trips the
/// primary key — in a different request, probably on a different day, blaming the wrong row.
/// </para>
/// <para>
/// So the trade is made explicit: the convention removes a silent <c>UPDATE</c> that matched nothing,
/// and this refuses to replace it with a silent zero. Both failures now happen at the moment of the
/// mistake, and say which entity made it.
/// </para>
/// </remarks>
public sealed class ClientGeneratedKeyGuard : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null) Check(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) Check(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Check(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not EntityState.Added) continue;

            foreach (var property in entry.Metadata.FindPrimaryKey()?.Properties ?? [])
            {
                if (property.ClrType != typeof(Guid)) continue;
                if (entry.Property(property.Name).CurrentValue is not Guid key || key != Guid.Empty)
                    continue;

                throw new InvalidOperationException(
                    $"{entry.Metadata.DisplayName()}.{property.Name} was not set. FieldKit names its "
                    + "own rows (Guid.CreateVersion7) and EF no longer invents a key — see "
                    + "ClientGeneratedKeyConvention.");
            }
        }
    }
}
