namespace FieldKit.Modules.Configuration.Contracts;

/// <summary>
/// What a rep is asked to do at one step of a visit (<c>VIS-03</c>).
/// </summary>
/// <remarks>
/// A closed set, for the reason <see cref="CustomFieldEntity"/> is one: each of these opens a
/// sub-flow that some module has to implement, so "which kinds exist" is a question the type system
/// should answer rather than one a typo could extend. The list is the spec's own, and the ones with
/// no module behind them yet are still named here — a workflow an admin cannot express is a workflow
/// they will express badly with the types that do exist.
/// </remarks>
public enum VisitStepType
{
    /// <summary>A store audit — availability, share of shelf, planogram (<c>AUD-01</c>).</summary>
    Audit = 0,

    /// <summary>Taking an order (<c>ORD-01</c>).</summary>
    Order = 1,

    /// <summary>A questionnaire the tenant defined (<c>CFG-04</c>).</summary>
    Survey = 2,

    /// <summary>A checklist item — "check the chiller is lit".</summary>
    Task = 3,

    /// <summary>A photo, which needs the upload path (W11).</summary>
    Photo = 4,

    /// <summary>Free text (<c>VIS-06</c>).</summary>
    Note = 5,

    /// <summary>A signature (<c>VIS-08</c>, Phase 3).</summary>
    Signature = 6,
}

/// <summary>One step in a visit's sequence, as the module running the visit sees it.</summary>
/// <param name="Order">Where it sits. Contiguous from 1 — see <c>VisitWorkflow</c>.</param>
/// <param name="Mandatory">
/// Whether check-out is refused while it is open (<c>BR-VIS-3</c>). The flag lives on the *step*
/// rather than the type, because the same kind of work is required in one channel and optional in
/// another — an audit is the job in modern trade and a courtesy in a kiosk.
/// </param>
/// <param name="Label">
/// What an admin called it, for the rep's screen. Never for matching: the <see cref="Type"/> decides
/// which sub-flow opens, and a workflow that behaved differently because somebody renamed a step
/// would be a rule hidden in a string.
/// </param>
public sealed record VisitStepDescriptor(int Order, VisitStepType Type, bool Mandatory, string Label);

/// <summary>
/// How a visit is worked in one channel: the steps, and whether the rep is expected to be there.
/// </summary>
/// <param name="PresenceExpected">
/// <para>
/// Whether being at the outlet is part of what this visit *is* — the flag <c>BR-VIS-2</c>'s
/// assumption asks for.
/// </para>
/// <para>
/// True for an ordinary store call: a rep checking in from somewhere else is an exception, and the
/// rule is to record it with a reason rather than block them. False for a channel worked remotely —
/// a phone call, a video call, a head-office meeting — where demanding an override reason would
/// record an exception every single time. <b>A flag that fires on ordinary work is a flag
/// supervisors learn to ignore</b>, which is worse than not having one.
/// </para>
/// </param>
public sealed record VisitWorkflowDescriptor(
    Guid ChannelId, bool PresenceExpected, IReadOnlyList<VisitStepDescriptor> Steps);

/// <summary>
/// The per-channel visit workflow a tenant configured (<c>VIS-03</c>, <c>A1</c>).
/// </summary>
/// <remarks>
/// <para>
/// Configuration owns what a tenant may flex; Visit owns what happens when a rep works. This is the
/// seam, and it is built one slice ahead of its consumer on purpose — <c>BR-VIS-2</c>'s override
/// rule cannot be written without somewhere to ask whether presence was expected, so check-in
/// depends on this existing rather than the other way round.
/// </para>
/// <para>
/// <b>Keyed by channel, and nothing else.</b> The visit workflow is the one thing in this contract a
/// consumer decides with, and channel is already how assortment (<c>PRD-02</c>) and pricing
/// (<c>BR-PRD-2</c>) branch — so a tenant that has thought about its channels has already done the
/// work this needs. Per-outlet workflows are deliberately absent: a workflow that varies shop by
/// shop is a workflow nobody can review, and no requirement asks for one.
/// </para>
/// </remarks>
public interface IVisitWorkflow
{
    /// <summary>
    /// How visits are worked in <paramref name="channelId"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never null.</b> A channel nobody has configured gets the default: no steps, and presence
    /// expected. Both halves are deliberate — an empty step list is a visit that is just a check-in
    /// and a check-out, which is a real and useful thing, and presence-expected is the ordinary case
    /// so the safe default is the one that records an exception rather than the one that hides it.
    /// </para>
    /// <para>
    /// Returning a default rather than null also keeps <c>BR-VIS-2</c> out of every caller's
    /// null-check: check-in asks whether presence was expected and gets an answer, rather than
    /// asking whether anybody configured this channel and then deciding what that means.
    /// </para>
    /// </remarks>
    Task<VisitWorkflowDescriptor> ForChannelAsync(
        Guid channelId, CancellationToken cancellationToken = default);
}
