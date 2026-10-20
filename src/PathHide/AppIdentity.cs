using System.Reflection;

namespace PathHide;

/// <summary>
/// The product name as the build stamps it from <c>Directory.Build.props</c>, so every window title,
/// menu and dialog that names the app reads one value.
/// </summary>
internal static class AppIdentity
{
    internal static string Name { get; } =
        typeof(AppIdentity).Assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product;
}
