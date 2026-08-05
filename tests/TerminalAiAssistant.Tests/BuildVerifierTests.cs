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
    public void GetAutoVerify_DefaultsOnForAllProviders()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("AUTO_VERIFY");
        Configuration.LoadProviderConfigs();
        Configuration.SetProvider("ollama");

        Assert.True(Configuration.GetAutoVerify());
    }

    [Fact]
    public void GetAutoVerify_DefaultsOnForCloud()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("AUTO_VERIFY");
        Configuration.LoadProviderConfigs();
        Configuration.SetProvider("openai");

        Assert.True(Configuration.GetAutoVerify());
    }

    [Fact]
    public void GetAutoVerify_RespectsEnvVar()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("AUTO_VERIFY");
        Configuration.LoadProviderConfigs();
        Configuration.SetProvider("openai");
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
    public void ResolveVerifyCommand_EnvOverrideAlwaysWins()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("VERIFY_COMMAND");
        Environment.SetEnvironmentVariable("VERIFY_COMMAND", "python -m compileall -q .");

        Assert.Equal("python -m compileall -q .", BuildVerifier.ResolveVerifyCommand());
    }

    [Fact]
    public void ResolveVerifyCommand_DetectsDotnetProject()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("VERIFY_COMMAND");
        ws.WriteFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        Assert.Equal("dotnet build --nologo -v q", BuildVerifier.ResolveVerifyCommand());
    }

    [Fact]
    public void ResolveVerifyCommand_DetectsTypeScriptProjectWithLocalTsc()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("VERIFY_COMMAND");
        ws.WriteFile("tsconfig.json", "{ }");
        ws.WriteFile("node_modules\\.bin\\tsc.cmd", "@echo off");

        string? command = BuildVerifier.ResolveVerifyCommand();

        Assert.NotNull(command);
        Assert.Contains("tsc.cmd", command);
        Assert.Contains("--noEmit", command);
    }

    [Fact]
    public void ResolveVerifyCommand_NullWhenNoSupportedBuildSystem()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("VERIFY_COMMAND");
        ws.WriteFile("README.md", "# nothing here");
        ws.WriteFile("tsconfig.json", "{ }");

        Assert.Null(BuildVerifier.ResolveVerifyCommand());
    }

    [Fact]
    public void ResolveVerifyCommand_NullWhenEmptyWorkspace()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("VERIFY_COMMAND");

        Assert.Null(BuildVerifier.ResolveVerifyCommand());
    }

    [Fact]
    public void GetVerifyTimeout_DefaultsTo120Seconds()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("VERIFY_TIMEOUT");

        Assert.Equal(120000, Configuration.GetVerifyTimeout());
    }

    [Fact]
    public void ParseBuildErrors_ExtractsCompilerErrorLines()
    {
        string output = "some noise\n" +
            "C:\\repo\\src\\Foo.cs(12,5): error CS0103: The name 'Bar' does not exist in the current context\n" +
            "C:\\repo\\src\\Foo.cs(42): error CS1002: ; expected\n" +
            "warning CS0219: unused (not an error line)\n" +
            "non-matching text here\n";

        string errors = BuildVerifier.ParseBuildErrors(output);

        Assert.Contains("Foo.cs(12,5): error CS0103", errors);
        Assert.Contains("Foo.cs(42): error CS1002", errors);
        Assert.DoesNotContain("warning", errors);
        Assert.DoesNotContain("non-matching", errors);
    }

    [Fact]
    public void ParseBuildErrors_EmptyWhenNoCompilerErrors()
    {
        Assert.Equal("", BuildVerifier.ParseBuildErrors("Build succeeded.\nAll good here."));
        Assert.Equal("", BuildVerifier.ParseBuildErrors(""));
    }

    [Fact]
    public void ParseBuildErrors_DeduplicatesAndCaps()
    {
        string line = "src\\Foo.cs(1,1): error CS0001: repeated message\n";
        string output = line + line + line;

        Assert.Equal("  " + line.TrimEnd('\n'), BuildVerifier.ParseBuildErrors(output));
    }
}
