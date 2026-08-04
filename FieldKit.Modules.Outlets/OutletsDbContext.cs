using System.Text.Json;
using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace FieldKit.Modules.Outlets;

/// <summary>The Outlets module's context — owns the <c>outlets</c> schema (schema-per-module, ADR-0005).</summary>
public sealed class OutletsDbContext(DbContextOptions<OutletsDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "outlets";

    protected override string Schema => SchemaName;

    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<Outlet> Outlets => Set<Outlet>();

    /// <summary>Append-only. Nothing in this module updates or removes one — see the entity.</summary>
    public DbSet<OutletStatusChange> OutletStatusChanges => Set<OutletStatusChange>();


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Channel>(channel =>
        {
            channel.ToTable("channel");
            channel.HasKey(c => c.Id);
            channel.Property(c => c.Name).HasMaxLength(100).IsRequired();

            // The uniqueness is case-insensitive and lives in raw SQL — see the note on the outlet's
            // code index below, which is the same decision for the same reason.
        });

        modelBuilder.Entity<Outlet>(outlet =>
        {
            outlet.ToTable("outlet");
            outlet.HasKey(o => o.Id);
            outlet.Property(o => o.Code).HasMaxLength(50).IsRequired();
            outlet.Property(o => o.Name).HasMaxLength(200).IsRequired();
            outlet.Property(o => o.Segment).HasMaxLength(50);
            outlet.Property(o => o.Banner).HasMaxLength(100);

            // Stored as the string, not the int. An enum's numeric value is a position in a source
            // file: reordering the members would silently reinterpret every row, and nobody reading
            // the table could tell 2 from "Closed" without the code in front of them.
            outlet.Property(o => o.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

            // The tenant's own identifier, so unique per tenant and not globally — two tenants
            // numbering their stores from 1 is the ordinary case, not a collision.
            //
            // Declared in raw SQL (see the AddCaseInsensitiveUniqueness migration) rather than here,
            // because the uniqueness is case-INsensitive and EF has no fluent API for an index over
            // an expression. A plain `(TenantId, Code)` compares case-sensitively in Postgres, which
            // let OUT-1 and out-1 both exist: two rows for one shop, and a bulk import of a file
            // holding both would create the pair without a word.
            //
            // The stored value keeps whatever casing it was given — only the comparison ignores case,
            // which is why the endpoints look up through `ToLower()` and hit the same index.

            // BR-OUT-1's half that a database can hold: every outlet has a channel, and the channel
            // cannot be deleted out from under it. The endpoint refuses first with a count, which is
            // the answer an admin can act on; this is what makes that a guarantee.
            outlet.HasOne<Channel>()
                .WithMany()
                .HasForeignKey(o => o.ChannelId)
                .OnDelete(DeleteBehavior.Restrict);

            // The two queries the outlet list actually makes: filter by channel, filter by status.
            outlet.HasIndex(o => new { o.TenantId, o.ChannelId });
            outlet.HasIndex(o => new { o.TenantId, o.Status });

            // Not an offset and not derived from the coordinates: a visit's business day and a
            // promotion's validity resolve here, and an offset is wrong twice a year.
            outlet.Property(o => o.TimeZoneId).HasMaxLength(64).IsRequired(); // IANA

            // Owned, so the columns live on `outlet` rather than in a join nobody wants for an
            // address. Every part is optional — a half-known outlet must still be recordable.
            outlet.OwnsOne(o => o.Address, address =>
            {
                address.Property(a => a.Street).HasColumnName("address_street").HasMaxLength(200);
                address.Property(a => a.City).HasColumnName("address_city").HasMaxLength(100);
                address.Property(a => a.PostalCode).HasColumnName("address_postal_code").HasMaxLength(20);
                address.Property(a => a.CountryCode).HasColumnName("address_country_code").HasMaxLength(2);
            });

            // Two columns, composed into a GeoPoint by the entity. Not an owned type: GeoPoint is a
            // struct and EF owns only reference types — which is worth the swap, because the
            // invariant is now a database constraint instead of a mapping convention.
            // JSONB, not EAV (ADR-0009 §1). Mapped through a converter because the property is a
            // dictionary of raw JSON elements — what is inside is the tenant's business, described by
            // the Configuration catalogue rather than by this model.
            outlet.Property(o => o.CustomFields)
                .HasColumnName("custom_fields")
                .HasColumnType("jsonb")
                .HasConversion(
                    fields => JsonSerializer.Serialize(fields, (JsonSerializerOptions?)null),
                    json => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, (JsonSerializerOptions?)null)!,
                    new ValueComparer<IReadOnlyDictionary<string, JsonElement>>(
                        // Compared by serialized form: JsonElement has no value equality, so without
                        // this EF would compare references and miss every edit to a custom field.
                        (left, right) => JsonSerializer.Serialize(left, (JsonSerializerOptions?)null)
                            == JsonSerializer.Serialize(right, (JsonSerializerOptions?)null),
                        fields => JsonSerializer.Serialize(fields, (JsonSerializerOptions?)null).GetHashCode(),
                        fields => fields));

            outlet.Property(o => o.Latitude).HasColumnName("latitude");
            outlet.Property(o => o.Longitude).HasColumnName("longitude");
            outlet.Ignore(o => o.Location);

            // Both or neither. A latitude without a longitude is not a partly-known location, it is
            // a broken one — and this holds against anything that writes the table, including the
            // bulk import that has not been written yet.
            outlet.ToTable(table => table.HasCheckConstraint(
                "ck_outlet_location_complete",
                @"(""latitude"" IS NULL) = (""longitude"" IS NULL)"));

            // A table, because contacts are a list and there may be several. Personal data (B8):
            // it inherits the tenant filter through its owner, and removing a contact from the
            // outlet deletes the row rather than flagging it.
            outlet.OwnsMany(o => o.Contacts, contact =>
            {
                contact.ToTable("outlet_contact");
                contact.WithOwner().HasForeignKey("OutletId");
                contact.Property(c => c.Name).HasMaxLength(200).IsRequired();
                contact.Property(c => c.Role).HasMaxLength(100);
                contact.Property(c => c.Phone).HasMaxLength(50);
                contact.Property(c => c.Email).HasMaxLength(320); // RFC 5321 maximum
            });
        });

        modelBuilder.Entity<OutletStatusChange>(change =>
        {
            change.ToTable("outlet_status_change");
            change.HasKey(c => c.Id);
            change.Property(c => c.From).HasConversion<string>().HasMaxLength(20);
            change.Property(c => c.To).HasConversion<string>().HasMaxLength(20).IsRequired();
            change.Property(c => c.Reason).HasMaxLength(500);

            // Cascade, uniquely in this module — and only because the parent cannot be deleted.
            // Outlets are never removed (that is what Closed is for), so this exists to stop an
            // administrative purge leaving orphaned audit rows pointing at nothing. If outlet
            // deletion is ever added, this needs revisiting before that ships: an audit trail that
            // disappears with its subject is not an audit trail.
            change.HasOne<Outlet>()
                .WithMany()
                .HasForeignKey(c => c.OutletId)
                .OnDelete(DeleteBehavior.Cascade);

            // The one query this table serves: one outlet's history, newest first.
            change.HasIndex(c => new { c.TenantId, c.OutletId, c.CreatedAtUtc });
        });
    }
}
