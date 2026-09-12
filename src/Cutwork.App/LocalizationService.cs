using System.Collections;
using System.Globalization;
using System.Resources;

namespace Flamoris.Cutwork.App;

public sealed class LocalizationService
{
    private const string ResourceBaseName = "Flamoris.Cutwork.App.Resources.Strings";
    private static readonly ResourceManager ResourceManager = new(ResourceBaseName, typeof(LocalizationService).Assembly);

    public static LocalizationService Current { get; } = new();

    public CultureInfo Culture { get; private set; } = new("ja-JP");

    public string this[string key] =>
        ResourceManager.GetString(key, Culture)
        ?? throw new MissingManifestResourceException($"Missing UI resource key: {key}");

    public void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        Culture = culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? new CultureInfo("en-US")
            : new CultureInfo("ja-JP");
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
    }

    public static IReadOnlySet<string> GetKeys(CultureInfo culture)
    {
        var resources = ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: true)
            ?? throw new MissingManifestResourceException($"Missing UI resource catalog: {culture.Name}");
        return resources.Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .ToHashSet(StringComparer.Ordinal);
    }
}
