namespace Sidequest.Application.Administration;

/// <summary>Deployment-only gate for consuming departure evidence written by a separately authorized verification process.</summary>
/// <remarks>This is not evidence of approval. Operators must approve and protect that process before enabling the gate.
/// No application administration command writes account eligibility or departure evidence.</remarks>
public sealed class DepartureRecoveryPolicy
{
    /// <summary>Whether deployment operators have enabled recovery after approving the external verification process; defaults closed.</summary>
    public bool Enabled { get; set; }

    /// <summary>Approved operational procedure reference, retained in each recovery audit, not a secret or user-editable setting.</summary>
    public string ProcedureReference { get; set; } = "";
}
