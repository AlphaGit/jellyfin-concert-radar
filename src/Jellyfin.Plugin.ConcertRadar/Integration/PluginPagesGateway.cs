using System.Reflection;

namespace Jellyfin.Plugin.ConcertRadar.Integration;

/// <summary>
/// Reaches the optional Plugin Pages integration by reflection, so the plugin references
/// neither that assembly nor Newtonsoft.Json. Same contract as the sibling New Releases plugin
/// (its <c>specs/003-jellyfin-12-compat/contracts/plugin-pages-registration.md</c>).
/// </summary>
public sealed class PluginPagesGateway
{
    private const string InterfaceTypeName = "Jellyfin.Plugin.PluginPages.PluginInterface";

    private readonly Func<IEnumerable<Assembly>> _assemblies;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginPagesGateway"/> class.
    /// </summary>
    /// <param name="assemblies">
    /// Where to look for the integration. Injected so a test can supply a stand-in without
    /// touching load contexts; production passes every loaded assembly.
    /// </param>
    public PluginPagesGateway(Func<IEnumerable<Assembly>> assemblies)
    {
        _assemblies = assemblies;
    }

    /// <summary>Registers the page. Returns false when the integration is unavailable.</summary>
    /// <param name="payloadJson">The page entry, as JSON.</param>
    public bool TryRegisterPage(string payloadJson)
    {
        try
        {
            return Register(payloadJson);
        }
        catch (Exception)
        {
            // Unavailable covers a Plugin Pages that is present but not ready, or that changed
            // shape. The plugin must load either way, so every failure is the same failure.
            return false;
        }
    }

    /// <summary>Withdraws the page. Returns false when the integration is unavailable.</summary>
    /// <param name="id">The page entry's identifier.</param>
    public bool TryRemovePage(string id)
    {
        try
        {
            return Remove(id);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool Register(string payloadJson)
    {
        var register = FindInterfaceType()?.GetMethod("RegisterPage", BindingFlags.Public | BindingFlags.Static);
        if (register is null)
        {
            return false;
        }

        var payloadType = register.GetParameters()[0].ParameterType;
        var parse = payloadType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        if (parse is null)
        {
            return false;
        }

        register.Invoke(null, [parse.Invoke(null, [payloadJson])]);
        return true;
    }

    private bool Remove(string id)
    {
        var remove = FindInterfaceType()?.GetMethod("RemovePage", BindingFlags.Public | BindingFlags.Static);
        if (remove is null)
        {
            return false;
        }

        remove.Invoke(null, [id]);
        return true;
    }

    /// <summary>
    /// Plugin Pages loads into its own <c>AssemblyLoadContext</c>, so the type is looked up by
    /// full name across every assembly the source offers rather than by assembly identity.
    /// </summary>
    private Type? FindInterfaceType()
    {
        foreach (var assembly in _assemblies())
        {
            var type = assembly.GetType(InterfaceTypeName, throwOnError: false);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }
}
