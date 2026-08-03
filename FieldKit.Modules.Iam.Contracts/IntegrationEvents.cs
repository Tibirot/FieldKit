using FieldKit.BuildingBlocks;

namespace FieldKit.Modules.Iam.Contracts;

/// <summary>
/// Published when a user is deactivated.
/// </summary>
/// <remarks>
/// <para>
/// Sync consumes this to deactivate the user's bound device and refuse further binds
/// ([A8](../../docs/product/decisions-and-assumptions.md)). It is an *integration* event delivered
/// through the outbox, so IAM does not need to know Sync exists — and the delivery is at-least-once,
/// which is why the handler must be idempotent (AT-6).
/// </para>
/// <para>
/// Deactivation is not revocation. Existing access tokens keep working until they expire (BR-IAM-4)
/// — short TTL plus refresh revocation is the trade the platform makes rather than checking a
/// database on every request. Consumers should treat this as "stop granting new access", not "this
/// user is gone as of now".
/// </para>
/// </remarks>
/// <param name="Id">Unique per event, for outbox idempotency.</param>
/// <param name="OccurredOn">When deactivation happened, UTC.</param>
/// <param name="UserId">The Keycloak subject of the deactivated user.</param>
public sealed record UserDeactivated(Guid Id, DateTimeOffset OccurredOn, string UserId) : IIntegrationEvent;
