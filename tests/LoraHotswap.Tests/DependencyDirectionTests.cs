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
/// These assertions read the project reference graph rather than assembly
/// metadata. The compiler elides assembly references that no code uses, so an
/// assembly-level check would pass vacuously while the projects are still empty
/// -- proving nothing at exactly the point this rule matters most.
/// </summary>
public class DependencyDirectionTests
{
    [Theory]
    [InlineData("LoraHotswap.Core", "LoraHotswap.Infrastructure")]
    [InlineData("LoraHotswap.Core", "LoraHotswap.Api")]
    [InlineData("LoraHotswap.Core.Ports", "LoraHotswap.Core")]
    [InlineData("LoraHotswap.Core.Ports", "LoraHotswap.Infrastructure")]
    [InlineData("LoraHotswap.Core.Ports", "LoraHotswap.Api")]
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
        Assert.Equal(new[] { "LoraHotswap.Core.Ports" }, ProjectReferencesOf("LoraHotswap.Core").Order());
    }

    [Fact]
    public void Ports_references_nothing()
    {
        Assert.Empty(ProjectReferencesOf("LoraHotswap.Core.Ports"));
    }

    private static HashSet<string> ProjectReferencesOf(string project)
    {
        var path = Path.Combine(SolutionRoot(), "src", project, $"{project}.csproj");
        Assert.True(File.Exists(path), $"Expected project file at {path}");

        return XDocument.Load(path)
            .Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', Path.DirectorySeparatorChar)))
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet();
    }

    private static string SolutionRoot()
    {
        // .NET 10's `dotnet new sln` emits the XML `.slnx` format, not `.sln`.
        // Accept either so this keeps working whichever format the repo carries.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !dir.EnumerateFiles("LoraHotswap.sln?").Any()
                               && !dir.EnumerateFiles("LoraHotswap.sln").Any())
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null,
            $"Could not find the solution root walking up from {AppContext.BaseDirectory}.");
        return dir.FullName;
    }
}
