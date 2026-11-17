# Third-party notices

CX Printer uses the following independently distributed components. The original AirPrint Installer and its native binaries are not runtime dependencies and are not included in the application distribution.

| Component | License / distribution terms | Upstream |
| --- | --- | --- |
| Microsoft Windows App SDK 1.7 | Microsoft Windows App SDK license and included third-party notices | Microsoft / WindowsAppSDK |
| .NET 8, ASP.NET Core, Windows Forms | MIT and bundled third-party notices | dotnet / runtime, aspnetcore, winforms |
| Microsoft.Extensions.Hosting.WindowsServices 8.0.1 | MIT | dotnet / runtime |
| Makaretu.Dns.Multicast 0.27.0 | MIT | richardschneider / net-mdns |
| Makaretu.Dns 2.0.1 | MIT | richardschneider / net-dns |
| Makaretu.Dns.Multicast transitive packages | Respective NuGet package license terms | As recorded in project.assets.json |

The build process copies package license files and metadata into the distribution's `Licenses` directory. Microsoft runtime and Windows App SDK notices supplied with published runtime binaries remain in the distribution.

The application icon was supplied by the user for this project. It is not covered by the third-party software licenses above.

ILSpy was used locally for static analysis. It is not included in the application runtime. OpenPrinting CUPS source was consulted as public format reference; the raster decoder is an independent implementation.
