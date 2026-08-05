using FieldKit.Web;

namespace FieldKit.Modules.Outlets;

/// <summary>
/// Checks the people attached to an outlet before they reach the database (<c>OUT-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// The column widths were the only thing standing here, which made a 201-character name a
/// <c>DbUpdateException</c> and a <c>500</c> — the API telling the caller their request had broken
/// it. A contact with no name at all was worse: nothing refused it, so it stored, and the outlet
/// grew a row saying a person exists without saying who.
/// </para>
/// <para>
/// Every problem is reported at once, and each is named by the path it arrived under —
/// <c>contacts[1].email</c>. A form showing three contacts needs to know <i>which</i> one it is
/// being told about, and "not an email address" over a list of three sends someone hunting.
/// </para>
/// </remarks>
internal static class ContactValidator
{
    private const int NameMax = 200;
    private const int RoleMax = 100;
    private const int PhoneMax = 50;

    /// <summary>RFC 5321's maximum, which is what the column is sized to.</summary>
    private const int EmailMax = 320;

    public static IReadOnlyList<FieldProblem> Validate(IReadOnlyList<OutletContact>? contacts)
    {
        if (contacts is null or []) return [];

        var problems = new List<FieldProblem>();

        for (var index = 0; index < contacts.Count; index++)
        {
            var contact = contacts[index];

            // A name is the whole point of the record — it is what a rep says at the counter. The
            // rest is how to reach the person, and any of it may simply not be known yet.
            if (string.IsNullOrWhiteSpace(contact.Name))
            {
                problems.Add(Problem(index, "name", "A contact needs a name."));
            }
            else if (contact.Name.Length > NameMax)
            {
                problems.Add(Problem(index, "name", TooLong("name", NameMax)));
            }

            if (contact.Role?.Length > RoleMax)
            {
                problems.Add(Problem(index, "role", TooLong("role", RoleMax)));
            }

            if (contact.Phone?.Length > PhoneMax)
            {
                problems.Add(Problem(index, "phone", TooLong("phone", PhoneMax)));
            }

            if (contact.Email is { Length: > EmailMax })
            {
                problems.Add(Problem(index, "email", TooLong("email", EmailMax)));
            }
            else if (!string.IsNullOrWhiteSpace(contact.Email) && !LooksLikeAnAddress(contact.Email))
            {
                problems.Add(Problem(index, "email", $"'{contact.Email}' is not an email address."));
            }
        }

        return problems;
    }

    /// <summary>
    /// Whether this could be an email address at all.
    /// </summary>
    /// <remarks>
    /// Deliberately shallow: something either side of exactly one <c>@</c>, and no whitespace. The
    /// point is to catch a phone number pasted into the wrong box, not to decide deliverability —
    /// that question is only ever answered by sending mail, and a stricter rule rejects addresses
    /// that work. Whatever passes here still fits the column, which is the part that used to fail
    /// loudly.
    /// </remarks>
    private static bool LooksLikeAnAddress(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);

        return at > 0
            && at == email.LastIndexOf('@')
            && at < email.Length - 1
            && !email.Any(char.IsWhiteSpace);
    }

    private static string TooLong(string what, int max) =>
        $"A contact's {what} is at most {max} characters.";

    /// <summary>
    /// Names the field by the path the caller sent it under — <c>contacts[0].name</c>.
    /// </summary>
    /// <remarks>
    /// The index matters as much as the field does. Which of the three people on this outlet has no
    /// name is not something a client can work out from the message.
    /// </remarks>
    private static FieldProblem Problem(int index, string field, string message) =>
        new($"contacts[{index}].{field}", message);
}
