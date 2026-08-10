using System.Reflection;

namespace LoraHotswap.Tests;

/// <summary>
/// NFR6: every port method is async and accepts a CancellationToken.
///
/// The rules pass vacuously against Core.Ports today -- no port exists yet, and
/// that is the intended state. Because a vacuous pass is indistinguishable from
/// a broken scanner, the scanner is separately proven against deliberately
/// non-conforming fixtures below. Without that proof these tests would assert
/// nothing at all until Story 2.1.
/// </summary>
public class PortConventionTests
{
    private static Assembly Ports => Assembly.Load("LoraHotswap.Core.Ports");

    // --- the rules, applied to the real ports assembly ----------------------

    [Fact]
    public void Port_methods_are_async()
    {
        var offenders = NonAsync(PortInterfacesIn(Ports));

        Assert.True(offenders.Count == 0,
            "Port methods must return Task, Task<T>, ValueTask, or ValueTask<T>:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Port_methods_accept_a_cancellation_token()
    {
        var offenders = WithoutCancellationToken(PortInterfacesIn(Ports));

        Assert.True(offenders.Count == 0,
            "Port methods must accept a CancellationToken:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // --- proof that the scanner detects what it claims to --------------------

    [Fact]
    public void Scanner_detects_a_synchronous_method()
    {
        Assert.Contains(
            NonAsync([typeof(Fixtures.ISynchronousPort)]),
            o => o.Contains(nameof(Fixtures.ISynchronousPort.Run)));
    }

    [Fact]
    public void Scanner_detects_a_missing_cancellation_token()
    {
        Assert.Contains(
            WithoutCancellationToken([typeof(Fixtures.IUncancellablePort)]),
            o => o.Contains(nameof(Fixtures.IUncancellablePort.RunAsync)));
    }

    [Fact]
    public void Scanner_detects_offenders_on_a_non_public_interface()
    {
        // GetExportedTypes would miss this entirely.
        Assert.NotEmpty(NonAsync([typeof(Fixtures.IInternalPort)]));
    }

    [Fact]
    public void Scanner_detects_offenders_inherited_from_a_base_interface()
    {
        // Type.GetMethods on an interface does not return base-interface members.
        Assert.NotEmpty(NonAsync([typeof(Fixtures.IDerivedPort)]));
    }

    [Fact]
    public void Scanner_accepts_a_conforming_port()
    {
        Type[] conforming = [typeof(Fixtures.IConformingPort)];

        Assert.Empty(NonAsync(conforming));
        Assert.Empty(WithoutCancellationToken(conforming));
    }

    // --- scanner -------------------------------------------------------------

    private static List<string> NonAsync(IEnumerable<Type> interfaces) =>
        MethodsOf(interfaces).Where(m => !IsAwaitable(m.ReturnType)).Select(Describe).ToList();

    private static List<string> WithoutCancellationToken(IEnumerable<Type> interfaces) =>
        MethodsOf(interfaces).Where(m => !HasCancellationToken(m)).Select(Describe).ToList();

    /// <summary>All interfaces in the assembly, public or not.</summary>
    private static IEnumerable<Type> PortInterfacesIn(Assembly assembly) =>
        assembly.GetTypes().Where(t => t.IsInterface);

    private static IEnumerable<MethodInfo> MethodsOf(IEnumerable<Type> interfaces) =>
        interfaces
            // Base-interface members are part of the contract; GetMethods on an
            // interface does not return them.
            .SelectMany(t => t.GetMethods().Concat(t.GetInterfaces().SelectMany(b => b.GetMethods())))
            .Distinct()
            // Property/event accessors are excluded: a property cannot be async,
            // so the rule cannot apply to one. Static abstract members are
            // factories, not I/O boundaries.
            .Where(m => !m.IsSpecialName && !m.IsStatic);

    private static bool IsAwaitable(Type t)
    {
        if (t == typeof(Task) || t == typeof(ValueTask)) return true;
        if (!t.IsGenericType) return false;

        var definition = t.GetGenericTypeDefinition();
        return definition == typeof(Task<>) || definition == typeof(ValueTask<>);
    }

    private static bool HasCancellationToken(MethodInfo m) =>
        m.GetParameters().Any(p =>
        {
            // `in`/`ref` produce CancellationToken&; `CancellationToken?` produces
            // Nullable<CancellationToken>. Both satisfy the intent.
            var type = p.ParameterType;
            if (type.IsByRef) type = type.GetElementType()!;
            return type == typeof(CancellationToken)
                || Nullable.GetUnderlyingType(type) == typeof(CancellationToken);
        });

    private static string Describe(MethodInfo m) => $"  {m.DeclaringType?.FullName}.{m.Name}";

    // --- fixtures: deliberately non-conforming, to prove the scanner works ---

    private static class Fixtures
    {
        public interface ISynchronousPort
        {
            void Run(CancellationToken cancellationToken);
        }

        public interface IUncancellablePort
        {
            Task RunAsync();
        }

        internal interface IInternalPort
        {
            int Run(CancellationToken cancellationToken);
        }

        public interface IBasePort
        {
            string Describe(CancellationToken cancellationToken);
        }

        public interface IDerivedPort : IBasePort
        {
            Task RunAsync(CancellationToken cancellationToken);
        }

        public interface IConformingPort
        {
            Task RunAsync(CancellationToken cancellationToken);
            ValueTask<int> CountAsync(in CancellationToken cancellationToken);
        }
    }
}
