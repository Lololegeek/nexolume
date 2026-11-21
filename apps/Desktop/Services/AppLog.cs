namespace Nexolume.Desktop.Services;

/// <summary>Error log: errors.log on Windows, the browser console on the web.</summary>
public static class AppLog { public static void Write(Exception error) { try { Platform.AppPlatform.Current.Log(error); } catch { } } }
