[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PackagePath,
    [switch]$CloseAfterTest,
    [string]$ReportPath
)
$ErrorActionPreference='Stop'
# Install the signed file first, then exercise the same activation API as Start.
Add-AppxPackage -Path ([IO.Path]::GetFullPath($PackagePath))
$package=Get-AppxPackage -Name 'MasterTea.CXPrinter'
if (!$package) { throw 'The package was not installed.' }
if ($package.IsDevelopmentMode) { throw 'A development registration cannot replace the signed installation test.' }
$source=@'
using System;
using System.Runtime.InteropServices;
namespace CxPrint.PackageTests {
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }
    public static class Launcher {
        public static uint Activate(string appId) {
            object manager=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c")));
            try {
                uint pid;
                int hr=((IApplicationActivationManager)manager).ActivateApplication(appId,null,0,out pid);
                Marshal.ThrowExceptionForHR(hr);
                return pid;
            } finally { Marshal.ReleaseComObject(manager); }
        }
    }
}
'@
if (!('CxPrint.PackageTests.Launcher' -as [type])) { Add-Type -TypeDefinition $source }
$launchedProcessId=[CxPrint.PackageTests.Launcher]::Activate($package.PackageFamilyName+'!App')
$process=[Diagnostics.Process]::GetProcessById([int]$launchedProcessId)
# Retain a process handle before closing a process created by the activation broker.
$null=$process.Handle
Start-Sleep -Seconds 8
$process.Refresh()
if ($process.HasExited) { throw ('Packaged application exited: {0:X8}' -f $process.ExitCode) }
if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Packaged application did not show a window.' }
$result=[ordered]@{
    package=$package.PackageFullName
    osVersion=[Environment]::OSVersion.Version.ToString()
    installedFromMsix=$true
    developmentRegistration=[bool]$package.IsDevelopmentMode
    installLocation=$package.InstallLocation
    signatureKind=$package.SignatureKind.ToString()
    activation='IApplicationActivationManager.ActivateApplication'
    windowTitle=$process.MainWindowTitle
    visibleWindow=$true
    processId=$launchedProcessId
    checkedAt=[DateTimeOffset]::Now.ToString('o')
}
if ($CloseAfterTest) {
    $null=$process.CloseMainWindow()
    if (!$process.WaitForExit(10000)) { throw 'The test window did not close normally.' }
    $result['exitCode']=$process.ExitCode
    if ($process.ExitCode -ne 0) {
        if ($ReportPath) { $result | ConvertTo-Json | Set-Content -LiteralPath $ReportPath -Encoding UTF8 }
        throw ('The test window did not exit successfully: {0:X8}' -f $process.ExitCode)
    }
}
$json=$result | ConvertTo-Json
if ($ReportPath) { $json | Set-Content -LiteralPath $ReportPath -Encoding UTF8 }
Write-Output $json
