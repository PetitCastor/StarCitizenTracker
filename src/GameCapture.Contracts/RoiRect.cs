namespace GameCapture.Contracts;

/// <summary>Plain rectangle in reference or frame pixel space (no WinRT so plugins stay portable).</summary>
public readonly record struct RoiRect(uint X, uint Y, uint Width, uint Height);
