namespace RefineryPlugin.Orders;

/// <summary>
/// Whether a materials read is trustworthy. <see cref="Complete"/> requires the COMPLETED-panel
/// checksum (sum of row yields == printed total) to pass on a non-occluded frame with no
/// edge-dropped rows; <see cref="Partial"/> is a checksum mismatch or dropped rows; <see cref="Unknown"/>
/// is an occluded read (Confirm modal covering the list) that must never be promoted to Complete.
/// </summary>
public enum Completeness { Complete, Partial, Unknown }
