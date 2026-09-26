using SCU.Common;
using Xunit;

namespace SCU.Tests;

public class ResultTests
{
    [Fact]
    public void Success_HasZeroCodeAndSuccessFlag()
    {
        var result = Result.Success("готово");

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Code);
        Assert.Equal("готово", result.Message);
    }

    [Fact]
    public void Success_WithoutMessage_HasEmptyMessage()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.Equal(string.Empty, result.Message);
    }

    [Fact]
    public void Failure_KeepsCodeAndMessage()
    {
        var result = Result.Failure("отменено", -1);

        Assert.False(result.IsSuccess);
        Assert.Equal(-1, result.Code);
        Assert.Equal("отменено", result.Message);
    }

    [Fact]
    public void GenericSuccess_StoresValue()
    {
        var result = Result<string>.Success("вывод powercfg");

        Assert.True(result.IsSuccess);
        Assert.Equal("вывод powercfg", result.Value);
    }

    [Fact]
    public void GenericFailure_ValueIsNull()
    {
        var result = Result<int>.Failure("ошибка", 5);

        Assert.False(result.IsSuccess);
        Assert.Equal(5, result.Code);
        Assert.Equal(0, result.Value); // default(T) при неудаче
    }
}
