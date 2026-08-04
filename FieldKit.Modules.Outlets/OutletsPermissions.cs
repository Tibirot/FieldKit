namespace FieldKit.Modules.Outlets;

/// <summary>
/// The permissions the Outlets module owns, as <c>resource:action</c> strings.
/// </summary>
/// <remarks>
/// Channels are split from outlets rather than folded in, because they are different in kind: an
/// outlet is a fact about the world that changes often, a channel is the vocabulary a tenant
/// classifies by and changes almost never. Someone maintaining the outlet base all day should not
/// also be able to rename the classification every assortment rule keys off.
/// </remarks>
public static class OutletsPermissions
{
    public const string OutletRead = "outlet:read";
    public const string OutletWrite = "outlet:write";
    public const string ChannelRead = "channel:read";
    public const string ChannelWrite = "channel:write";
}
