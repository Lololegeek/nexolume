using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nexolume.Desktop.Localization;

public static class Localization
{
    public sealed record Language(string Code, string Name);
    public static IReadOnlyList<Language> Languages { get; } = [new("fr", "Français"), new("en", "English"), new("es", "Español"), new("de", "Deutsch"), new("it", "Italiano"), new("pt", "Português")];
    public static string LanguageCode { get; private set; } = "fr";
    private static readonly Dictionary<string, Dictionary<string, string>> catalogues = [];
    private static readonly ConditionalWeakTable<AvaloniaObject, Dictionary<string, Func<object?>>> properties = new();
    private static readonly List<WeakReference<AvaloniaObject>> targets = [];
    private static readonly Dictionary<string, string> keys = [];

    public static void Initialize()
    {
        var assembly = typeof(Localization).Assembly;
        foreach (var language in Languages)
        {
            // French is the source language: a missing catalogue falls back to the source strings instead of aborting startup.
            using var stream = assembly.GetManifestResourceStream($"Nexolume.Desktop.Localization.Locales.{language.Code}.json");
            if (stream is null) continue;
            catalogues[language.Code] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }
        ChangeLanguage(Services.Preferences.Load().Language);
    }

    public static string Key(string text)
    {
        if (!keys.TryGetValue(text, out var key)) keys[text] = key = "Ui." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
        return key;
    }
    public static string T(string text) => catalogues.TryGetValue(LanguageCode, out var catalogue) && catalogue.TryGetValue(text, out var value) ? value : text;
    public static string F(string text, params object?[] arguments) => string.Format(CultureInfo.GetCultureInfo(LanguageCode), T(text), arguments);

    public static void ChangeLanguage(string code)
    {
        if (!Languages.Any(l => l.Code == code)) code = "fr";
        LanguageCode = code;
        if (Application.Current is { } app && catalogues.TryGetValue(code, out var catalogue))
            foreach (var (source, translation) in catalogue) app.Resources[Key(source)] = translation;
        for (var i = targets.Count - 1; i >= 0; i--)
        {
            if (!targets[i].TryGetTarget(out var target)) { targets.RemoveAt(i); continue; }
            if (properties.TryGetValue(target, out var bound)) foreach (var (property, value) in bound.ToArray()) Apply(target, property, value);
        }
    }

    public static TControl Localize<TControl>(TControl control, params (string Property, Func<object?> Value)[] values) where TControl : AvaloniaObject
    {
        foreach (var (property, value) in values) { if (property == "ItemsSource" && control is ComboBox combo) LocalizeItems(combo); else Set(control, property, value); }
        return control;
    }
    public static void Set(AvaloniaObject target, string property, Func<object?> value)
    {
        if (!properties.TryGetValue(target, out var bound)) { bound = []; properties.Add(target, bound); targets.Add(new(target)); }
        bound[property] = value; Apply(target, property, value);
    }
    private static void Apply(AvaloniaObject target, string property, Func<object?> value)
    {
        var registered = property == "ToolTip" ? ToolTip.TipProperty : AvaloniaPropertyRegistry.Instance.FindRegistered(target, property);
        if (registered is not null) target.SetValue(registered, value());
    }
    public static void SetTip(Control control, Func<object?> value) => Set(control, "ToolTip", value);
    public static void LocalizeItems(ComboBox combo)
    {
        combo.ItemTemplate ??= new FuncDataTemplate<string>((text, _) => Localize(new TextBlock(), ("Text", () => T(text ?? ""))));
    }
}
