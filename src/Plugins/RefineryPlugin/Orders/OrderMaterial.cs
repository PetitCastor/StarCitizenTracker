namespace RefineryPlugin.Orders;

/// <summary>
/// One material line of a work order, in integer cSCU (the raw on-screen unit). The ledger stores
/// cSCU rather than the SETUP path's decimal SCU so that rounding drift can never split a record's
/// identity or perturb a match score — divide by 100 only when displaying.
/// </summary>
/// <param name="Quality">
/// The per-row QUALITY value shown on every panel. It is part of the material's identity: a single
/// work order can list the same material name twice at different qualities (e.g. two TORITE rows,
/// quality 262 and 785), so name alone would collapse them.
/// </param>
public sealed record OrderMaterial(string Name, int Quality, int QtyCscu, int YieldCscu, bool RefineOn);
