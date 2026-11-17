using System.Reflection;
using System.Security.Cryptography;

namespace CxPrint.Service;

internal static class ServiceBuild
{
    private static readonly Assembly Assembly=typeof(ServiceBuild).Assembly;
    internal static string Version { get; }=Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetName().Version?.ToString() ?? "unknown";
    internal static string Sha256 { get; }=ReadHash();
    private static string ReadHash()
    {
        try
        {
            using var file=File.OpenRead(Assembly.Location);
            return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        }
        catch (IOException) { return "unavailable"; }
        catch (UnauthorizedAccessException) { return "unavailable"; }
    }
}
