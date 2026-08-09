namespace Exclr8Cef;

/// <summary>
/// Versions reported by the linked Exclr8CEF shim, the Chromium Embedded
/// Framework it was built against, and the underlying Chromium release.
/// </summary>
/// <param name="Shim">Version of the Exclr8CEF native shim.</param>
/// <param name="Cef">CEF version (e.g. <c>150.0.9</c>).</param>
/// <param name="Chromium">Chromium version (e.g. <c>150.0.7871.46</c>).</param>
public sealed record CefVersions(string Shim, string Cef, string Chromium);
