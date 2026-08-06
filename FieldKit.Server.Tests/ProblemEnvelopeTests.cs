using System.Text.Json;
using FieldKit.Web;

namespace FieldKit.Server.Tests;

/// <summary>
/// The refusal envelope's wire shape (<c>ADR-0012</c>, api-contracts §3).
/// </summary>
/// <remarks>
/// Serialization rather than behaviour, because the envelope <i>is</i> the contract: a client
/// branches on it, and a field that appears when it should not — or a code that quietly changes
/// name — breaks callers without failing anything else in the suite.
/// <para>
/// These serialize <see cref="FieldProblem"/> directly instead of driving an endpoint, because no
/// endpoint emits a code yet. That is the point of this slice: the envelope gains the capability
/// first, modules use it after.
/// </para>
/// </remarks>
public class ProblemEnvelopeTests
{
    private static string Serialize(FieldProblem problem) =>
        JsonSerializer.Serialize(problem, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    [Fact]
    public void A_problem_without_a_code_serializes_exactly_as_it_did_before()
    {
        // The compatibility guarantee the whole staged migration rests on. Every endpoint emits this
        // shape today, and none of them change in this PR — so if `code` or `args` appeared here as
        // nulls, every existing client and every .http response would change shape for a feature
        // nothing uses yet.
        var json = Serialize(new FieldProblem("name", "A channel needs a name."));

        Assert.Equal("""{"field":"name","message":"A channel needs a name."}""", json);
    }

    [Fact]
    public void A_problem_with_a_code_carries_it_alongside_the_english_fallback()
    {
        // The message deliberately has no apostrophe. Real refusals do — "A channel named 'Modern
        // Trade' already exists." — and the default encoder escapes it to ', which is valid
        // JSON that decodes correctly. Asserting the escaped form here would make this test about
        // the encoder rather than about the envelope, and it would break the day anyone configured
        // a laxer one for an unrelated reason.
        var json = Serialize(new FieldProblem(
            "name",
            "A channel named Modern Trade already exists.",
            "channel.name.taken",
            new Dictionary<string, string> { ["name"] = "Modern Trade" }));

        Assert.Equal(
            """{"field":"name","message":"A channel named Modern Trade already exists.","code":"channel.name.taken","args":{"name":"Modern Trade"}}""",
            json);
    }

    [Fact]
    public void A_code_without_arguments_omits_args_rather_than_sending_an_empty_object()
    {
        // Not every message interpolates. `"args": {}` would be noise a client has to ignore, and
        // the difference between "no arguments" and "arguments I could not compute" should not be
        // something a caller has to guess at.
        var json = Serialize(new FieldProblem("name", "A channel needs a name.", "channel.name.required"));

        Assert.Equal(
            """{"field":"name","message":"A channel needs a name.","code":"channel.name.required"}""",
            json);
    }

    [Fact]
    public void A_problem_about_the_whole_request_still_has_a_null_field()
    {
        // `field: null` is meaningful — it tells a form to show the message at the top rather than
        // beside a control — so unlike `code` and `args` it is never omitted.
        var json = Serialize(new FieldProblem(null, "The file has no header row.", "outlet.import.headerMissing"));

        Assert.Equal(
            """{"field":null,"message":"The file has no header row.","code":"outlet.import.headerMissing"}""",
            json);
    }

    [Fact]
    public void Argument_values_are_strings_so_the_browser_cannot_coerce_them_to_float()
    {
        // The same reasoning that puts Money on the wire as a string (BR-PRD-8). A numeric arg would
        // arrive in JavaScript as float64, and the first place that bites is a pricing message
        // quoting an amount — which W6 is about to start writing.
        var problem = new FieldProblem(
            "customFields.chiller_count",
            "'chiller_count' must be at most 50.",
            "outlet.customField.tooLarge",
            new Dictionary<string, string> { ["max"] = "50" });

        Assert.Contains("""{"max":"50"}""", Serialize(problem));
    }
}
