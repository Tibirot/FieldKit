using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FieldKit.Infrastructure;

/// <summary>Maps the strongly-typed <see cref="TenantId"/> to/from its underlying <see cref="Guid"/>.</summary>
public sealed class TenantIdValueConverter : ValueConverter<TenantId, Guid>
{
    public TenantIdValueConverter() : base(id => id.Value, value => new TenantId(value)) { }
}
