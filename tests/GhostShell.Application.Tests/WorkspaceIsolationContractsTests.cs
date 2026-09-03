using GhostShell.Core;

namespace GhostShell.Application.Tests;

public sealed class WorkspaceIsolationContractsTests
{
    private static readonly WorkspaceId WorkspaceId = new("workspace-contract-test");
    private static readonly WorkspaceIsolationProviderId ProviderId = new("test-provider");

    [Theory]
    [InlineData("ubuntu:24.04", "ubuntu:24.04")]
    [InlineData("docker.io/library/ubuntu:24.04", "ubuntu:24.04")]
    [InlineData("docker.io/library/alpine@sha256:actual", "alpine")]
    [InlineData("docker.io/acme/tool:2", "acme/tool:2")]
    [InlineData("ghcr.io/acme/tool@sha256:actual", "ghcr.io/acme/tool@sha256:actual")]
    public void Docker_Hub_image_references_use_Dockerfile_style_display_names(
        string imageReference,
        string expected)
    {
        Assert.Equal(expected, WorkspaceIsolationImages.ForDisplay(imageReference));
    }

    [Fact]
    public void Prepare_request_allows_a_guest_only_workspace_and_snapshots_mounts()
    {
        var mounts = new List<WorkspaceIsolationMount>
        {
            new(HostPath("source"), "/workspace", isReadOnly: false),
        };

        var request = new WorkspaceIsolationPrepareRequest(WorkspaceId, mounts);
        mounts.Clear();

        Assert.Single(request.Mounts);
        Assert.Empty(new WorkspaceIsolationPrepareRequest(WorkspaceId).Mounts);
    }

    [Fact]
    public void Prepare_request_rejects_duplicate_guest_destinations()
    {
        var mounts = new WorkspaceIsolationMount[]
        {
            new(HostPath("one"), "/workspace", isReadOnly: false),
            new(HostPath("two"), "/workspace/", isReadOnly: true),
        };

        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspaceIsolationPrepareRequest(WorkspaceId, mounts));
    }

    [Fact]
    public void Prepare_request_caps_the_number_of_mounts()
    {
        var mounts = Enumerable.Range(0, WorkspaceDefinition.MaximumIsolationMountCount + 1)
            .Select(index => new WorkspaceIsolationMount(
                HostPath(index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                $"/workspace/{index}",
                isReadOnly: true))
            .ToArray();

        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspaceIsolationPrepareRequest(WorkspaceId, mounts));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/proc")]
    [InlineData("/proc/self")]
    [InlineData("/bin")]
    [InlineData("/bin/sh")]
    [InlineData("/sbin")]
    [InlineData("/usr")]
    [InlineData("/usr/bin")]
    [InlineData("/lib")]
    [InlineData("/etc")]
    [InlineData("/var")]
    [InlineData("/sys/kernel")]
    [InlineData("/dev")]
    [InlineData("/run/host-services")]
    [InlineData("/root")]
    [InlineData("/root/project")]
    [InlineData("/workspace/../run")]
    public void Mount_rejects_guest_root_and_provider_owned_paths(string guestDestination)
    {
        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspaceIsolationMount(
                HostPath("source"),
                guestDestination,
                isReadOnly: false));
    }

    [Fact]
    public void Binding_rejects_empty_provider_and_lease()
    {
        var mount = new WorkspaceIsolationMount(
            HostPath("source"),
            "/workspace",
            isReadOnly: false);

        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspaceIsolationProviderId(string.Empty));
        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspaceIsolationBinding(
                WorkspaceId,
                ProviderId,
                WorkspaceIsolationCapability.None,
                "resource",
                [mount],
                Guid.Empty));
    }

    [Fact]
    public void Binding_distinguishes_saved_override_from_running_image()
    {
        var binding = new WorkspaceIsolationBinding(
            WorkspaceId,
            ProviderId,
            WorkspaceIsolationCapability.PersistentRootFileSystem,
            "resource",
            [],
            Guid.NewGuid(),
            imageReference: null,
            runtimeImageReference: "docker.io/library/alpine@sha256:actual");

        Assert.Null(binding.ImageReference);
        Assert.Equal(
            "docker.io/library/alpine@sha256:actual",
            binding.RuntimeImageReference);
    }

    [Fact]
    public void Process_launch_snapshots_structured_values_and_rejects_null_environment_values()
    {
        var arguments = new List<string> { "first" };
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KEY"] = "value",
        };
        var launch = new WorkspaceProcessLaunch(
            "/usr/bin/tool",
            arguments,
            environment,
            hostWorkingDirectory: null);
        arguments[0] = "changed";
        environment["KEY"] = "changed";

        Assert.Equal("first", Assert.Single(launch.Arguments));
        Assert.Equal("value", launch.Environment["KEY"]);

        IReadOnlyDictionary<string, string> invalid =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["KEY"] = null!,
            };
        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspaceProcessLaunch(
                "/usr/bin/tool",
                [],
                invalid,
                hostWorkingDirectory: null));
    }

    private static string HostPath(string suffix) =>
        Path.Combine(Path.GetTempPath(), "ghostshell-isolation-contracts", suffix);
}
