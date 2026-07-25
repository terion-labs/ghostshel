namespace GhostShell.Packaging;

internal static class Program
{
    private const int FailedExitCode = 1;
    private const int UsageExitCode = 64;

    public static int Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault()?.ToLowerInvariant() switch
            {
                "macos" => BuildMacOs(MacOsPackagingCommand.Parse(args[1..])),
                "native-macos-receipt" => BuildNativeMacOsReceipt(
                    NativeMacOsReceiptCommand.Parse(args[1..])),
                "native-macos-build-evidence" => BuildNativeMacOsBuildEvidence(
                    NativeMacOsBuildEvidenceCommand.Parse(args[1..])),
                "native-macos-resource-evidence" =>
                    BuildNativeMacOsResourceEvidence(
                        NativeMacOsResourceEvidenceCommand.Parse(args[1..])),
                "native-macos-publish-artifacts" =>
                    PublishNativeMacOsArtifacts(
                        NativeMacOsArtifactPublishCommand.Parse(args[1..])),
                "--help" or "-h" or "help" => PrintHelpAndReturn(),
                _ => throw new PackagingUsageException(
                    "Expected a supported macOS packaging command."),
            };
        }
        catch (PackagingUsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            PrintHelp();
            return UsageExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"GhostSHELL packaging failed: {exception.Message}");
            return FailedExitCode;
        }
    }

    private static int BuildNativeMacOsReceipt(
        NativeMacOsReceiptCommand command)
    {
        var result = NativeMacOsBuildReceiptBuilder.Create(
            command.ToRequest());
        Console.WriteLine(
            $"Created native macOS build receipt "
            + $"({result.ArtifactFileCount} files, "
            + $"{result.ArtifactBytes} bytes, "
            + $"{result.ArtifactManifestSha256}).");
        return 0;
    }

    private static int BuildNativeMacOsBuildEvidence(
        NativeMacOsBuildEvidenceCommand command)
    {
        var result = NativeMacOsBuildEvidenceBuilder.Observe(command.Request);
        NativeMacOsEvidenceFilePublisher.Publish(
            command.OutputPath,
            result.CanonicalJson);
        Console.WriteLine(
            $"Created normalized native build evidence ({result.Sha256}).");
        return 0;
    }

    private static int BuildNativeMacOsResourceEvidence(
        NativeMacOsResourceEvidenceCommand command)
    {
        var result = NativeMacOsResourceEvidenceBuilder.Observe(command.Request);
        NativeMacOsEvidenceFilePublisher.Publish(
            command.OutputPath,
            result.RawContent);
        Console.WriteLine(
            $"Created normalized native resource evidence ({result.Sha256}).");
        return 0;
    }

    private static int PublishNativeMacOsArtifacts(
        NativeMacOsArtifactPublishCommand command)
    {
        var result = NativeMacOsArtifactPublisher.Publish(
            command.StagedDirectory,
            command.DestinationDirectory);
        Console.WriteLine(
            result.ReplacedExistingDirectory
                ? "Atomically replaced the native macOS artifact directory."
                : "Published the native macOS artifact directory.");
        return 0;
    }

    private static int BuildMacOs(MacOsPackagingCommand command)
    {
        var result = new MacOsAppBundleBuilder().Build(command.ToRequest());
        Console.WriteLine(
            $"Created GhostShell.app {result.ProductVersion} "
            + $"({result.FileCount} files, build {result.BuildVersion}).");
        return 0;
    }

    private static int PrintHelpAndReturn()
    {
        PrintHelp();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            GhostSHELL packaging

              macos --publish <directory> --output <GhostShell.app>
                    --version <major.minor.patch> --build-version <number[.number...]>
                    --component-catalog <managed-components.json>
                    --native-component-catalog <native-macos-components.json>
                    --native-build-receipt <native-macos-build-receipt.json>
                    --nuget-packages <global-packages-directory>

              native-macos-receipt --catalog <native-macos-components.json>
                    --artifact-directory <native/artifacts/osx-arm64>
                    --output <native-macos-build-receipt.json>
                    --repository-root <directory> --ghostty-source <directory>
                    --zig-archive <file> --zig-executable <file>
                    --zig-library-directory <directory>
                    --zig-local-cache <directory> --zig-global-cache <directory>
                    --zig-build-trace <file> --ghostty-install <directory>
                    --clang-executable <file> --sdk-directory <directory>
                    --sdk-settings <file>
                    --release-archive <file> --release-arm64-binary <file>
                    --metallib <file> --artifact-libghostty <file>
                    --zig-version <version>
                    --ghostty-commit <commit> --ghostty-tag <tag>
                    --clang-version <version> --sdk-version <identity>
                    [--sdk-build <build>] --ghostty-option <option>...
                    --shim-compiler-option <option>...

              native-macos-build-evidence --trace <zig-build-trace.log>
                    --repository-root <directory> --ghostty-source <directory>
                    --zig-executable <file> --zig-library-directory <directory>
                    --zig-local-cache <directory> --zig-global-cache <directory>
                    --sdk-directory <directory> --metallib <file>
                    --ghostty-install <directory> --artifact-libghostty <file>
                    --output <new-json-file>

              native-macos-resource-evidence --ghostty-source <directory>
                    --zig-global-cache <directory>
                    --ghostty-install <directory> --output <new-json-file>

              native-macos-publish-artifacts --staged-directory <directory>
                    --destination <native/artifacts/osx-arm64>

            The macOS command refuses an existing destination and requires a complete
            self-contained publish payload, including the pinned Ghostty runtime. It
            validates the reviewed managed catalog and exact native build receipt,
            then writes deterministic evidence into the application bundle.
            """);
    }
}
