namespace FieldKit.SharedKernel;

/// <summary>
/// An id a device must drop, and the version at which it stopped applying (sync engine §3).
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in a module's contracts because it is a <b>protocol</b> primitive, not a fact
/// about any one entity. It was born in <c>Outlets.Contracts</c> in W8 slice 3 — correctly, since
/// that was the only feed in existence — and moved the moment a second module needed to say the
/// same thing. Journey's feed cannot reference Outlets' to borrow a record, and duplicating it per
/// module would give the wire four types that are the same type.
/// </para>
/// <para>
/// It carries no discriminator for <i>why</i> a row is gone. Deleted, closed, out of the rep's
/// territory: the device's response is identical in every case, and telling it apart would be
/// telling a phone something it has no way to act on — and, in the out-of-scope case, something
/// about a shop the rep no longer covers.
/// </para>
/// </remarks>
public sealed record ReferenceTombstone(Guid Id, long RowVersion);
