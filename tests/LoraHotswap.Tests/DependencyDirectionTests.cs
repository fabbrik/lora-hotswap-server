using System.Reflection;
using System.Xml.Linq;

namespace LoraHotswap.Tests;

/// <summary>
/// The dependency rule from the Architecture Spine:
///
///     Api            -> Core, Core.Ports, Infrastructure
///     Infrastructure -> Core, Core.Ports
///     Core           -> Core.Ports
///     Core.Ports     -> (nothing)
///
/// The primary assertions read the project reference graph. The compiler elides
/// assembly references that no code uses, so an assembly-level check alone would
/// pass vacuously while the projects are still empty -- proving nothing at
/// exactly the point this rule matters most. The assembly check runs too, as a
/// second net that catches references arriving by other means once code exists.
///
/// Known limits, recorded in deferred-work.md: only direct references are
/// inspected (no transitive traversal), and references injected through
/// Directory.Build.props or MSBuild property expansion are invisible to XML
/// parsing.
/// </summary>
public class DependencyDirectionTests
{
    private const string Core = "LoraHotswap.Core";
    private const string Ports = "LoraHotswap.Core.Ports";
    private const string Infrastructure = "LoraHotswap.Infrastructure";
    private const string Api = "LoraHotswap.Api";

    [Theory]
    // Core may only reach Ports.
    [InlineData(Core, Infrastructure)]
    [InlineData(Core, Api)]
    // Ports is the innermost layer and reaches nothing.
    [InlineData(Ports, Core)]
    [InlineData(Ports, Infrastructure)]
    [InlineData(Ports, Api)]
    // Infrastructure is a driven adapter; depending on the driving adapter
    // inverts the hexagon.
    [InlineData(Infrastructure, Api)]
    public void Project_does_not_reference(string project, string forbidden)
    {
        var referenced = ProjectReferencesOf(project);

        Assert.False(
            referenced.Contains(forbidden),
            $"{project} must not reference {forbidden}. " +
            $"Current references: {(referenced.Count == 0 ? "(none)" : string.Join(", ", referenced.Order()))}.");
    }

    [Fact]
    public void Core_references_only_ports()
    {
        Assert.Equal([Ports], ProjectReferencesOf(Core).Order());
    }

    [Fact]
    public void Ports_references_no_projects()
    {
        Assert.Empty(ProjectReferencesOf(Ports));
    }

    [Fact]
    public void Ports_references_no_packages()
    {
        // "Depends on nothing" covers NuGet as well. A package reference in the
        // innermost layer is exactly the contamination the hexagon exists to
        // prevent, and the ProjectReference check would not see it.
        Assert.Empty(PackageReferencesOf(Ports));
    }

    [Theory]
    [InlineData(Core, Infrastructure)]
    [InlineData(Core, Api)]
    [InlineData(Ports, Core)]
    [InlineData(Ports, Infrastructure)]
    [InlineData(Ports, Api)]
    [InlineData(Infrastructure, Api)]
    public void Assembly_does_not_reference(string assemblyName, string forbidden)
    {
        // Second net. Vacuous while the projects hold no code -- the compiler
        // emits no reference it does not need -- but it catches references that
        // arrive without a ProjectReference once there is code to use them.
        var referenced = Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.False(
            referenced.Contains(forbidden),
            $"Assembly {assemblyName} must not reference {forbidden}.");
    }

    private static HashSet<string> ProjectReferencesOf(string project) =>
        ItemIncludesOf(project, "ProjectReference")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

    private static HashSet<string> PackageReferencesOf(string project) =>
        ItemIncludesOf(project, "PackageReference")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> ItemIncludesOf(string project, string itemName)
    {
        var path = Path.Combine(SolutionRoot(), "src", project, $"{project}.csproj");
        Assert.True(File.Exists(path), $"Expected project file at {path}");

        return XDocument.Load(path)
            .Descendants()
            // Match on local name so a legacy csproj carrying the old MSBuild
            // xmlns is not silently read as having zero references, and
            // case-insensitively because MSBuild itself is.
            .Where(e => string.Equals(e.Name.LocalName, itemName, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Attributes()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, "Include", StringComparison.OrdinalIgnoreCase))
                ?.Value ?? string.Empty)
            // MSBuild accepts semicolon-separated includes in one attribute.
            .SelectMany(include => include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(include => include.Replace('\\', Path.DirectorySeparatorChar));
    }

    private static string SolutionRoot()
    {
        // .NET 10's `dotnet new sln` emits the XML `.slnx` format. Both names are
        // checked explicitly rather than by wildcard: `sln?` would also match
        // `.slnf` solution filters and stop the walk in the wrong directory.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null
               && !File.Exists(Path.Combine(dir.FullName, "LoraHotswap.slnx"))
               && !File.Exists(Path.Combine(dir.FullName, "LoraHotswap.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null,
            $"Could not find the solution root walking up from {AppContext.BaseDirectory}. " +
            "These tests assume the suite runs from within the repository.");
        return dir.FullName;
    }
}
