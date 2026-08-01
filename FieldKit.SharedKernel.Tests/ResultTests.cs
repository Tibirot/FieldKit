using FieldKit.SharedKernel;

namespace FieldKit.SharedKernel.Tests;

public class ResultTests
{
    [Fact]
    public void Success_has_no_error()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(Error.None, result.Error);
    }

    [Fact]
    public void Failure_carries_its_error()
    {
        var error = new Error("outlet.closed", "Outlet is closed");

        var result = Result.Failure(error);

        Assert.True(result.IsFailure);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void Success_with_value_exposes_the_value()
    {
        var result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Accessing_the_value_of_a_failure_throws()
    {
        var result = Result.Failure<int>(new Error("x", "y"));

        Assert.Throws<InvalidOperationException>(() => result.Value);
    }
}
