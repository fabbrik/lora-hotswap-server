using System.Reflection;

namespace LoraHotswap.Tests;

/// <summary>
/// NFR6: every port method is async and accepts a CancellationToken.
///
/// This passes vacuously today because no port exists yet -- that is the
/// intended state. The rule is declared now so the first port added (Story 2.1's
/// IInferenceRuntime) inherits it rather than negotiating it.
/// </summary>
public class PortConventionTests
{
    // Loaded by name rather than through a marker type, so Core.Ports stays free
    // of types that exist only to be pointed at. The project reference copies the
    // assembly into the test output, so it resolves.
    private static readonly Assembly Ports = Assembly.Load("LoraHotswap.Core.Ports");

    [Fact]
    public void Port_methods_are_async()
    {
        var offenders = PortMethods()
            .Where(m => !IsAwaitable(m.ReturnType))
            .Select(Describe)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Port methods must return Task, Task<T>, ValueTask, ValueTask<T>, or an async stream:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Port_methods_accept_a_cancellation_token()
    {
        var offenders = PortMethods()
            .Where(m => m.GetParameters().All(p => p.ParameterType != typeof(CancellationToken)))
            .Select(Describe)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Port methods must accept a CancellationToken:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static IEnumerable<MethodInfo> PortMethods() =>
        Ports.GetExportedTypes()
            .Where(t => t.IsInterface)
            .SelectMany(t => t.GetMethods())
            .Where(m => !m.IsSpecialName);

    private static bool IsAwaitable(Type t)
    {
        if (t == typeof(Task) || t == typeof(ValueTask)) return true;
        if (!t.IsGenericType) return false;

        var definition = t.GetGenericTypeDefinition();
        return definition == typeof(Task<>)
            || definition == typeof(ValueTask<>)
            || definition == typeof(IAsyncEnumerable<>);
    }

    private static string Describe(MethodInfo m) =>
        $"  {m.DeclaringType?.FullName}.{m.Name}";
}
