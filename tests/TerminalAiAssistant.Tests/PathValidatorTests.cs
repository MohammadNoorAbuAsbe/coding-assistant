using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class PathValidatorTests
{
    [Fact]
    public void ValidRelativePath_ReturnsFullPathWithinWorkspace()
    {
        using var ws = new TempWorkspace();
        string result = PathValidator.ValidatePath("src/Program.cs", ws.Root);

        Assert.Equal(Path.GetFullPath(Path.Combine(ws.Root, "src", "Program.cs")), result);
        Assert.StartsWith(Path.GetFullPath(ws.Root) + Path.DirectorySeparatorChar, result);
    }

    [Fact]
    public void AbsolutePathWithinWorkspace_Accepted()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "taa-pv-ws2");
        string target = Path.Combine(workspace, "file.txt");

        string result = PathValidator.ValidatePath(target, workspace);

        Assert.Equal(Path.GetFullPath(target), result);
    }

    [Fact]
    public void PathOutsideWorkspace_Throws()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "taa-pv-ws3");
        string outside = Path.Combine(Path.GetTempPath(), "taa-pv-outside", "file.txt");

        Assert.Throws<PathOutsideWorkspaceException>(() => PathValidator.ValidatePath(outside, workspace));
    }

    [Fact]
    public void ParentTraversal_Throws()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "taa-pv-ws4");

        Assert.Throws<PathOutsideWorkspaceException>(() => PathValidator.ValidatePath("../file.txt", workspace));
        Assert.Throws<PathOutsideWorkspaceException>(() => PathValidator.ValidatePath("../../file.txt", workspace));
        Assert.Throws<PathOutsideWorkspaceException>(() => PathValidator.ValidatePath("sub/../../file.txt", workspace));
    }

    [Fact]
    public void SiblingPrefix_IsNotTreatedAsInside()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "taa-pv-ws5");
        string sibling = Path.Combine(Path.GetTempPath(), "taa-pv-ws5-sibling", "file.txt");

        Assert.Throws<PathOutsideWorkspaceException>(() => PathValidator.ValidatePath(sibling, workspace));
    }

    [Fact]
    public void EmptyPath_ThrowsArgument()
    {
        Assert.Throws<ArgumentException>(() => PathValidator.ValidatePath("", Path.GetTempPath()));
        Assert.Throws<ArgumentException>(() => PathValidator.ValidatePath(null!, Path.GetTempPath()));
    }

    [Fact]
    public void EmptyWorkspaceRoot_ThrowsArgument()
    {
        Assert.Throws<ArgumentException>(() => PathValidator.ValidatePath("file.txt", ""));
        Assert.Throws<ArgumentException>(() => PathValidator.ValidatePath("file.txt", null!));
    }

    [Fact]
    public void RootPath_Throws()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "taa-pv-ws6");
        string root = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.Throws<PathOutsideWorkspaceException>(() => PathValidator.ValidatePath(root, workspace));
    }

    [Fact]
    public void TrailingSeparator_TrimmedFromResult()
    {
        using var ws = new TempWorkspace();
        string result = PathValidator.ValidatePath("subdir" + Path.DirectorySeparatorChar, ws.Root);

        Assert.Equal(Path.GetFullPath(Path.Combine(ws.Root, "subdir")), result);
        Assert.False(result.EndsWith(Path.DirectorySeparatorChar.ToString()));
    }

    [Fact]
    public void WindowsDeviceNames_Throws()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string workspace = Path.Combine(Path.GetTempPath(), "taa-pv-ws8");
        foreach (string device in new[] { "CON", "NUL", "PRN", "AUX", "COM1", "LPT3", "con", "nul" })
        {
            Assert.Throws<PathOutsideWorkspaceException>(() => PathValidator.ValidatePath(device, workspace));
            Assert.Throws<PathOutsideWorkspaceException>(() => PathValidator.ValidatePath($"sub/{device}.txt", workspace));
        }
    }

    [Fact]
    public void WindowsExtendedPathPrefix_Throws()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string workspace = Path.Combine(Path.GetTempPath(), "taa-pv-ws9");
        Assert.Throws<PathOutsideWorkspaceException>(() => PathValidator.ValidatePath(@"\\.\C:\Windows\win.ini", workspace));
        Assert.Throws<PathOutsideWorkspaceException>(() => PathValidator.ValidatePath(@"\\?\C:\Windows\win.ini", workspace));
    }

    [Fact]
    public void Windows_CaseInsensitiveComparison()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string workspace = Path.Combine(Path.GetTempPath(), "taa-pv-ws10");
        string result = PathValidator.ValidatePath(workspace.ToUpperInvariant() + Path.DirectorySeparatorChar + "file.txt", workspace);

        Assert.Equal(Path.GetFullPath(workspace).ToUpperInvariant() + Path.DirectorySeparatorChar + "file.txt", result);
    }
}
