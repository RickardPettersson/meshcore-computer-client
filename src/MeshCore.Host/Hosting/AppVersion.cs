using System.Reflection;

namespace MeshCore.Host.Hosting;

/// <summary>The version this build was published as (release builds pass -p:Version=1.2.3), without the "+commit" suffix.</summary>
public static class AppVersion
{
    public static string Current { get; } =
        (typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?").Split('+')[0];
}
