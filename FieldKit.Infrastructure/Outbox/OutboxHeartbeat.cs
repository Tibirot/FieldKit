using System.Collections.Concurrent;

namespace FieldKit.Infrastructure.Outbox;

/// <summary>
/// When each module's dispatcher last completed a cycle (<c>observability §3</c>) — W13 slice 5.
/// </summary>
/// <remarks>
/// <para>
/// <b>A heartbeat rather than a status flag.</b> The failure this exists to catch is a dispatcher
/// that has <i>stopped</i>, and a stopped loop cannot set a flag saying so — the only evidence it
/// leaves is the absence of new evidence. So the dispatcher stamps a time on every completed cycle,
/// including one that found nothing, and a reader judges the silence.
/// </para>
/// <para>
/// <b>It records success, not attempts.</b> A cycle that threw is caught, logged and slept on
/// (<see cref="OutboxDispatcher{TContext}"/>); it does not stamp. So a database that has gone away
/// stops the heartbeat as surely as a killed loop, which is the honest reading — a dispatcher that
/// cannot reach its outbox is not delivering, whatever its thread is doing.
/// </para>
/// <para>
/// Kept here rather than in the host because the dispatcher writes it and the dispatcher lives here.
/// The <i>judgement</i> — how old is too old — is a health check's, and lives with the host that
/// answers the probe.
/// </para>
/// </remarks>
public sealed class OutboxHeartbeat
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _beats = new(StringComparer.Ordinal);

    /// <summary>Records that <paramref name="module"/>'s dispatcher completed a cycle.</summary>
    public void Beat(string module, DateTimeOffset at) => _beats[module] = at;

    /// <summary>The last completed cycle per module, as a snapshot.</summary>
    /// <remarks>
    /// A copy rather than the live dictionary: a reader iterating one that a dispatcher is writing to
    /// would be correct today (<c>ConcurrentDictionary</c> allows it) and would stop being obviously
    /// correct the moment somebody added a second reader with an opinion about ordering.
    /// </remarks>
    public IReadOnlyDictionary<string, DateTimeOffset> Beats() =>
        new Dictionary<string, DateTimeOffset>(_beats, StringComparer.Ordinal);
}
