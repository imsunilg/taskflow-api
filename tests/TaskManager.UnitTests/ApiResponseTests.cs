using FluentAssertions;
using TaskManager.Application.Common;
using Xunit;

namespace TaskManager.UnitTests;

public class ApiResponseTests
{
    [Fact]
    public void Success_result_maps_to_success_envelope_with_data()
    {
        var result = Result<string>.Success("hello", "ok");

        var response = ApiResponse<string>.FromResult(result);

        response.Success.Should().BeTrue();
        response.Data.Should().Be("hello");
        response.Message.Should().Be("ok");
        response.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Failure_result_maps_to_failure_envelope_with_errors()
    {
        var result = Result<string>.Failure("bad", new[] { new ValidationError("title", "required") });

        var response = ApiResponse<string>.FromResult(result);

        response.Success.Should().BeFalse();
        response.Data.Should().BeNull();
        response.Errors.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ApiErrorItem("title", "required"));
    }

    [Fact]
    public void Paged_result_carries_pagination_info()
    {
        var paged = new PagedResult<int>(new[] { 1, 2, 3 }, page: 2, pageSize: 3, total: 10);

        var response = ApiResponse<int>.FromPaged(paged);

        response.Pagination!.Total.Should().Be(10);
        response.Pagination.TotalPages.Should().Be(4);
        response.Pagination.Page.Should().Be(2);
    }
}
