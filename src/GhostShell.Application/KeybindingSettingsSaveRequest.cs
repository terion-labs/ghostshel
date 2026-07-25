using GhostShell.Core;

namespace GhostShell.Application;

public sealed record KeybindingSettingsSaveRequest(
    KeymapProfile Profile,
    long? ExpectedRevision);
