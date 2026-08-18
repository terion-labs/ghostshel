namespace GhostShell.Terminal.Tests;

public sealed class ClaudeCodeInvocationTests
{
    private readonly string _pluginDirectory = Path.Combine(
        Path.GetTempPath(),
        "ghostshell plugin");

    [Theory]
    [InlineData("--help")]
    [InlineData("-v")]
    [InlineData("--print")]
    [InlineData("--safe-mode")]
    [InlineData("doctor")]
    [InlineData("plugin")]
    [InlineData("--verbose", "update")]
    public void Noninteractive_and_management_invocations_are_passed_through(
        params string[] arguments)
    {
        Assert.False(ClaudeCodeInvocation.ShouldInjectPlugin(
            arguments,
            _pluginDirectory));
    }

    [Theory]
    [InlineData()]
    [InlineData("write tests")]
    [InlineData("--model", "sonnet", "write tests")]
    [InlineData("--settings", "{\"hooks\":{}}", "write tests")]
    [InlineData("--", "doctor")]
    public void Interactive_invocations_receive_the_plugin(params string[] arguments)
    {
        Assert.True(ClaudeCodeInvocation.ShouldInjectPlugin(
            arguments,
            _pluginDirectory));
    }

    [Fact]
    public void Exact_existing_plugin_path_is_not_added_twice()
    {
        var equivalent = Path.Combine(_pluginDirectory, ".", "child", "..");

        Assert.False(ClaudeCodeInvocation.ShouldInjectPlugin(
            [
                "--plugin-dir=/first/user/plugin",
                "--plugin-dir=/second/user/plugin",
                $"--plugin-dir={equivalent}",
                "--model",
                "sonnet",
            ],
            _pluginDirectory));
    }

    [Fact]
    public void Other_plugin_directories_do_not_disable_the_managed_plugin()
    {
        Assert.True(ClaudeCodeInvocation.ShouldInjectPlugin(
            ["--plugin-dir=/user/plugin", "--model", "sonnet"],
            _pluginDirectory));
    }
}
