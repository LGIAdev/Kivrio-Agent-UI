[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDir
)

$ErrorActionPreference = 'Stop'

function Join-KivrioAgentUiPackageParts {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDir,
        [Parameter(Mandatory = $true)]
        [string]$DestinationZip
    )

    $parts = Get-ChildItem -Path $SourceDir -Filter 'kivrio-agent-ui-package.zip.part*' | Sort-Object Name
    if (-not $parts) {
        $singleZip = Join-Path $SourceDir 'kivrio-agent-ui-package.zip'
        if (-not (Test-Path -LiteralPath $singleZip)) {
            throw "Archive Kivrio Agent UI introuvable dans $SourceDir."
        }
        Copy-Item -LiteralPath $singleZip -Destination $DestinationZip -Force
        return
    }

    $buffer = New-Object byte[] (4MB)
    $output = [System.IO.File]::Open($DestinationZip, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    try {
        foreach ($part in $parts) {
            $input = [System.IO.File]::OpenRead($part.FullName)
            try {
                while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $output.Write($buffer, 0, $read)
                }
            }
            finally {
                $input.Dispose()
            }
        }
    }
    finally {
        $output.Dispose()
    }
}

function New-KivrioAgentUiShortcut {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ShortcutPath,
        [Parameter(Mandatory = $true)]
        [string]$InstallDir,
        [Parameter(Mandatory = $true)]
        [string]$IconPath
    )

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = Join-Path $env:WINDIR 'System32\wscript.exe'
    $shortcut.Arguments = '"' + (Join-Path $InstallDir 'start-kivrio-agent-ui-hidden.vbs') + '"'
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.IconLocation = $IconPath
    $shortcut.Save()
}

$scriptRoot = (Resolve-Path $PackageDir).Path
$installDir = Join-Path $env:LOCALAPPDATA 'Kivrio Agent UI'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Kivrio Agent UI.lnk'
$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Kivrio Agent UI'
$startMenuShortcut = Join-Path $startMenuDir 'Kivrio Agent UI.lnk'
$extractRoot = Join-Path $env:TEMP ('kivrio-agent-ui-install-' + [guid]::NewGuid().ToString('N'))
$resolvedZip = Join-Path $extractRoot 'kivrio-agent-ui-package.zip'
$packageRoot = Join-Path $extractRoot 'app'

try {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null
    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null

    Join-KivrioAgentUiPackageParts -SourceDir $scriptRoot -DestinationZip $resolvedZip
    Expand-Archive -Path $resolvedZip -DestinationPath $extractRoot -Force
    if (-not (Test-Path -LiteralPath $packageRoot)) {
        throw "Le package Kivrio Agent UI est invalide: dossier app introuvable."
    }

    $null = robocopy $packageRoot $installDir /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -ge 8) {
        throw "La copie des fichiers Kivrio Agent UI a echoue (code $LASTEXITCODE)."
    }

    $dataDir = Join-Path $installDir 'data'
    $uploadsDir = Join-Path $dataDir 'uploads'
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    New-Item -ItemType Directory -Path $uploadsDir -Force | Out-Null

    $iconPath = Join-Path $installDir 'assets\kivrio-agent-ui.ico'
    if (-not (Test-Path -LiteralPath $iconPath)) {
        throw "Icone Kivrio Agent UI introuvable apres installation."
    }

    New-KivrioAgentUiShortcut -ShortcutPath $desktopShortcut -InstallDir $installDir -IconPath $iconPath
    New-KivrioAgentUiShortcut -ShortcutPath $startMenuShortcut -InstallDir $installDir -IconPath $iconPath

    Start-Process -FilePath (Join-Path $env:WINDIR 'System32\wscript.exe') -ArgumentList @('"' + (Join-Path $installDir 'start-kivrio-agent-ui-hidden.vbs') + '"')
}
finally {
    if (Test-Path -LiteralPath $extractRoot) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
