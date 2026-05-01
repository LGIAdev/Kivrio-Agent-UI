[CmdletBinding()]
param(
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

function Copy-KivrioAgentUiItem {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    if (Test-Path -LiteralPath $SourcePath -PathType Container) {
        New-Item -ItemType Directory -Path $DestinationPath -Force | Out-Null
        $null = robocopy $SourcePath $DestinationPath /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP
        if ($LASTEXITCODE -ge 8) {
            throw "La copie de $SourcePath a echoue (code $LASTEXITCODE)."
        }
        return
    }

    $parent = Split-Path -Parent $DestinationPath
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Force
}

function Remove-IfExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Split-BinaryFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceFile,
        [Parameter(Mandatory = $true)]
        [long]$ChunkSizeBytes
    )

    $source = Get-Item -LiteralPath $SourceFile
    $buffer = New-Object byte[] (4MB)
    $index = 0
    $input = [System.IO.File]::OpenRead($source.FullName)
    try {
        while ($input.Position -lt $input.Length) {
            $partPath = '{0}.part{1:D2}' -f $source.FullName, $index
            $remaining = [Math]::Min($ChunkSizeBytes, $input.Length - $input.Position)
            $output = [System.IO.File]::Open($partPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
            try {
                while ($remaining -gt 0) {
                    $toRead = [Math]::Min($buffer.Length, $remaining)
                    $read = $input.Read($buffer, 0, $toRead)
                    if ($read -le 0) {
                        break
                    }
                    $output.Write($buffer, 0, $read)
                    $remaining -= $read
                }
            }
            finally {
                $output.Dispose()
            }
            $index++
        }
    }
    finally {
        $input.Dispose()
    }

    return Get-ChildItem -Path ($source.FullName + '.part*') | Sort-Object Name
}

function Get-CSharpCompiler {
    $candidates = @(
        'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
        'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }
    throw 'Compilateur C# introuvable sur cette machine.'
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $scriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $projectRoot 'releases\windows-installer'
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDir)
$buildRoot = Join-Path $outputRoot 'build'
$packageRoot = Join-Path $buildRoot 'package\app'
$installerBuildRoot = Join-Path $buildRoot 'installer'
$zipPath = Join-Path $installerBuildRoot 'kivrio-agent-ui-package.zip'
$targetExe = Join-Path $outputRoot 'Kivrio-Agent-UI-Setup.exe'
$stubExe = Join-Path $installerBuildRoot 'Kivrio-Agent-UI-Setup.stub.exe'
$iconPath = Join-Path $projectRoot 'assets\kivrio-agent-ui.ico'
$installerStub = Join-Path $projectRoot 'installer\KivrioAgentUiSetupStub.cs'
$serverSource = Join-Path $projectRoot 'server\KivrioAgentUiServer.cs'
$serverExe = Join-Path $packageRoot 'bin\kivrio-agent-ui-server.exe'
$csharpCompiler = Get-CSharpCompiler

if (-not (Test-Path -LiteralPath $iconPath)) {
    throw "Icone Kivrio Agent UI introuvable: $iconPath"
}

if (-not (Test-Path -LiteralPath $serverSource)) {
    throw "Serveur autonome introuvable: $serverSource"
}

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
New-Item -ItemType Directory -Path $installerBuildRoot -Force | Out-Null

$itemsToCopy = @(
    'assets',
    'bin',
    'css',
    'js',
    'server',
    'index.html',
    'README.md',
    'SECURITY.md',
    'LICENSE',
    'CODE_OF_CONDUCT.md',
    'CONTRIBUTING.md',
    'start-kivrio-agent-ui.bat',
    'start-kivrio-agent-ui-hidden.vbs',
    'start_localhost.bat'
)

foreach ($item in $itemsToCopy) {
    Copy-KivrioAgentUiItem `
        -SourcePath (Join-Path $projectRoot $item) `
        -DestinationPath (Join-Path $packageRoot $item)
}

$serverExeParent = Split-Path -Parent $serverExe
New-Item -ItemType Directory -Path $serverExeParent -Force | Out-Null
$serverCompilerArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    "/out:$serverExe",
    '/r:System.Web.Extensions.dll',
    $serverSource
)

& $csharpCompiler @serverCompilerArgs
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $serverExe)) {
    throw 'La compilation du serveur autonome Kivrio Agent UI a echoue.'
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Join-Path $buildRoot 'package\app'),
    $zipPath,
    [System.IO.Compression.CompressionLevel]::NoCompression,
    $true
)
$compilerArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    "/out:$stubExe",
    "/win32icon:$iconPath",
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    '/r:System.IO.Compression.dll',
    '/r:System.IO.Compression.FileSystem.dll',
    '/r:Microsoft.CSharp.dll',
    $installerStub
)

& $csharpCompiler @compilerArgs
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $stubExe)) {
    throw 'La compilation de l installeur Windows Kivrio Agent UI a echoue.'
}

Copy-Item -LiteralPath $stubExe -Destination $targetExe -Force

$magic = [byte[]](0x4B,0x49,0x56,0x50,0x41,0x59,0x4C,0x31)
$payloadLengthBytes = [BitConverter]::GetBytes((Get-Item -LiteralPath $zipPath).Length)
$buffer = New-Object byte[] (4MB)
$targetStream = [System.IO.File]::Open($targetExe, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write)
try {
    $zipStream = [System.IO.File]::OpenRead($zipPath)
    try {
        while (($read = $zipStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $targetStream.Write($buffer, 0, $read)
        }
    }
    finally {
        $zipStream.Dispose()
    }

    $targetStream.Write($magic, 0, $magic.Length)
    $targetStream.Write($payloadLengthBytes, 0, $payloadLengthBytes.Length)
}
finally {
    $targetStream.Dispose()
}

Write-Output "Installateur cree: $targetExe"
