using System.Reflection;

namespace Relay.ArchitectureTests;

/// <summary>
/// Dependencies point inward: Domain &lt;- Application &lt;- Infrastructure and Api.
/// These tests read the compiled assembly references, so they catch a stray
/// dependency even if it sneaks in through a project file edit.
/// </summary>
public class LayeringTests
{
    private static string[] RelayReferencesOf(string assemblyName) =>
        Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Relay.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void Domain_references_no_other_Relay_project() =>
        Assert.Empty(RelayReferencesOf("Relay.Domain"));

    [Fact]
    public void Domain_references_no_framework_beyond_the_base_library()
    {
        var references = Assembly.Load("Relay.Domain")
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Microsoft.", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(references);
    }

    [Theory]
    [InlineData("Relay.Application")]
    [InlineData("Relay.Guard")]
    public void Inner_layers_do_not_reference_outer_layers(string assemblyName)
    {
        string[] outer = ["Relay.Infrastructure", "Relay.Api", "Relay.Worker", "Relay.Mcp"];

        Assert.Empty(RelayReferencesOf(assemblyName).Intersect(outer));
    }

    [Theory]
    [InlineData("Relay.Infrastructure")]
    [InlineData("Relay.Worker")]
    [InlineData("Relay.Guard")]
    [InlineData("Relay.Mcp")]
    public void Nothing_but_the_host_references_the_Api(string assemblyName) =>
        Assert.DoesNotContain("Relay.Api", RelayReferencesOf(assemblyName));
}
