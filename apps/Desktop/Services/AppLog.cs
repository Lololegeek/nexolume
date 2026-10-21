namespace Nexolume.Desktop.Services;

public static class AppLog { public static void Write(Exception error) { try { var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexolume"); Directory.CreateDirectory(path); File.AppendAllText(System.IO.Path.Combine(path, "errors.log"), $"{DateTimeOffset.Now:O} {error}\n"); } catch { } } }
