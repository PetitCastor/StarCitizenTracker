namespace GameCapture.Sdk;

/// <summary>
/// The id a plugin gave one of its subscribed regions. A type rather than a bare string so a ROI id
/// and a piece of OCR text cannot be swapped at a call site: both are strings, and
/// <c>tick.TryGetText(panelText)</c> compiles perfectly well when the argument is untyped.
/// </summary>
/// <remarks>
/// The implicit conversion from string is what keeps <c>tick.TryGetText("panel")</c> and every
/// existing call site compiling; there is deliberately no conversion back, because that would undo
/// the separation in the direction that matters — a <see cref="RoiId"/> flowing into a
/// <c>string</c> parameter is exactly the mixup this type exists to catch.
/// <para>
/// Comparison is ordinal by construction (the generated record equality uses
/// <see cref="EqualityComparer{T}.Default"/> for the string), which is what the engine does with
/// the ids it echoes back — a case-insensitive match here would pair a result with a subscription
/// the engine considers a different region.
/// </para>
/// </remarks>
/// <param name="Value">The raw id, as it travels the wire.</param>
public readonly record struct RoiId(string Value)
{
    /// <summary>The raw id, as it travels the wire; never null once a constructor has run.</summary>
    public string Value { get; } = Value ?? string.Empty;

    public static implicit operator RoiId(string value) => new(value);

    /// <summary>
    /// Ordinal, and null-tolerant: <c>default(RoiId)</c> never runs a constructor, so its
    /// <see cref="Value"/> stays null however hard the constructor normalises.
    /// </summary>
    /// <remarks>
    /// Comparing through <see cref="ToString"/> is what makes <c>default(RoiId)</c> equal to
    /// <c>new RoiId("")</c>. The generated equality compares the backing fields, so it would call
    /// those two different while <see cref="ToString"/> prints both as <c>""</c> — a dictionary miss
    /// whose log line reads as an innocuous blank id, exactly the silent mismatch this type exists
    /// to prevent.
    /// </remarks>
    public bool Equals(RoiId other) => string.Equals(ToString(), other.ToString(), StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToString());

    /// <summary>The raw id, so interpolation and logging read as the plain string they used to.</summary>
    /// <remarks>
    /// Empty rather than null for <c>default(RoiId)</c>: the struct's default is reachable (an
    /// uninitialised field, a <c>default</c> in a collection) and a null here would turn a log line
    /// into a NullReferenceException.
    /// </remarks>
    public override string ToString() => Value ?? string.Empty;
}
