$shimDirectory = $env:GHOSTSHELL_CLAUDE_SHIM_DIRECTORY
if (-not [string]::IsNullOrWhiteSpace($shimDirectory)) {
    $remainingPath = @($env:PATH -split [IO.Path]::PathSeparator | Where-Object {
        -not [string]::Equals($_, $shimDirectory, [StringComparison]::OrdinalIgnoreCase)
    })
    $env:PATH = (@($shimDirectory) + $remainingPath) -join [IO.Path]::PathSeparator
}

function global:claude {
    & $env:GHOSTSHELL_CLAUDE_WRAPPER_HOST --ghostshell-claude-wrapper @args
}
