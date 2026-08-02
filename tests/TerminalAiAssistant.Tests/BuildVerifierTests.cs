using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class BuildVerifierTests
{
    [Theory]
    [InlineData(ToolHandler.EditFunctionName)]
    [InlineData(ToolHandler.ApplyPatchFunctionName)]
    [InlineData(ToolHandler.WriteFunctionName)]
    public void IsFileModifyingFunction_ReturnsTrue_ForFileModifyingTools(string functionName)
    {
        Assert.True(BuildVerifier.IsFileModifyingFunction(functionName));
    }

    [Theory]
    [InlineData(ToolHandler.ReadFunctionName)]
    [InlineData(ToolHandler.GrepFunctionName)]
    [InlineData(ToolHandler.GlobFunctionName)]
    [InlineData(ToolHandler.PowershellFunctionName)]
    [InlineData(ToolHandler.DiffFunctionName)]
    [InlineData(ToolHandler.WebSearchFunctionName)]
    public void IsFileModifyingFunction_ReturnsFalse_ForNonModifyingTools(string functionName)
    {
        Assert.False(BuildVerifier.IsFileModifyingFunction(functionName));
    }

    [Fact]
    public void HasDotnetProject_True_WhenCsprojInCwd()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        Assert.True(BuildVerifier.HasDotnetProject());
    }

    [Fact]
    public void HasDotnetProject_True_WhenSlnInSubdir()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("src\\App.sln", "");

        Assert.True(BuildVerifier.HasDotnetProject());
    }

    [Fact]
    public void HasDotnetProject_False_WhenNoProjectFiles()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("README.md", "# no projects here");

        Assert.False(BuildVerifier.HasDotnetProject());
    }

    [Fact]
    public void GetAutoVerify_DefaultsToTrue()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("AUTO_VERIFY");

        Assert.True(Configuration.GetAutoVerify());
    }

    [Fact]
    public void GetAutoVerify_RespectsEnvVar()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("AUTO_VERIFY");
        Environment.SetEnvironmentVariable("AUTO_VERIFY", "false");

        Assert.False(Configuration.GetAutoVerify());
    }

    [Fact]
    public void GetVerifyCommand_DefaultsToDotnetBuild()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("VERIFY_COMMAND");

        Assert.Equal("dotnet build --nologo -v q", Configuration.GetVerifyCommand());
    }

    [Fact]
    public void GetVerifyTimeout_DefaultsTo120Seconds()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("VERIFY_TIMEOUT");

        Assert.Equal(120000, Configuration.GetVerifyTimeout());
    }
}
