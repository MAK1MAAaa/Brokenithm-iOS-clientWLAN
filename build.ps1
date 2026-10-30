param(
    [switch]$Tests,
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
if ($Tests -and $Package) { throw 'Use -Tests and -Package in separate invocations.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw '.NET Framework C# compiler not found. Install .NET Framework 4.7.2 or later.'
}

$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
if ($Tests) {
    $outputDirectory = Join-Path $PSScriptRoot 'tests\bin'
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    $outputFile = Join-Path $outputDirectory 'Brokenithm-LAN.Tests.exe'
    $sourceFiles += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Filter '*.cs' | ForEach-Object { $_.FullName })
    $entryPoint = '/main:BrokenithmLan.Tests.IntegrationTests'
} else {
    $outputFile = Join-Path $PSScriptRoot 'Brokenithm-LAN.exe'
    $entryPoint = '/main:BrokenithmLan.Program'
}

$compilerArguments = @('/nologo', '/noconfig', '/codepage:65001', '/warn:4', '/warnaserror+', '/optimize+', '/target:exe', '/platform:anycpu', '/reference:System.dll', '/reference:System.Core.dll', $entryPoint, ('/out:' + $outputFile)) + $sourceFiles
& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed (exit $LASTEXITCODE)." }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\App.config') -Destination ($outputFile + '.config') -Force
Write-Output "Built: $outputFile"

if ($Tests) {
    & $outputFile
    if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit $LASTEXITCODE)." }
}

if ($Package) {
    $distributionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist'))
    $packageDirectory = [IO.Path]::GetFullPath((Join-Path $distributionRoot ('package-' + [Guid]::NewGuid().ToString('N'))))
    $archivePath = Join-Path $distributionRoot 'Brokenithm-LAN-portable.zip'
    $runtimeFiles = @(
        'Brokenithm-LAN.exe',
        'Brokenithm-LAN.exe.config',
        'Start-LAN.ps1',
        'Start-LAN.cmd',
        'Start-LAN-NoLED.cmd',
        'Start-LAN-Diagnostics.cmd',
        'README.md',
        'docs\PROTOCOL.md',
        'docs\TROUBLESHOOTING.md'
    )
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    try {
        foreach ($relativeFile in $runtimeFiles) {
            $destinationFile = Join-Path $packageDirectory $relativeFile
            New-Item -ItemType Directory -Path (Split-Path -Parent $destinationFile) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $relativeFile) -Destination $destinationFile
        }
        Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archivePath -Force
        Write-Output "Packaged: $archivePath"
    } finally {
        $resolvedPackage = [IO.Path]::GetFullPath($packageDirectory)
        $distributionPrefix = $distributionRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedPackage.StartsWith($distributionPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedPackage) -notmatch '^package-[0-9a-f]{32}$') {
            throw 'Refusing to clean an unexpected package directory.'
        }
        if (Test-Path -LiteralPath $resolvedPackage) { Remove-Item -LiteralPath $resolvedPackage -Recurse -Force }
    }
}
