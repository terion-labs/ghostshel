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
                "cef-runtime-receipt" => CreateCefRuntimeReceipt(
                    CefRuntimeReceiptCommand.Parse(args[1..])),
                "cef-runtime-validate" => ValidateCefRuntime(
                    CefRuntimeValidateCommand.Parse(args[1..])),
                "native-publish-artifacts" =>
                    PublishNativeArtifacts(
                        NativeArtifactPublishCommand.Parse(args[1..])),
                "--help" or "-h" or "help" => PrintHelpAndReturn(),
                _ => throw new PackagingUsageException(
                    "Expected a supported packaging command."),
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

    private static int PublishNativeArtifacts(
        NativeArtifactPublishCommand command)
    {
        var result = NativeArtifactPublisher.Publish(
            command.StagedDirectory,
            command.DestinationDirectory);
        Console.WriteLine(
            result.ReplacedExistingDirectory
                ? "Atomically replaced the native artifact directory."
                : "Published the native artifact directory.");
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

    private static int CreateCefRuntimeReceipt(CefRuntimeReceiptCommand command)
    {
        CefRuntimeReceipt.Create(
            command.RuntimeRoot,
            command.CatalogPath,
            command.RuntimeIdentifier,
            command.ArchiveSha1,
            command.ArchiveSha256,
            command.PatchSetSha256,
            command.SourceSnapshotSha256,
            command.OutputPath);
        Console.WriteLine(
            $"Created verified CEF runtime receipt for {command.RuntimeIdentifier}.");
        return 0;
    }

    private static int ValidateCefRuntime(CefRuntimeValidateCommand command)
    {
        var inspection = CefRuntimeReceipt.Validate(
            command.RuntimeRoot,
            command.CatalogPath,
            command.RuntimeIdentifier);
        Console.WriteLine(
            $"Validated CEF runtime {inspection.Catalog.CefVersion} for "
            + $"{inspection.Rid} ({inspection.Files.Count} files).");
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
                    --native-component-catalog <native-terminal-components.json>
                    --native-build-receipt <native-terminal-build-receipt.json>
                    --font-assets-catalog <terminal-font-assets.json>
                    --font-assets-build-receipt <terminal-font-assets-build-receipt.json>
                    --nuget-packages <global-packages-directory>
                    --cef-runtime-root <verified-runtime-directory>
                    --cef-runtime-catalog <cef-runtime-components.json>
                    --runtime-identifier <osx-arm64>

              cef-runtime-receipt --runtime-root <staged-directory>
                    --catalog <cef-runtime-components.json>
                    --runtime-identifier <rid> --archive-sha1 <hex>
                    --archive-sha256 <hex>
                    --patch-set-sha256 <hex>
                    --source-snapshot-sha256 <hex>
                    --output <staged-directory/cef-runtime-build-receipt.json>

              cef-runtime-validate --runtime-root <staged-directory>
                    --catalog <cef-runtime-components.json>
                    --runtime-identifier <rid>

              native-publish-artifacts --staged-directory <directory>
                    --destination <native/artifacts/runtime-identifier>

            The macOS command refuses an existing destination and requires a complete
            self-contained publish payload, including the pinned native terminal runtime. It
            validates the reviewed managed catalog, native build receipt, and
            exact terminal-font and CEF runtime receipts,
            then writes deterministic evidence into the application bundle.
            """);
    }
}
