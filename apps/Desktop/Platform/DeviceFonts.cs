using Avalonia.Threading;

namespace Nexolume.Desktop.Platform;

/// <summary>
/// Fonts installed on the device. On Windows Avalonia already sees every installed family, so this stays idle.
/// In the browser the head project plugs in the Local Font Access API (Chrome/Edge): families become usable
/// once the user allows it, and each family's files are read on first use, which raises <see cref="Changed"/>.
/// </summary>
public static class DeviceFonts
{
    /// <summary>Raised on the UI thread when families were listed or loaded: text using them must be laid out again.</summary>
    public static event Action? Changed;
    /// <summary>Family names reported by the device, sorted. Empty until allowed (browser) or on desktop.</summary>
    public static IReadOnlyList<string> Families { get; private set; } = [];
    /// <summary>Fonts shipped with the web version (open fonts with the metrics of Calibri, Cambria, Arial, Times New Roman and Courier New), under the names documents use.</summary>
    public static IReadOnlyList<string> Bundled { get; set; } = [];
    /// <summary>The browser can list device fonts but the user has not allowed it yet.</summary>
    public static bool CanRequest => requester is not null && Families.Count == 0;
    /// <summary>Device fonts are out of reach (browser without the Local Font Access API, or permission denied).</summary>
    public static bool Unavailable { get; private set; }

    private static Func<Task<IReadOnlyList<string>?>>? requester;

    /// <summary>Set by the browser head. <paramref name="request"/> returns null when the API is missing or refused.</summary>
    public static void Configure(Func<Task<IReadOnlyList<string>?>> request, bool supported)
    {
        requester = supported ? request : null;
        Unavailable = !supported;
    }

    /// <summary>Asks for the device fonts. Must run from a click so the browser can show its permission prompt.</summary>
    public static async Task<bool> RequestAsync()
    {
        if (requester is null) return false;
        var families = await requester();
        if (families is null || families.Count == 0) { Unavailable = true; NotifyChanged(); return false; }
        SetFamilies(families);
        return true;
    }

    public static void SetFamilies(IReadOnlyList<string> families)
    {
        Families = families;
        Unavailable = false;
        NotifyChanged();
    }

    public static void NotifyChanged() => Dispatcher.UIThread.Post(() => Changed?.Invoke(), DispatcherPriority.Background);
}
