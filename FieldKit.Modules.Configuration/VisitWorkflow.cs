using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Configuration;

/// <summary>One step of a channel's visit workflow (<c>VIS-03</c>).</summary>
public sealed class VisitWorkflowStep : ITenantOwned
{
    /// <summary>The column width for a step's label.</summary>
    public const int MaximumLabelLength = 100;

    public Guid Id { get; private set; }

    public Guid VisitWorkflowId { get; private set; }

    /// <summary>Where it sits in the sequence. Contiguous from 1 — see <see cref="VisitWorkflow"/>.</summary>
    public int Order { get; private set; }

    public VisitStepType Type { get; private set; }

    /// <summary>Whether check-out is refused while it is open (<c>BR-VIS-3</c>).</summary>
    public bool Mandatory { get; private set; }

    /// <summary>What an admin called it. For the rep's screen, never for matching.</summary>
    public string Label { get; private set; } = null!;

    public TenantId TenantId { get; set; }

    private VisitWorkflowStep() { } // EF

    internal static VisitWorkflowStep Create(
        Guid workflowId, int order, VisitStepType type, bool mandatory, string label) => new()
    {
        Id = Guid.CreateVersion7(),
        VisitWorkflowId = workflowId,
        Order = order,
        Type = type,
        Mandatory = mandatory,
        Label = label.Trim(),
    };
}

/// <summary>
/// How visits are worked in one channel (<c>VIS-03</c>, <c>A1</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The steps are replaced wholesale, never patched.</b> The same call
/// <c>Outlet.SetContacts</c> and a role's permissions make, and for the same reason: a workflow is
/// an <i>ordered</i> thing, so a patch would need the caller to know the current order to say
/// anything about it, and two admins editing one channel would silently interleave into a sequence
/// neither of them designed.
/// </para>
/// <para>
/// <b>Order is assigned here, not accepted from the caller.</b> A client that sends its own numbers
/// can send 1, 2, 2, 7 — and every consumer then has to decide what a gap or a tie means. Taking the
/// position in the submitted list as the truth makes "the order" exactly what the admin saw on the
/// screen, and makes it impossible to express anything else.
/// </para>
/// </remarks>
public sealed class VisitWorkflow : AggregateRoot, ITenantOwned, IAuditable, ISyncTracked
{
    /// <summary>
    /// Set by the row-version interceptor, never here (ADR-0013).
    /// </summary>
    /// <remarks>
    /// <b>On the root, and that is enough because the steps have no path of their own.</b> Every
    /// edit goes through <see cref="Set"/>, which writes <c>ModifiedAtUtc</c> and therefore marks
    /// this row modified whatever the steps did — so a workflow whose only change was a reordered
    /// step still gets a new version. A step is not <c>ISyncTracked</c> on purpose: it is not
    /// something a device holds separately, it is part of the workflow it arrives with.
    /// </remarks>
    public long RowVersion { get; set; }

    /// <summary>
    /// The most steps one visit can ask for.
    /// </summary>
    /// <remarks>
    /// A sanity bound. A rep works this list standing in a shop with a phone in one hand; a workflow
    /// of eighty steps is a configuration mistake, and the cost of finding out is a rep's afternoon.
    /// </remarks>
    public const int MaximumSteps = 30;

    private readonly List<VisitWorkflowStep> _steps = [];

    public Guid Id { get; private set; }

    /// <summary>The channel this is for. Unique within the tenant — one channel, one workflow.</summary>
    public Guid ChannelId { get; private set; }

    /// <summary>
    /// Whether being at the outlet is part of what this visit is (<c>BR-VIS-2</c>).
    /// </summary>
    /// <remarks>
    /// Defaults to true, and stays true unless a tenant says otherwise. Getting this backwards is
    /// worse in one direction than the other: presence expected on a remote channel records an
    /// exception for every ordinary call, but presence *not* expected on a store channel silently
    /// stops recording the one thing the rule exists to capture.
    /// </remarks>
    public bool PresenceExpected { get; private set; } = true;

    public IReadOnlyList<VisitWorkflowStep> Steps => _steps;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private VisitWorkflow() { } // EF

    public static VisitWorkflow Create(
        Guid channelId, bool presenceExpected, IEnumerable<(VisitStepType Type, bool Mandatory, string Label)> steps)
    {
        var workflow = new VisitWorkflow
        {
            Id = Guid.CreateVersion7(),
            ChannelId = channelId,
            PresenceExpected = presenceExpected,
        };

        workflow.Replace(steps);

        return workflow;
    }

    public void Set(
        bool presenceExpected,
        IEnumerable<(VisitStepType Type, bool Mandatory, string Label)> steps,
        IClock clock)
    {
        PresenceExpected = presenceExpected;
        Replace(steps);
        ModifiedAtUtc = clock.UtcNow;
    }

    /// <summary>This workflow as another module sees it.</summary>
    public VisitWorkflowDescriptor Describe() => new(
        ChannelId,
        PresenceExpected,
        [.. _steps
            .OrderBy(step => step.Order)
            .Select(step => new VisitStepDescriptor(step.Order, step.Type, step.Mandatory, step.Label))]);

    /// <summary>The default for a channel nobody has configured: no steps, presence expected.</summary>
    /// <remarks>
    /// Here rather than in the caller so there is one answer to "what does an unconfigured channel
    /// do", and so the safe direction is chosen once. See <see cref="IVisitWorkflow"/> for why it is
    /// a default rather than a null.
    /// </remarks>
    public static VisitWorkflowDescriptor DefaultFor(Guid channelId) => new(channelId, true, []);

    private void Replace(IEnumerable<(VisitStepType Type, bool Mandatory, string Label)> steps)
    {
        _steps.Clear();

        var order = 1;

        foreach (var (type, mandatory, label) in steps)
        {
            _steps.Add(VisitWorkflowStep.Create(Id, order++, type, mandatory, label));
        }
    }
}
