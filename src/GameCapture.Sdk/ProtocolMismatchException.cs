namespace GameCapture.Sdk;

/// <summary>
/// The engine and this SDK do not speak a common protocol version. Not retryable: the versions are
/// fixed for the life of both processes, so a host that loops on this loops forever. The supported
/// range is carried as data because the useful thing to tell the user is which side to upgrade.
/// </summary>
public sealed class ProtocolMismatchException : GameCaptureException
{
    public ProtocolMismatchException(uint engineMin, uint engineMax, uint sdkVersion,
        Exception? innerException = null)
        : base(Describe(engineMin, engineMax, sdkVersion), innerException)
    {
        EngineMin = engineMin;
        EngineMax = engineMax;
        SdkVersion = sdkVersion;
    }

    /// <summary>Oldest protocol version the engine still speaks.</summary>
    public uint EngineMin { get; }

    /// <summary>Newest protocol version the engine speaks. Zero means the engine predates
    /// negotiation entirely and reported no range at all.</summary>
    public uint EngineMax { get; }

    /// <summary>What this SDK announced — <see cref="GameCapture.Contracts.ProtocolVersion.Current"/> in production.</summary>
    public uint SdkVersion { get; }

    private static string Describe(uint engineMin, uint engineMax, uint sdkVersion)
        => engineMax == 0
            ? $"The engine predates protocol negotiation (it reports no supported range); this SDK speaks protocol {sdkVersion}."
            : $"The engine speaks protocol {engineMin}-{engineMax}; this SDK speaks {sdkVersion}.";
}
