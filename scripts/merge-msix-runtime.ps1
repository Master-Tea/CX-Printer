[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PackageManifest,
    [Parameter(Mandatory=$true)][string]$RuntimeManifest
)
$ErrorActionPreference='Stop'
$namespace='http://schemas.microsoft.com/appx/manifest/foundation/windows10'
[xml]$package=Get-Content -LiteralPath $PackageManifest -Raw -Encoding UTF8
[xml]$runtime=Get-Content -LiteralPath $RuntimeManifest -Raw -Encoding UTF8
$packageNs=New-Object System.Xml.XmlNamespaceManager($package.NameTable)
$packageNs.AddNamespace('m',$namespace)
$runtimeNs=New-Object System.Xml.XmlNamespaceManager($runtime.NameTable)
$runtimeNs.AddNamespace('m',$namespace)
$extensions=$package.SelectSingleNode('/m:Package/m:Extensions',$packageNs)
if (!$extensions) {
    $extensions=$package.CreateElement('Extensions',$namespace)
    $null=$package.DocumentElement.AppendChild($extensions)
}
# A self-contained packaged app needs the SDK's package-level WinRT registrations.
# The executable's registration-free activation manifest only covers unpackaged use.
$categories=@('windows.activatableClass.inProcessServer','windows.activatableClass.proxyStub')
foreach ($existing in @($extensions.SelectNodes('m:Extension',$packageNs))) {
    if ($existing.Category -in $categories) { throw 'Runtime registrations already exist; refusing to replace custom registrations.' }
}
$root=Split-Path ([IO.Path]::GetFullPath($PackageManifest)) -Parent
$count=0
$classes=0
foreach ($extension in $runtime.SelectNodes('/m:Package/m:Extensions/m:Extension',$runtimeNs)) {
    if ($extension.Category -notin $categories) { continue }
    $path=$extension.SelectSingleNode('*/m:Path',$runtimeNs).InnerText
    if (!$path -or [IO.Path]::IsPathRooted($path) -or $path -match '(^|[\\/])\.\.([\\/]|$)') { throw "Invalid runtime registration path: $path" }
    if (!(Test-Path -LiteralPath (Join-Path $root $path) -PathType Leaf)) { throw "Missing registered runtime binary: $path" }
    $null=$extensions.AppendChild($package.ImportNode($extension,$true))
    $classes+=$extension.SelectNodes('.//m:ActivatableClass',$runtimeNs).Count
    $count++
}
if ($count -eq 0 -or !$extensions.SelectSingleNode("m:Extension/m:InProcessServer/m:ActivatableClass[@ActivatableClassId='Microsoft.UI.Xaml.Application']",$packageNs)) {
    throw 'Windows App SDK WinUI activation registrations are missing.'
}
$package.Save([IO.Path]::GetFullPath($PackageManifest))
Write-Output "Merged $count runtime extensions and $classes activatable classes."
