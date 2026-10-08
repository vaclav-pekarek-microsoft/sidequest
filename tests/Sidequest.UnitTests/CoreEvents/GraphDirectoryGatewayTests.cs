using Sidequest.Application.Abstractions;
using Sidequest.Infrastructure.Directory;

namespace Sidequest.UnitTests.CoreEvents;

/// <summary>Verifies the retained individual-directory contract after group operations were removed.</summary>
public sealed class GraphDirectoryGatewayTests
{
    /// <summary>Returns the approved synthetic workforce policy shared by credential-provider tests.</summary>
    /// <param name="approved">Whether the synthetic extension policy is approved.</param>
    /// <returns>A deterministic Graph directory policy.</returns>
    internal static GraphDirectoryOptions Policy(bool approved = true) => new()
    {
        TenantId = Guid.Parse("10000000-0000-0000-0000-000000000001"),
        WorkforcePolicyApproved = approved,
        WorkforceExtension = "extension_test_workforce",
        WorkforceValue = "approved"
    };

    /// <summary>The application directory port contains only individual user search and resolution.</summary>
    [Fact]
    public void DirectoryGateway_ExposesOnlyIndividualUserOperations()
    {
        Assert.Equal(["GetUserAsync", "SearchUsersAsync"],
            typeof(IDirectoryGateway).GetMethods().Select(method => method.Name).Order());
        Assert.DoesNotContain(typeof(IDirectoryGateway).GetMethods(), method =>
            method.Name.Contains("Group", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The Graph adapter has no residual public group search or expansion entry point.</summary>
    [Fact]
    public void GraphDirectoryGateway_ExposesNoGroupOperation()
    {
        Assert.DoesNotContain(typeof(GraphDirectoryGateway).GetMethods(), method =>
            method.DeclaringType == typeof(GraphDirectoryGateway) &&
            method.Name.Contains("Group", StringComparison.OrdinalIgnoreCase));
    }
}
