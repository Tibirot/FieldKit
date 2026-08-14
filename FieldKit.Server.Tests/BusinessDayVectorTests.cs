using System.Text.Json;
using FieldKit.SharedKernel;

namespace FieldKit.Server.Tests;

/// <summary>
/// Runs the shared business-day vectors against the C# engine (<c>BR-PRD-6</c>, <c>PRD-08</c>'s
/// regime) — W11½ R6b.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fifth rule this repository implements twice, and the first whose two implementations share
/// no library at all.</b> Money is <c>decimal</c> against <c>decimal.js</c> — two libraries, one
/// specification. The geofence is arithmetic both languages perform natively. This is
/// <c>TimeZoneInfo</c> against <c>Intl</c>, backed by whatever zone database each runtime shipped
/// with, and agreement is inherited from nothing at all.
/// </para>
/// <para>
/// <b>The disagreement it exists to prevent has already happened once.</b> Until R6b the device
/// dated its pricing by the rep's phone and this side re-priced by the UTC date — two rules rather
/// than one rounded twice, so an order captured before 03:00 local was flagged as a disagreement the
/// rep did nothing to cause (regression F6).
/// </para>
/// <para>
/// <b><c>expected: null</c> is a case, not a gap.</b> A zone neither runtime recognises has to
/// produce *no answer* on both sides — a UTC fallback would reinstate the defect silently, for
/// exactly the shops nobody had noticed, and would look like the rule working.
/// </para>
/// </remarks>
public class BusinessDayVectorTests
{
    private static readonly VectorFile File = Load();

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var vector in File.Cases) data.Add(vector.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_engine_agrees_with_the_shared_vector(string name)
    {
        var vector = File.Cases.Single(candidate => candidate.Name == name);

        var actual = BusinessDay.On(vector.TimeZoneId, vector.At);

        if (vector.Expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.Equal(DateOnly.Parse(vector.Expected), actual);
    }

    [Fact]
    public void The_file_carries_the_version_this_suite_was_written_against()
    {
        // A file whose cases changed meaning bumps its version, so a suite running an older one
        // fails loudly rather than quietly proving yesterday's rule (vectors/README.md).
        Assert.Equal(1, File.Version);
    }

    [Fact]
    public void Every_zone_the_file_names_resolves_here_except_the_ones_meant_not_to()
    {
        /*
         * The assertion that stops this suite passing vacuously on a runtime with no zone data.
         *
         * A container built without ICU resolves *nothing*, so every case would expect a date, get
         * null, and fail — loudly, which is fine. The subtler failure is the reverse: if the file
         * ever came to consist only of `expected: null` cases, a runtime that knew no zones would
         * agree with all of them and prove nothing at all. This pins that most of the file is
         * positive, and that the two negatives are negative for the stated reason rather than
         * because the runtime is bare.
         */
        var resolvable = File.Cases.Count(vector => vector.Expected is not null);

        Assert.True(resolvable >= 10, $"Only {resolvable} cases expect a real date — this suite would barely test anything.");
        Assert.Contains(File.Cases, vector => vector.Expected is null);
    }

    [Fact]
    public void A_null_zone_is_declined_rather_than_thrown()
    {
        /*
         * The guard the shared file cannot express, because JSON has no null-that-is-not-null.
         *
         * The empty string throws <c>TimeZoneNotFoundException</c> here and is caught by the narrow
         * catch anyway, so the vector case for it passes either way. <b>Null throws
         * <c>ArgumentNullException</c></b>, which that catch deliberately does not handle — without
         * the guard a device sending a null zone gets a 500 instead of an order recorded as *not
         * re-priced*.
         *
         * The parameter is non-nullable and the caller is a network, which is the whole argument.
         */
        Assert.Null(BusinessDay.On(null!, DateTimeOffset.UnixEpoch));
        Assert.Null(BusinessDay.On("   ", DateTimeOffset.UnixEpoch));
    }

    private static VectorFile Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors", "pricing", "business-day.v1.json");

        return JsonSerializer.Deserialize<VectorFile>(
                   System.IO.File.ReadAllText(path),
                   new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidOperationException($"{path} deserialized to null");
    }

    private sealed record VectorFile(int Version, IReadOnlyList<DayVector> Cases);

    private sealed record DayVector(
        string Name, DateTimeOffset At, string TimeZoneId, string? Expected);
}
