// Smoke test for the framework-agnostic Exclr8Cef package.
// Calls Cef.GetVersions() — the idiomatic facade — which under the hood
// invokes excef_get_versions in the native shim via ClangSharp-generated
// P/Invoke. Should print versions identical to native version_probe.

using Exclr8Cef;

var versions = Cef.GetVersions();

Console.WriteLine($"Exclr8CEF shim version : {versions.Shim}");
Console.WriteLine($"CEF version            : {versions.Cef}");
Console.WriteLine($"Chromium version       : {versions.Chromium}");
