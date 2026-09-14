using Sidequest.Application.Abstractions;
using Sidequest.Domain.Rules;

namespace Sidequest.UnitTests.FoundationDomain;

/// <summary>Checks validated one-based paging and exact zero-based offsets without clamping or integer wraparound.</summary>
public sealed class PageRequestTests
{
    /// <summary>Checks that omitted paging arguments select the first twenty-five rows.</summary>
    [Fact]
    public void Defaults_SelectFirstTwentyFiveRows()
    {
        var request = new PageRequest();
        Assert.Equal(0, request.Offset);
        Assert.Equal(25, request.Limit);
    }

    /// <summary>Checks exact offset arithmetic at valid size and Int32 representation boundaries.</summary>
    /// <param name="page">The one-based page number.</param>
    /// <param name="size">The validated requested number of rows per page.</param>
    /// <param name="offset">The independently calculated zero-based skip count.</param>
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(1, 100, 0)]
    [InlineData(2, 25, 25)]
    [InlineData(3, 100, 200)]
    [InlineData(int.MaxValue, 1, 2147483646)]
    [InlineData(1073741824, 2, 2147483646)]
    [InlineData(21474837, 100, 2147483600)]
    public void ValidPages_ReturnExactLimitAndOffset(int page, int size, int offset)
    {
        var request = new PageRequest(page, size);
        Assert.Equal(offset, request.Offset);
        Assert.Equal(size, request.Limit);
    }

    /// <summary>Checks invalid sizes through both query properties, preventing either path from silently clamping.</summary>
    /// <param name="size">The below-minimum or above-maximum page size.</param>
    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void InvalidSize_RejectsLimitAndOffset(int size)
    {
        var request = new PageRequest(2, size);
        AssertValidation(() => _ = request.Limit, "PageSize", "Page size must be between 1 and 100.");
        AssertValidation(() => _ = request.Offset, "PageSize", "Page size must be between 1 and 100.");
    }

    /// <summary>Checks nonpositive pages and the first overflowing offset at sizes two and one hundred.</summary>
    /// <param name="page">The invalid one-based page or first overflowing page.</param>
    /// <param name="size">A valid page size that must remain unchanged by validation.</param>
    [Theory]
    [InlineData(int.MinValue, 25)]
    [InlineData(-1, 25)]
    [InlineData(0, 25)]
    [InlineData(1073741825, 2)]
    [InlineData(21474838, 100)]
    [InlineData(int.MaxValue, 100)]
    public void InvalidPageOrOverflow_ReportsPageValidationWithoutClamping(int page, int size)
    {
        var request = new PageRequest(page, size);
        AssertValidation(() => _ = request.Offset, "Page", "Page is outside the supported range.");
        Assert.Equal(size, request.Limit);
    }

    private static void AssertValidation(Action action, string field, string message)
    {
        var error = Assert.Throws<DomainException>(action);
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal(field, error.Field);
        Assert.Equal(message, error.Message);
    }
}
