namespace GameCapture.Contracts;

/// <summary>
/// Where the engine and its plugins meet. Not a payload limit like <see cref="WireLimits"/>, but
/// the same kind of thing: a value two separately-shipped processes must agree on exactly, and
/// which therefore cannot live as a literal in each of them.
/// </summary>
public static class PipeContract
{
    /// <summary>
    /// Pipe the engine listens on unless its config overrides it.
    /// </summary>
    /// <remarks>
    /// One definition because a drifted copy is undiagnosable from either side: connecting to a
    /// pipe nobody serves blocks rather than failing, so a rename applied to the engine but not
    /// the SDK surfaces only as a connect timeout, with nothing in it pointing at the name.
    /// </remarks>
    public const string DefaultPipeName = "GameCapture.Engine";
}
