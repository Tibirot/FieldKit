using System.Text.Json.Serialization;
using FieldKit.BuildingBlocks;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Visit;

/// <summary>Where one step of a visit has got to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VisitStepStatus>))]
public enum VisitStepStatus
{
    /// <summary>Still to do. Where every step starts, and where an optional one may stay.</summary>
    Pending,

    /// <summary>Done, with the moment it was done at.</summary>
    Completed,
}

/// <summary>
/// One step of a visit, as the rep actually works it (<c>VIS-03</c>, <c>VIS-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A copy of the configured step, not a pointer to it.</b> The order, the type, the label and —
/// above all — <see cref="Mandatory"/> are stamped onto the visit at check-in and never re-read.
/// An admin who edits the channel workflow at eleven o'clock must not change what a rep who checked
/// in at ten is required to do: they would be refused check-out for a step that did not exist when
/// they started, or released from one they were told was compulsory. This is <c>BR-VIS-6</c>'s
/// snapshot rule applied to the one piece of reference data that decides whether a visit can end.
/// </para>
/// <para>
/// It is also what makes the whole thing work offline (<c>§7</c>): the device holds the visit and its
/// steps, and needs no second conversation with Configuration to know what is outstanding.
/// </para>
/// <para>
/// <b>There is no <c>Skipped</c>.</b> An optional step nobody did is one left <see
/// cref="VisitStepStatus.Pending"/>, and a mandatory one cannot be skipped at all (<c>BR-VIS-3</c>) —
/// so a third state would record the same fact twice and invite the question of what a *skipped*
/// mandatory step means.
/// </para>
/// </remarks>
public sealed class VisitStep : ITenantOwned
{
    /// <summary>The column width for what a rep typed against a step.</summary>
    public const int MaximumNotesLength = 2_000;

    public Guid Id { get; private set; }

    public Guid VisitId { get; private set; }

    /// <summary>Where it sits in the sequence. Contiguous from 1, as the workflow defined it.</summary>
    public int Order { get; private set; }

    /// <summary>
    /// Which kind of work it is — Configuration's vocabulary, because it is Configuration an admin
    /// chose it from and a second enum here would be two lists to keep in step.
    /// </summary>
    public VisitStepType Type { get; private set; }

    /// <summary>Whether check-out is refused while it is open (<c>BR-VIS-3</c>).</summary>
    public bool Mandatory { get; private set; }

    /// <summary>What the admin called it, for the rep's screen — never for matching.</summary>
    public string Label { get; private set; } = null!;

    public VisitStepStatus Status { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>What the rep typed. The whole content of a <see cref="VisitStepType.Note"/> step.</summary>
    public string? Notes { get; private set; }

    public TenantId TenantId { get; set; }

    private VisitStep() { } // EF

    internal static VisitStep From(Guid visitId, VisitStepDescriptor descriptor) => new()
    {
        Id = Guid.CreateVersion7(),
        VisitId = visitId,
        Order = descriptor.Order,
        Type = descriptor.Type,
        Mandatory = descriptor.Mandatory,
        Label = descriptor.Label,
        Status = VisitStepStatus.Pending,
    };

    /// <summary>
    /// A step as a device completed it offline — already done, with the device's own id and time.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="From"/> because the two build different things. That one creates a
    /// pending step from a workflow the server just read; this one records a step that was completed
    /// under a workflow the server may no longer have. Re-deriving the shape here would describe
    /// yesterday's visit with today's definition (W8 slice 5).
    /// </remarks>
    internal static VisitStep Ingested(
        Guid visitId, Guid stepId, int order, VisitStepType type, bool mandatory, string label,
        string? notes, DateTimeOffset completedAtUtc) => new()
        {
            Id = stepId,
            VisitId = visitId,
            Order = order,
            Type = type,
            Mandatory = mandatory,
            Label = label,
            Status = VisitStepStatus.Completed,
            CompletedAtUtc = completedAtUtc,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        };

    /// <summary>Whether this step is one <c>BR-VIS-3</c> would hold a check-out open for.</summary>
    internal bool IsOpenAndMandatory => Mandatory && Status != VisitStepStatus.Completed;

    internal void Complete(string? notes, IClock clock)
    {
        Status = VisitStepStatus.Completed;
        CompletedAtUtc = clock.UtcNow;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }
}
