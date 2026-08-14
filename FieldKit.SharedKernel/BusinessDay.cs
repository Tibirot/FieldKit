namespace FieldKit.SharedKernel;

/// <summary>
/// Which calendar day an instant falls on, somewhere (<c>BR-PRD-6</c>) — W11½ R6b.
/// </summary>
/// <remarks>
/// <para>
/// <b>A business day is a date with no instant in it, and it starts at a different moment in every
/// place.</b> A price list, a promotion window and a tax rate all run by calendar day, so "which
/// day" has to be answered before any of them can be. Getting it from a clock is what this exists to
/// stop: the two sides of this system answered it differently for a whole phase — the device by the
/// rep's phone, the server by Greenwich — and the disagreement only showed for orders taken late at
/// night (regression F6).
/// </para>
/// <para>
/// <b>Here rather than in Outlets, for the reason <see cref="Money"/> is here.</b> It is a rule the
/// device implements too, in <c>lib/pricing/business-day.ts</c>, and the pair is pinned by
/// <c>vectors/pricing/business-day.v1.json</c>. A rule with a mirror belongs where the mirror can be
/// held to it — inside a module, the vector suite could not reach it, and the two implementations
/// would agree only by inspection.
/// </para>
/// <para>
/// <b>It takes a zone rather than finding one.</b> Whose day it is — the shop's, the rep's, the
/// tenant's — is a question this type deliberately has no opinion about, and answering it here is
/// how a rule of this kind acquires a caller it was never designed for. `IOutletCalendar` decides it
/// is the shop's; the round decides it is the rep's, and does not use this.
/// </para>
/// </remarks>
public static class BusinessDay
{
    /// <summary>
    /// The date <paramref name="at"/> falls on in <paramref name="timeZoneId"/>, or <c>null</c> when
    /// this runtime cannot resolve that zone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null rather than a fallback to UTC.</b> An unrecognised zone means the database that
    /// accepted the value and the one reading it disagree — .NET and V8 do not ship identical zone
    /// data, and neither does one Linux image versus another. Answering UTC would silently reinstate
    /// the defect this type exists to remove, for exactly the shops nobody had noticed, and it would
    /// look like the rule working. The caller declines instead.
    /// </para>
    /// <para>
    /// IANA names on every platform this runs on: native on Linux, converted through ICU by .NET 6+
    /// on Windows. That is why the column stores IANA — the device has no other vocabulary.
    /// </para>
    /// </remarks>
    public static DateOnly? On(string timeZoneId, DateTimeOffset at)
    {
        /*
         * Rejected before <c>FindSystemTimeZoneById</c> sees it.
         *
         * <b>Measured rather than assumed.</b> The empty string *throws*
         * <c>TimeZoneNotFoundException</c> on the runtime this was written against, so that half
         * would be caught below anyway. <b>Null does not</b>: it throws
         * <c>ArgumentNullException</c>, which the deliberately narrow catch below does not handle —
         * so without this line a null zone would surface as a 500 rather than as "cannot say".
         *
         * The parameter is non-nullable, so null should be impossible. It arrives over a wire from a
         * device, and the device's own copy of this field is one migration old (W11½ R6a) — a guard
         * against a value the type forbids is worth it when the caller is a network and the failure
         * is an exception rather than an answer.
         */
        if (string.IsNullOrWhiteSpace(timeZoneId)) return null;

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        }
        catch (Exception exception)
            when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Caught narrowly on purpose: an unknown name and a corrupt zone file are both "this
            // runtime cannot answer", and anything else is a bug that should not be swallowed.
            return null;
        }
    }
}
