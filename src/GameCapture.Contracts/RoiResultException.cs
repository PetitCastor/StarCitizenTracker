namespace GameCapture.Contracts;

/// <summary>
/// A <c>RoiResult</c> could not be turned into a usable result: the engine flagged the ROI as
/// failed, or the payload violates the wire invariants.
/// </summary>
/// <remarks>
/// This exists so a failed ROI can never masquerade as a successful empty read. An errored
/// TEXT result carries <c>text = ""</c>, and a state machine that reads an empty panel header
/// as "panel closed" would conclude an in-progress order finished. Plugins that want to skip
/// failed ROIs instead of catching should test <c>RoiResult.Error</c> (or use the Try* mapping
/// overloads) at the top of their tick handler.
/// </remarks>
public sealed class RoiResultException : Exception
{
    /// <summary>The <c>roi_id</c> of the result that failed, as sent by the engine.</summary>
    public string RoiId { get; }

    /// <summary>True when the engine itself reported the failure, false when the payload was malformed.</summary>
    public bool ReportedByEngine { get; }

    public RoiResultException(string roiId, string message, bool reportedByEngine)
        : base(message)
    {
        RoiId = roiId;
        ReportedByEngine = reportedByEngine;
    }
}
