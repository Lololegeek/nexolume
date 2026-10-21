using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Nexolume.Desktop.Editors.Writer.Spelling;

// ABI declarations follow the Windows SDK spellcheck.h IUnknown vtable order.
// https://learn.microsoft.com/windows/win32/api/spellcheck/
internal static class WindowsSpelling
{
    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFactory
    {
        IEnumString GetLanguages();
        int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string language);
        IChecker Create([MarshalAs(UnmanagedType.LPWStr)] string language);
    }
    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IChecker
    {
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetLanguage();
        IErrors Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
    }
    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IErrors { IError? Next(); }
    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IError
    {
        uint GetStart(); uint GetLength(); int GetAction();
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetReplacement();
    }

    public static SpellingResult Check(string text, string language)
    {
        if (!OperatingSystem.IsWindows()) return new(false, [], "La vérification orthographique nécessite Windows.");
        object? factoryObject = null; IChecker? checker = null; IErrors? errors = null;
        try
        {
            factoryObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"))!);
            var factory = (IFactory)factoryObject!;
            if (factory.IsSupported(language) == 0) return new(false, [], "Le dictionnaire « " + language + " » n’est pas installé. Ajoutez les fonctionnalités linguistiques correspondantes dans les paramètres Windows.");
            checker = factory.Create(language);
            if (string.IsNullOrWhiteSpace(text)) return new(true, []);
            errors = checker.Check(text); var result = new List<SpellingIssue>();
            while (result.Count < 200)
            {
                var error = errors.Next(); if (error is null) break;
                try
                {
                    var start = checked((int)error.GetStart()); var length = checked((int)error.GetLength()); var action = error.GetAction();
                    if (action == 0 || start < 0 || length <= 0 || start > text.Length - length) continue;
                    var word = text.Substring(start, length); var suggestions = new List<string>();
                    if (action == 3) suggestions.Add("");
                    else
                    {
                        var replacement = error.GetReplacement(); if (!string.IsNullOrEmpty(replacement)) suggestions.Add(replacement);
                        var values = checker.Suggest(word);
                        try
                        {
                            var buffer = new string[1]; var fetched = Marshal.AllocCoTaskMem(sizeof(int));
                            try { for (int i = 0; i < 8 && values.Next(1, buffer, fetched) == 0; i++) if (!suggestions.Contains(buffer[0])) suggestions.Add(buffer[0]); }
                            finally { Marshal.FreeCoTaskMem(fetched); }
                        }
                        finally { Marshal.ReleaseComObject(values); }
                    }
                    result.Add(new(start, length, word, suggestions));
                }
                finally { Marshal.ReleaseComObject(error); }
            }
            return new(true, result);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or OverflowException or TypeLoadException)
        {
            Services.AppLog.Write(ex); return new(false, [], "Le correcteur Windows est indisponible pour le moment.");
        }
        finally
        {
            if (errors is not null) Marshal.ReleaseComObject(errors);
            if (checker is not null) Marshal.ReleaseComObject(checker);
            if (factoryObject is not null) Marshal.ReleaseComObject(factoryObject);
        }
    }
}
