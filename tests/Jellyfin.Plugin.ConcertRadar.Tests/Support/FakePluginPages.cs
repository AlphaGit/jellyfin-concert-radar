namespace Jellyfin.Plugin.PluginPages;

/// <summary>
/// Stand-in for Plugin Pages' payload type. The real one is a Newtonsoft
/// <c>JObject</c>; all the gateway knows about either is that the type has a static
/// <c>Parse(string)</c>, which is how it builds the argument without naming the type.
/// </summary>
public sealed class FakePayload
{
    private FakePayload(string json)
    {
        Json = json;
    }

    /// <summary>Gets the JSON the gateway handed over, verbatim.</summary>
    public string Json { get; }

    public static FakePayload Parse(string json) => new(json);
}

/// <summary>
/// Stand-in for <c>Jellyfin.Plugin.PluginPages.PluginInterface</c>, deliberately in that
/// namespace and with those member names: the gateway finds it by full type name, so the
/// shape here is the contract. Static, because the real one is static.
/// </summary>
public static class PluginInterface
{
    private static readonly List<FakePayload> RegisteredPages = new();
    private static readonly List<string> RemovedPages = new();

    /// <summary>Gets every payload passed to <see cref="RegisterPage"/> since the last reset.</summary>
    public static IReadOnlyList<FakePayload> Registered => RegisteredPages;

    /// <summary>Gets every id passed to <see cref="RemovePage"/> since the last reset.</summary>
    public static IReadOnlyList<string> Removed => RemovedPages;

    /// <summary>Gets or sets an exception <see cref="RegisterPage"/> throws instead of recording.</summary>
    public static Exception? RegisterThrows { get; set; }

    public static void RegisterPage(FakePayload payload)
    {
        if (RegisterThrows is not null)
        {
            throw RegisterThrows;
        }

        RegisteredPages.Add(payload);
    }

    public static void RemovePage(string id) => RemovedPages.Add(id);

    /// <summary>Clears the recorded calls. Static state, so every test starts from here.</summary>
    public static void Reset()
    {
        RegisteredPages.Clear();
        RemovedPages.Clear();
        RegisterThrows = null;
    }
}
