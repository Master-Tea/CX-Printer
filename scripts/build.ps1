[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration='Release',
    [ValidateSet('x64','x86','ARM64')][string]$Platform='x64',
    [switch]$SkipTests,
    [switch]$FrameworkDependent,
    [string]$RuntimeVersion='8.0.34',
    [string[]]$PackageSources=@()
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$env:MSBuildEnableWorkloadResolver='false'
$rid=@{ x64='win-x64';x86='win-x86';ARM64='win-arm64' }[$Platform]
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio 2022 with Windows application development tools.' }
$msbuild=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
if (!$msbuild) { throw 'Visual Studio MSBuild was not found.' }
function Build([string]$Project,[string]$Target,[string[]]$Extra=@()) {
    $arguments=@((Join-Path $repo $Project),"/t:$Target","/p:Configuration=$Configuration","/p:Platform=$Platform",'/m:1','/nr:false','/v:minimal')+$Extra
    & $msbuild @arguments
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $Project (exit $LASTEXITCODE)" }
}
Push-Location $repo
try {
    $restore=@()
    if ($PackageSources.Count) {
        $configDirectory=Join-Path $repo '.tools'
        New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null
        $configPath=Join-Path $configDirectory 'build.nuget.config'
        $sourceEntries=for ($i=0; $i -lt $PackageSources.Count; $i++) {
            $source=$PackageSources[$i]
            if ($source -notmatch '^https?://') { $source=[System.IO.Path]::GetFullPath($source) }
            '<add key="source'+$i+'" value="'+[System.Security.SecurityElement]::Escape($source)+'" />'
        }
        ('<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/>'+($sourceEntries -join '')+'</packageSources></configuration>') | Set-Content -LiteralPath $configPath -Encoding UTF8
        $restore+='/p:RestoreConfigFile='+$configPath
    }
    Build 'CX Printer.sln' 'Restore' $restore
    Build 'CX Printer.sln' 'Build'
    if (!$SkipTests) {
        $coreTest=Join-Path $repo "tests\CxPrint.Tests\bin\$Platform\$Configuration\net8.0\CxPrint.Tests.exe"
        $serviceTest=Join-Path $repo "tests\CxPrint.Service.Tests\bin\$Platform\$Configuration\net8.0-windows10.0.19041.0\CxPrint.Service.Tests.exe"
        & $coreTest
        if ($LASTEXITCODE -ne 0) { throw 'Core protocol tests failed.' }
        & $serviceTest
        if ($LASTEXITCODE -ne 0) { throw 'Service integration tests failed.' }
    }
    $out=Join-Path $repo "artifacts\CX-Printer-$rid"
    $selfContained=(!$FrameworkDependent).ToString().ToLowerInvariant()
    $publish=@("/p:RuntimeIdentifier=$rid","/p:SelfContained=$selfContained",'/p:PublishSingleFile=false','/p:PublishTrimmed=false','/p:PublishReadyToRun=false')+$restore
    if (!$FrameworkDependent) { $publish+="/p:CXRuntimeVersion=$RuntimeVersion" }
    Build 'src\CxPrint.Service\CxPrint.Service.csproj' 'Restore;Publish' ($publish+"/p:PublishDir=$out/Bridge/")
    Build 'CX Printer\CX Printer\CX Printer.csproj' 'Restore;Publish' ($publish+"/p:PublishDir=$out/")
    Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $out -Force
    Copy-Item -LiteralPath (Join-Path $repo 'THIRD_PARTY_NOTICES.md') -Destination $out -Force
    $docsOut=Join-Path $out 'docs'
    New-Item -ItemType Directory -Path $docsOut -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repo 'docs') -Filter '*.md' -File | Copy-Item -Destination $docsOut -Force
    $licenseRoot=Join-Path $out 'Licenses'
    New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
    $seen=@{}; $inventory=@()
    foreach ($assetPath in @('CX Printer\CX Printer\obj\project.assets.json','src\CxPrint.Service\obj\project.assets.json')) {
        $assets=Get-Content -LiteralPath (Join-Path $repo $assetPath) -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($library in $assets.libraries.PSObject.Properties) {
            if ($library.Value.type -ne 'package' -or $seen.ContainsKey($library.Name)) { continue }
            $seen[$library.Name]=$true
            foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
                $packagePath=Join-Path $folder $library.Value.path
                if (!(Test-Path -LiteralPath $packagePath)) { continue }
                $destination=Join-Path $licenseRoot ($library.Name -replace '/','-')
                New-Item -ItemType Directory -Path $destination -Force | Out-Null
                foreach ($file in Get-ChildItem -LiteralPath $packagePath -File -Recurse | Where-Object { $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE|THIRD.PARTY)' -or $_.Extension -eq '.nuspec' }) {
                    $relative=$file.FullName.Substring($packagePath.TrimEnd('\').Length+1)
                    $licenseFile=Join-Path $destination $relative
                    New-Item -ItemType Directory -Path (Split-Path $licenseFile -Parent) -Force | Out-Null
                    Copy-Item -LiteralPath $file.FullName -Destination $licenseFile -Force
                }
                $inventory+=@{ package=$library.Name; sha512=$library.Value.sha512 }
                break
            }
        }
    }
    $inventory | Sort-Object { $_.package } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'dependencies.json') -Encoding UTF8
    Write-Output "Published application: $out"
    Write-Output "Start: $(Join-Path $out 'CX Printer.exe')"
}
finally { Pop-Location }
