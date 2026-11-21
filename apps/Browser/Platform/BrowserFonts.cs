using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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

    public override Uri Key => SystemFontsKey;
    public override int Count => families.Count;
    public override FontFamily this[int index] => families[index];
    public override IEnumerator<FontFamily> GetEnumerator() => families.GetEnumerator();
    public override void Initialize(IFontManagerImpl fontManager) => platform = fontManager;

    public static async Task InstallAsync()
    {
        var collection = new BrowserFontCollection();
        FontManager.Current.AddFontCollection(collection);
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
        families = names.Select(n => new FontFamily(n)).ToList();
        return names;
    }

    public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
    {
        glyphTypeface = null;
        string? device; lock (deviceNames) deviceNames.TryGetValue(familyName, out device);
        var cacheName = device ?? familyName;
        if (_glyphTypefaceCache.TryGetValue(cacheName, out var faces) && Nearest(faces, style, weight, stretch) is { } face)
        {
            var wantsItalic = style != FontStyle.Normal && face.Style == FontStyle.Normal;
            var wantsBold = weight >= FontWeight.DemiBold && face.Weight < FontWeight.DemiBold;
            if ((wantsItalic || wantsBold) && TryCreateSyntheticGlyphTypeface(face, style, weight, stretch, out var synthetic)) { glyphTypeface = synthetic; return true; }
            glyphTypeface = face; return true;
        }
        if (device is not null && requested.TryAdd(device, 0)) _ = LoadAsync(device);
        // Same as Avalonia's own system collection until the device files are read.
        return platform is not null && platform.TryCreateGlyphTypeface(familyName, style, weight, stretch, out glyphTypeface);
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

    private async Task LoadAsync(string family)
    {
        try
        {
            var json = await BrowserInterop.LoadLocalFontFamily(family);
            if (string.IsNullOrEmpty(json) || platform is null) return;
            var added = false;
            using (var document = JsonDocument.Parse(json))
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.GetString() is not { Length: > 0 } data) continue;
                    using var stream = new MemoryStream(Convert.FromBase64String(data), writable: false);
                    if (!platform.TryCreateGlyphTypeface(stream, FontSimulations.None, out var face)) continue;
                    var key = new FontCollectionKey(face.Style, face.Weight, face.Stretch);
                    foreach (var name in NamesOf(face, family)) _glyphTypefaceCache.GetOrAdd(name, _ => new()).TryAdd(key, face);
                    added = true;
                }
            if (added) DeviceFonts.NotifyChanged();
        }
        catch (Exception ex) { BrowserInterop.LogError("Police " + family + " : " + ex.Message); }
    }

    // The device family name plus the name inside the font (used by synthetic bold/italic lookups).
    private static IEnumerable<string> NamesOf(IGlyphTypeface face, string family) =>
        new[] { family, face.FamilyName }.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal);
}
