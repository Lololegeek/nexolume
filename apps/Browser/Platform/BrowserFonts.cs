using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace Nexolume.Desktop.Platform;

/// <summary>
/// Replaces Avalonia's system font collection in the browser (where it only knows the bundled Inter) with
/// the device fonts exposed by the Local Font Access API. Families are listed once the user allows it,
/// then each family's files are read the first time a text asks for it; <see cref="DeviceFonts.Changed"/>
/// tells the views to lay their text out again with the real font.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class BrowserFontCollection : FontCollectionBase
{
    private static readonly Uri SystemFontsKey = new("fonts:SystemFonts", UriKind.Absolute);
    private IFontManagerImpl? platform;
    private readonly Dictionary<string, string> deviceNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> requested = new(StringComparer.OrdinalIgnoreCase);
    private List<FontFamily> families = [];

    // Open fonts in wwwroot/fonts, drawn with the metrics of the Office fonts they stand in for, so pages break
    // at the same places. A device font with the same name (Chrome/Edge, once allowed) takes precedence.
    private static readonly (string Family, string[] Aliases, string[] Files)[] BundledFamilies =
    [
        ("Carlito", ["Calibri"], ["Carlito-Regular.ttf", "Carlito-Bold.ttf", "Carlito-Italic.ttf", "Carlito-BoldItalic.ttf"]),
        ("Caladea", ["Cambria"], ["Caladea-Regular.ttf", "Caladea-Bold.ttf", "Caladea-Italic.ttf", "Caladea-BoldItalic.ttf"]),
        ("Arimo", ["Arial", "Helvetica", "Liberation Sans"], ["Arimo-Regular.ttf", "Arimo-Italic.ttf"]),
        ("Tinos", ["Times New Roman", "Times", "Liberation Serif"], ["Tinos-Regular.ttf", "Tinos-Bold.ttf", "Tinos-Italic.ttf", "Tinos-BoldItalic.ttf"]),
        ("Cousine", ["Courier New", "Courier", "Liberation Mono"], ["Cousine-Regular.ttf", "Cousine-Bold.ttf", "Cousine-Italic.ttf", "Cousine-BoldItalic.ttf"]),
    ];
    private static readonly Dictionary<string, int> BundledByName = BuildBundledIndex();

    private static Dictionary<string, int> BuildBundledIndex()
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < BundledFamilies.Length; i++)
        {
            index[BundledFamilies[i].Family] = i;
            foreach (var alias in BundledFamilies[i].Aliases) index[alias] = i;
        }
        return index;
    }

    public override Uri Key => SystemFontsKey;
    public override int Count => families.Count;
    public override FontFamily this[int index] => families[index];
    public override IEnumerator<FontFamily> GetEnumerator() => families.GetEnumerator();
    public override void Initialize(IFontManagerImpl fontManager) => platform = fontManager;

    public static async Task InstallAsync()
    {
        var collection = new BrowserFontCollection();
        FontManager.Current.AddFontCollection(collection);
        DeviceFonts.Bundled = ["Calibri", "Cambria", "Arial", "Times New Roman", "Courier New", .. BundledFamilies.Select(b => b.Family)];
        var supported = BrowserInterop.LocalFontsSupported();
        DeviceFonts.Configure(collection.ListAsync, supported);
        // Already allowed on an earlier visit: list without waiting for a click.
        if (supported && await BrowserInterop.LocalFontsPermission() == "granted")
            if (await collection.ListAsync() is { Count: > 0 } names) DeviceFonts.SetFamilies(names);
    }

    private async Task<IReadOnlyList<string>?> ListAsync()
    {
        var json = await BrowserInterop.ListLocalFonts();
        if (string.IsNullOrEmpty(json)) return null;
        var names = new List<string>();
        using (var document = JsonDocument.Parse(json))
            foreach (var item in document.RootElement.EnumerateArray())
                if (item.GetString() is { Length: > 0 } name && !name.StartsWith('@')) names.Add(name);
        lock (deviceNames) foreach (var name in names) deviceNames[name] = name;
        // The real Calibri, Arial… now replace their stand-ins.
        foreach (var name in names) if (BundledByName.TryGetValue(name, out var b) && !string.Equals(BundledFamilies[b].Family, name, StringComparison.OrdinalIgnoreCase)) _glyphTypefaceCache.TryRemove(name, out _);
        families = names.Select(n => new FontFamily(n)).ToList();
        return names;
    }

    public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
    {
        glyphTypeface = null;
        string? device; lock (deviceNames) deviceNames.TryGetValue(familyName, out device);
        var cacheName = device ?? familyName;
        if (TryCached(cacheName, style, weight, stretch, out glyphTypeface)) return true;
        if (device is not null) { if (requested.TryAdd(device, 0)) _ = LoadAsync(device); }
        else if (BundledByName.TryGetValue(familyName, out var bundled)) RequestBundled(bundled);
        else if (!familyName.StartsWith('$') && !_glyphTypefaceCache.ContainsKey(familyName))
        {
            // A font this device doesn't have (Aptos, Segoe…): draw it with the open font of the same kind
            // rather than whichever face the browser's empty font manager hands back.
            var substitute = SubstituteFor(familyName);
            if (TryCached(BundledFamilies[substitute].Family, style, weight, stretch, out glyphTypeface)) return true;
            RequestBundled(substitute);
        }
        // Same as Avalonia's own system collection until the device files are read.
        return platform is not null && platform.TryCreateGlyphTypeface(familyName, style, weight, stretch, out glyphTypeface);
    }

    private bool TryCached(string name, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
    {
        glyphTypeface = null;
        if (!_glyphTypefaceCache.TryGetValue(name, out var faces) || Nearest(faces, style, weight, stretch) is not { } face) return false;
        var wantsItalic = style != FontStyle.Normal && face.Style == FontStyle.Normal;
        var wantsBold = weight >= FontWeight.DemiBold && face.Weight < FontWeight.DemiBold;
        glyphTypeface = (wantsItalic || wantsBold) && TryCreateSyntheticGlyphTypeface(face, style, weight, stretch, out var synthetic) ? synthetic : face;
        return true;
    }

    private void RequestBundled(int index)
    {
        if (requested.TryAdd("bundle:" + BundledFamilies[index].Family, 0)) _ = LoadBundledAsync(index);
    }

    // Index in BundledFamilies: Cousine for monospaced names, Tinos/Caladea for serif ones, Carlito otherwise.
    private static int SubstituteFor(string name)
    {
        var n = name.ToLowerInvariant();
        string[] mono = ["mono", "courier", "consol", "code", "typewriter", "fixed", "lucida console"];
        string[] serif = ["serif", "times", "georgia", "garamond", "book", "roman", "palatino", "baskerville", "bodoni", "century", "didot", "minion", "constantia", "cambria"];
        if (mono.Any(n.Contains)) return Array.FindIndex(BundledFamilies, b => b.Family == "Cousine");
        if (!n.Contains("sans") && serif.Any(n.Contains)) return Array.FindIndex(BundledFamilies, b => b.Family == "Tinos");
        return Array.FindIndex(BundledFamilies, b => b.Family == "Carlito");
    }

    // Fallback for text whose font is missing (Aptos, a "$Default" lookup…): the base collection would pick
    // whichever loaded face comes first, often Cousine. Avalonia's own fallback (Inter) is tried first.
    public override bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch, string? familyName, CultureInfo? culture, out Typeface typeface)
    {
        if (familyName is not null && _glyphTypefaceCache.ContainsKey(familyName))
            return base.TryMatchCharacter(codepoint, style, weight, stretch, familyName, culture, out typeface);
        if (platform is not null && platform.TryMatchCharacter(codepoint, style, weight, stretch, culture, out typeface)) return true;
        return base.TryMatchCharacter(codepoint, style, weight, stretch, familyName, culture, out typeface);
    }

    private static IGlyphTypeface? Nearest(ConcurrentDictionary<FontCollectionKey, IGlyphTypeface?> faces, FontStyle style, FontWeight weight, FontStretch stretch)
    {
        if (faces.TryGetValue(new FontCollectionKey(style, weight, stretch), out var exact) && exact is not null) return exact;
        return faces.Values.OfType<IGlyphTypeface>()
            .OrderBy(f => (f.Style == FontStyle.Normal) == (style == FontStyle.Normal) ? 0 : 1)
            .ThenBy(f => Math.Abs((int)f.Stretch - (int)stretch))
            .ThenBy(f => Math.Abs((int)f.Weight - (int)weight))
            .FirstOrDefault();
    }

    private Task LoadAsync(string family) => LoadAsync(family, () => BrowserInterop.LoadLocalFontFamily(family), [family]);

    private Task LoadBundledAsync(int index)
    {
        var (family, aliases, files) = BundledFamilies[index];
        string[] names;
        // Skip the aliases a device font already covers.
        lock (deviceNames) names = [family, .. aliases.Where(a => !deviceNames.ContainsKey(a))];
        return LoadAsync(family, () => BrowserInterop.LoadBundledFonts(JsonSerializer.Serialize(files, BrowserFontsJson.Default.StringArray)), names);
    }

    private async Task LoadAsync(string family, Func<Task<string>> read, string[] cacheNames)
    {
        try
        {
            var json = await read();
            if (string.IsNullOrEmpty(json) || platform is null) return;
            var added = false;
            using (var document = JsonDocument.Parse(json))
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.GetString() is not { Length: > 0 } data) continue;
                    using var stream = new MemoryStream(Convert.FromBase64String(data), writable: false);
                    if (!platform.TryCreateGlyphTypeface(stream, FontSimulations.None, out var face)) continue;
                    var key = new FontCollectionKey(face.Style, face.Weight, face.Stretch);
                    foreach (var name in NamesOf(face, cacheNames)) _glyphTypefaceCache.GetOrAdd(name, _ => new()).TryAdd(key, face);
                    added = true;
                }
            if (added) DeviceFonts.NotifyChanged();
        }
        catch (Exception ex) { BrowserInterop.LogError("Police " + family + " : " + ex.Message); }
    }

    // The device family name plus the name inside the font (used by synthetic bold/italic lookups).
    private static IEnumerable<string> NamesOf(IGlyphTypeface face, string[] names) =>
        names.Append(face.FamilyName).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class BrowserFontsJson : System.Text.Json.Serialization.JsonSerializerContext;
