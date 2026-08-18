using System.Reflection;
using Xunit;

namespace GameCapture.Sdk.Tests;

/// <summary>
/// The SDK's whole reason to exist, as a test: a plugin must be writable against this assembly
/// alone. The moment a generated proto type or a gRPC type appears in an exported signature, the
/// plugin author has to reference the contracts, learn the wire, and recompile when it changes —
/// and that happens one convenient return type at a time, not as a decision anyone makes.
/// </summary>
public class PublicSurfaceTests
{
    /// <summary>
    /// Namespaces a plugin must never have to name. <c>GameCapture.Contracts</c> itself is NOT here:
    /// <c>RoiRect</c>, <c>OcrRegionResult</c>, <c>PixelPatchSampler</c> and <c>RoiScaler</c> live
    /// there and are hand-written shared types the SDK deliberately re-exports — the codegen sits
    /// under <c>GameCapture.Contracts.Proto</c>, which is.
    /// </summary>
    private static readonly string[] Forbidden =
        ["GameCapture.Contracts.Proto", "Grpc", "Google.Protobuf"];

    [Fact]
    public void PublicSurface_LeaksNoTransportTypes()
    {
        var offenders = typeof(GameCapturePluginHost).Assembly.GetExportedTypes()
            .SelectMany(AllPublicMemberSignatureTypes)
            .Where(IsTransportType)
            .Select(t => t.FullName)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    private static bool IsTransportType(Type type)
    {
        var ns = type.Namespace;
        if (ns is null)
            return false;

        // Prefix match with a boundary check: "Grpc" must catch Grpc.Core without also catching a
        // namespace that merely starts with those letters.
        return Forbidden.Any(f => ns == f || ns.StartsWith(f + ".", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every type reachable from a type's public surface: what it inherits, what it implements, and
    /// the parameter/return/field types of everything callable on it. Generic arguments are walked
    /// too — a <c>Task&lt;StatusResponse&gt;</c> leaks exactly as much as a bare one.
    /// </summary>
    private static IEnumerable<Type> AllPublicMemberSignatureTypes(Type type)
    {
        IEnumerable<Type> Declared()
        {
            if (type.BaseType is { } baseType)
                yield return baseType;

            foreach (var iface in type.GetInterfaces())
                yield return iface;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var member in type.GetMembers(flags))
            {
                switch (member)
                {
                    case MethodBase method:
                        // Protected members count: a plugin deriving from an SDK type has to name
                        // them, which is the same coupling by another route.
                        if (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly)
                        {
                            if (method is MethodInfo m)
                                yield return m.ReturnType;

                            foreach (var p in method.GetParameters())
                                yield return p.ParameterType;
                        }
                        break;

                    case PropertyInfo property:
                        yield return property.PropertyType;
                        break;

                    case FieldInfo field:
                        yield return field.FieldType;
                        break;

                    case EventInfo evt when evt.EventHandlerType is { } handler:
                        yield return handler;
                        break;
                }
            }
        }

        return Declared().SelectMany(Expand);
    }

    /// <summary>A type plus everything it is made of: generic arguments, array/by-ref elements.</summary>
    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;

        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var inner in Expand(element))
                yield return inner;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var inner in Expand(argument))
                yield return inner;
        }
    }
}
