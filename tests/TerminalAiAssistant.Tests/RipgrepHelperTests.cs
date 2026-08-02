using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class RipgrepHelperTests
{
    private static ToolHandler.GrepCall Call(string? include = null, string? exclude = null,
        string? caseInsensitive = null, string? contextLines = null) => new()
    {
        pattern = "foo",
        include = include,
        exclude = exclude,
        case_insensitive = caseInsensitive,
        context_lines = contextLines
    };

    [Fact]
    public void BuildArguments_BaseFlagsInOrder()
    {
        var args = RipgrepHelper.BuildRipgrepArguments(Call(), "C:\\search\\dir");

        Assert.Equal("--max-count", args[0]);
        Assert.Equal("50", args[1]);
        Assert.Equal("--max-columns", args[2]);
        Assert.Equal("200", args[3]);
        Assert.Equal("--max-columns-preview", args[4]);
        Assert.Equal("-n", args[5]);
    }

    [Fact]
    public void BuildArguments_PatternAndPathLast()
    {
        var args = RipgrepHelper.BuildRipgrepArguments(Call(), "C:\\search\\dir");

        Assert.Equal("foo", args[^2]);
        Assert.Equal("C:\\search\\dir", args[^1]);
    }

    [Fact]
    public void BuildArguments_CaseInsensitive_AddsFlag()
    {
        var args = RipgrepHelper.BuildRipgrepArguments(Call(caseInsensitive: "true"), "dir");

        Assert.Contains("-i", args);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("FALSE")]
    public void BuildArguments_CaseSensitive_OmitsFlag(string? value)
    {
        var args = RipgrepHelper.BuildRipgrepArguments(Call(caseInsensitive: value), "dir");

        Assert.DoesNotContain("-i", args);
    }

    [Fact]
    public void BuildArguments_ValidContextLines_AddsFlag()
    {
        var args = RipgrepHelper.BuildRipgrepArguments(Call(contextLines: "3"), "dir");

        Assert.Contains("-C", args);
        Assert.Equal("3", args[args.IndexOf("-C") + 1]);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("")]
    public void BuildArguments_InvalidContextLines_OmitsFlag(string? value)
    {
        var args = RipgrepHelper.BuildRipgrepArguments(Call(contextLines: value), "dir");

        Assert.DoesNotContain("-C", args);
    }

    [Fact]
    public void BuildArguments_Include_AddsGlob()
    {
        var args = RipgrepHelper.BuildRipgrepArguments(Call(include: "*.cs"), "dir");

        Assert.Contains("--glob", args);
        Assert.Contains("*.cs", args);
    }

    [Fact]
    public void BuildArguments_Exclude_AddsNegatedGlob()
    {
        var args = RipgrepHelper.BuildRipgrepArguments(Call(exclude: "node_modules/**"), "dir");

        Assert.Contains("--glob", args);
        Assert.Contains("!node_modules/**", args);
    }

    [Fact]
    public void BuildArguments_IncludeAndExclude_BothPresent()
    {
        var args = RipgrepHelper.BuildRipgrepArguments(Call(include: "*.cs", exclude: "**/generated/**"), "dir");

        int globCount = args.Count(a => a == "--glob");
        Assert.Equal(2, globCount);
        Assert.Contains("*.cs", args);
        Assert.Contains("!**/generated/**", args);
    }
}
