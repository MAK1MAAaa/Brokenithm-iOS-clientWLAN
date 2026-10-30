param(
    [string]$IP,
    [ValidateRange(1, 65535)][int]$Port = 24864,
    [string]$Device,
    [string]$LogDirectory,
    [switch]$Diagnostics,
    [switch]$NoLed
)

$ErrorActionPreference = 'Stop'
try {
    $program = Join-Path $PSScriptRoot 'Brokenithm-LAN.exe'
    $pendingProgram = Join-Path $PSScriptRoot 'Brokenithm-LAN.pending.exe'
    if (Test-Path -LiteralPath $pendingProgram) {
        try {
            Copy-Item -LiteralPath $pendingProgram -Destination $program -Force
            Remove-Item -LiteralPath $pendingProgram -Force
        } catch {
            throw 'Close the previous Brokenithm LAN window with Ctrl+C, then start this launcher again to apply the update.'
        }
    }
    if (-not (Test-Path -LiteralPath $program)) {
        $buildScript = Join-Path $PSScriptRoot 'build.ps1'
        if (-not (Test-Path -LiteralPath $buildScript)) {
            throw 'Brokenithm-LAN.exe is missing. Extract the complete portable package, or use the source checkout with build.ps1 and src.'
        }
        & $buildScript
    }
    $bridgeArguments = @()
    if (-not [string]::IsNullOrWhiteSpace($IP)) { $bridgeArguments += @('--ip', $IP, '--port', $Port.ToString()) }
    if (-not [string]::IsNullOrWhiteSpace($Device)) { $bridgeArguments += @('--device', $Device) }
    if (-not [string]::IsNullOrWhiteSpace($LogDirectory)) { $bridgeArguments += @('--log-dir', $LogDirectory) }
    if ($Diagnostics) { $bridgeArguments += '--diagnostics' }
    if ($NoLed) { $bridgeArguments += '--no-led' }
    & $program @bridgeArguments
    if ($LASTEXITCODE -ne 0) { throw "Bridge exited with code $LASTEXITCODE." }
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}
