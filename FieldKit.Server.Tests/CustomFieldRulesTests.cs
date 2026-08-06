using System.Text.Json;
using FieldKit.Modules.Configuration.Contracts;

namespace FieldKit.Server.Tests;

/// <summary>
/// The shared custom-field rules (<c>CFG-02</c>), tested as the pure function they now are.
/// </summary>
/// <remarks>
/// These previously existed only inside Outlets and were reachable only over HTTP, so every branch
/// cost a request, a database and a Keycloak token. Extracting them to
/// <see cref="CustomFieldRules"/> made them unit-testable — which is most of the argument for the
/// extraction, and the reason this file has no fixture.
/// </remarks>
public class CustomFieldRulesTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    private static FieldDefinitionDescriptor Define(
        string key,
        CustomFieldType type,
        bool required = false,
        IReadOnlyList<string>? options = null,
        int? maxLength = null,
        double? minimum = null,
        double? maximum = null) =>
        new(key, key, type, required, options ?? [], maxLength, minimum, maximum);

    private static IReadOnlyList<CustomFieldViolation> Validate(
        Dictionary<string, JsonElement>? values,
        params FieldDefinitionDescriptor[] definitions) =>
        CustomFieldRules.Validate(values, definitions, CustomFieldEntity.Product);

    [Fact]
    public void An_undefined_key_is_rejected_rather_than_ignored()
    {
        // Silently dropping it means an import or a typo loses data with no signal — and the
        // catalogue exists precisely so what is stored is describable.
        var violations = Validate(new() { ["nope"] = Json("\"x\"") });

        var violation = Assert.Single(violations);
        Assert.Equal(CustomFieldViolationKind.Unknown, violation.Kind);
        Assert.Equal("nope", violation.Key);
    }

    [Fact]
    public void The_unknown_key_message_names_the_entity_it_was_sent_for()
    {
        // The one sentence that was outlet-specific before the extraction, and the reason `entity`
        // is a parameter rather than the rules being entity-blind.
        var forProducts = CustomFieldRules.Validate(
            new Dictionary<string, JsonElement> { ["nope"] = Json("\"x\"") }, [], CustomFieldEntity.Product);
        var forOutlets = CustomFieldRules.Validate(
            new Dictionary<string, JsonElement> { ["nope"] = Json("\"x\"") }, [], CustomFieldEntity.Outlet);

        Assert.Contains("for products.", forProducts[0].Message);
        Assert.Contains("for outlets.", forOutlets[0].Message);
    }

    [Fact]
    public void A_required_field_with_no_value_is_a_violation_and_an_optional_one_is_not()
    {
        var required = Validate(null, Define("must", CustomFieldType.Text, required: true));
        Assert.Equal(CustomFieldViolationKind.Required, Assert.Single(required).Kind);

        Assert.Empty(Validate(null, Define("may", CustomFieldType.Text)));
    }

    [Fact]
    public void An_explicit_null_counts_as_absent()
    {
        // Sending `{"must": null}` is how a form clears a field. Treating it as "present but wrong
        // type" would make clearing a required field report the wrong problem.
        var violations = Validate(
            new() { ["must"] = Json("null") }, Define("must", CustomFieldType.Text, required: true));

        Assert.Equal(CustomFieldViolationKind.Required, Assert.Single(violations).Kind);
    }

    [Theory]
    [InlineData(CustomFieldType.Text, "1")]
    [InlineData(CustomFieldType.Number, "\"1\"")]
    [InlineData(CustomFieldType.Boolean, "\"true\"")]
    [InlineData(CustomFieldType.Date, "\"04/11/2025\"")]
    public void A_value_of_the_wrong_shape_is_a_violation(CustomFieldType type, string raw)
    {
        var violations = Validate(new() { ["field"] = Json(raw) }, Define("field", type));

        Assert.Equal(CustomFieldViolationKind.WrongType, Assert.Single(violations).Kind);
    }

    [Fact]
    public void A_date_is_only_accepted_as_yyyy_MM_dd()
    {
        // Accepting a timestamp would store an instant for something the tenant means as a day.
        Assert.Empty(Validate(new() { ["when"] = Json("\"2025-11-04\"") }, Define("when", CustomFieldType.Date)));

        Assert.Equal(
            CustomFieldViolationKind.WrongType,
            Assert.Single(Validate(
                new() { ["when"] = Json("\"2025-11-04T08:00:00Z\"") },
                Define("when", CustomFieldType.Date))).Kind);
    }

    [Fact]
    public void Text_longer_than_its_maximum_reports_the_maximum()
    {
        var violations = Validate(
            new() { ["code"] = Json("\"abcdef\"") }, Define("code", CustomFieldType.Text, maxLength: 3));

        var violation = Assert.Single(violations);
        Assert.Equal(CustomFieldViolationKind.TooLong, violation.Kind);
        Assert.Equal("3", violation.Args?["max"]);
    }

    [Fact]
    public void A_choice_outside_the_permitted_set_is_a_violation_and_case_matters()
    {
        var definition = Define("size", CustomFieldType.Choice, options: ["small", "large"]);

        Assert.Empty(Validate(new() { ["size"] = Json("\"small\"") }, definition));

        // Ordinal: these are stored identifiers, and accepting "Small" would make the permitted set
        // depend on how a caller happened to type it.
        Assert.Equal(
            CustomFieldViolationKind.NotAnOption,
            Assert.Single(Validate(new() { ["size"] = Json("\"Small\"") }, definition)).Kind);
    }

    [Fact]
    public void A_number_outside_its_bounds_reports_which_bound()
    {
        var definition = Define("count", CustomFieldType.Number, minimum: 1, maximum: 50);

        Assert.Equal(
            CustomFieldViolationKind.TooSmall,
            Assert.Single(Validate(new() { ["count"] = Json("0") }, definition)).Kind);

        var tooLarge = Assert.Single(Validate(new() { ["count"] = Json("51") }, definition));
        Assert.Equal(CustomFieldViolationKind.TooLarge, tooLarge.Kind);
        Assert.Equal("50", tooLarge.Args?["max"]);
    }

    [Fact]
    public void Every_problem_is_returned_rather_than_the_first()
    {
        // An admin filling a form wants to fix everything in one pass; returning one at a time turns
        // a six-field form into six round trips.
        var violations = Validate(
            new()
            {
                ["unknown"] = Json("\"x\""),
                ["number"] = Json("\"not a number\""),
            },
            Define("number", CustomFieldType.Number),
            Define("required", CustomFieldType.Text, required: true));

        Assert.Equal(3, violations.Count);
        // Sorted, so this asserts the set rather than the order the rules happen to emit in —
        // nothing promises unknown keys come before missing required ones.
        Assert.Equal(
            [
                CustomFieldViolationKind.Unknown,
                CustomFieldViolationKind.Required,
                CustomFieldViolationKind.WrongType,
            ],
            violations.Select(v => v.Kind).Order());
    }
}
