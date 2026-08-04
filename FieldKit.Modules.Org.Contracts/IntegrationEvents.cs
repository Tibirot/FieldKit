using FieldKit.BuildingBlocks;

namespace FieldKit.Modules.Org.Contracts;

/// <summary>
/// A territory's rep assignment was created, changed or removed (<c>ORG-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// Delivered through the outbox. Sync and Journey react to it: a territory's membership is a rep's
/// offline data scope (BR-ORG-3), so this is the moment a device's contents should change — in
/// <b>both</b> directions, which is why the outgoing rep is named as well as the incoming one. A
/// consumer that only knew who arrived would still have to work out whose device to shrink.
/// </para>
/// <para>
/// Deliberately does <b>not</b> carry the territory's outlets. The list would be stale the moment
/// membership changed, which happens independently of assignments, and it would grow the payload
/// with the territory. A consumer that needs the outlets should ask for them at the point of use.
/// </para>
/// </remarks>
/// <param name="TerritoryId">The territory whose coverage changed.</param>
/// <param name="IncomingUserId">
/// The rep now assigned, or <c>null</c> when an assignment was removed and nobody replaced them.
/// </param>
/// <param name="OutgoingUserId">
/// The rep who was assigned before, or <c>null</c> when there was nobody — a first assignment.
/// </param>
/// <param name="From">The first day the incoming assignment covers. Null when there is no incoming one.</param>
/// <param name="To">Its last day, or null for "until further notice".</param>
public sealed record RepAssignmentChanged(
    Guid Id,
    DateTimeOffset OccurredOn,
    Guid TerritoryId,
    string? IncomingUserId,
    string? OutgoingUserId,
    DateOnly? From,
    DateOnly? To) : IIntegrationEvent;
