using Avalonia;
using Avalonia.Controls;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Nexolume.Desktop.Editors.Writer.Printing;

public static class WindowsPrinter
{
    // The named-printer path also serves command-line printing; only the dialog path asks the user.
    public static async Task<bool> PrintAsync(WriterPrintLayout layout, Window? owner = null, string? printer = null, string? output = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("L’impression nécessite Windows.");
        var dialog = new PrintDialogData { Size = Marshal.SizeOf<PrintDialogData>(), Owner = owner?.TryGetPlatformHandle()?.Handle ?? 0,
            Flags = 0x100 | 0x40000 | 0x4 | 0x100000, FromPage = 1, ToPage = (ushort)layout.Pages.Count, MinPage = 1, MaxPage = (ushort)layout.Pages.Count, Copies = 1 };
        var started = false;
        try
        {
            if (printer is null)
            {
                // Initialize paper/orientation from the document without changing printer defaults.
                var defaults = new PrintDialogData { Size = dialog.Size, Flags = 0x400 };
                if (PrintDlg(ref defaults)) { dialog.DevMode = defaults.DevMode; dialog.DevNames = defaults.DevNames; SetPaper(dialog.DevMode, layout.Settings); }
                if (!PrintDlg(ref dialog)) { var code = CommDlgExtendedError(); if (code != 0) throw new Win32Exception((int)code, "Impossible d’ouvrir le dialogue d’impression."); return false; }
            }
            else
            {
                var mode = await Task.Run(() => GetMode(printer, layout.Settings));
                try { dialog.Dc = CreateDC("WINSPOOL", printer, null, mode); } finally { Marshal.FreeHGlobal(mode); }
                if (dialog.Dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Imprimante inaccessible : " + printer);
            }
            var dc = dialog.Dc;
            var first = (dialog.Flags & 2) != 0 ? dialog.FromPage - 1 : 0; var last = (dialog.Flags & 2) != 0 ? dialog.ToPage : layout.Pages.Count;
            var info = new DocInfo { Size = Marshal.SizeOf<DocInfo>(), Name = layout.Title, Output = output };
            if (await Task.Run(() => StartDoc(dc, ref info)) <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Impossible de démarrer l’impression.");
            started = true;
            for (var index = first; index < last; index++)
            {
                using var bitmap = new SKBitmap(new SKImageInfo((int)Math.Ceiling(layout.Settings.Width * 2), (int)Math.Ceiling(layout.Settings.Height * 2), SKColorType.Bgra8888, SKAlphaType.Opaque));
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.White);
                    var visual = new PrintPageControl(layout, index, 2); visual.Measure(new Size(layout.Settings.Width * 2, layout.Settings.Height * 2)); visual.Arrange(new Rect(visual.DesiredSize));
                    await DrawingContextHelper.RenderAsync(canvas, visual);
                }
                await Task.Run(() => PrintBitmap(dc, bitmap, layout.Settings));
            }
            if (await Task.Run(() => EndDoc(dc)) <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "L’impression n’a pas pu être finalisée.");
            started = false; return true;
        }
        finally { if (started && dialog.Dc != 0) AbortDoc(dialog.Dc); if (dialog.Dc != 0) DeleteDC(dialog.Dc); if (dialog.DevMode != 0) GlobalFree(dialog.DevMode); if (dialog.DevNames != 0) GlobalFree(dialog.DevNames); }
    }
    private static void PrintBitmap(nint dc, SKBitmap bitmap, PageSettings settings)
    {
        if (StartPage(dc) <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Impossible de démarrer la page.");
        var dpiX = GetDeviceCaps(dc, 88); var dpiY = GetDeviceCaps(dc, 90);
        var physicalWidth = GetDeviceCaps(dc, 110); var physicalHeight = GetDeviceCaps(dc, 111);
        var scale = Math.Min(1, Math.Min(physicalWidth / (settings.Width * dpiX / 96), physicalHeight / (settings.Height * dpiY / 96)));
        var w = (int)Math.Round(settings.Width * dpiX / 96 * scale); var h = (int)Math.Round(settings.Height * dpiY / 96 * scale);
        var x = (physicalWidth - w) / 2 - GetDeviceCaps(dc, 112); var y = (physicalHeight - h) / 2 - GetDeviceCaps(dc, 113);
        var header = new BitmapInfo { Size = 40, Width = bitmap.Width, Height = -bitmap.Height, Planes = 1, Bits = 32, ImageSize = (uint)bitmap.ByteCount };
        SetStretchBltMode(dc, 4);
        if (StretchDIBits(dc, x, y, w, h, 0, 0, bitmap.Width, bitmap.Height, bitmap.GetPixels(), ref header, 0, 0x00CC0020) <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Le pilote refuse le rendu de la page.");
        if (EndPage(dc) <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Impossible de terminer la page.");
    }
    private static nint GetMode(string printer, PageSettings settings)
    {
        if (!OpenPrinter(printer, out var handle, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint mode = 0;
        try { var bytes = DocumentProperties(0, handle, printer, 0, 0, 0); if (bytes < 220) throw new InvalidOperationException("Le pilote ne fournit pas de réglages papier."); mode = Marshal.AllocHGlobal(bytes); if (DocumentProperties(0, handle, printer, mode, 0, 2) < 0) throw new InvalidOperationException("Réglages du pilote indisponibles."); SetMode(mode, settings); if (DocumentProperties(0, handle, printer, mode, mode, 10) < 0) throw new InvalidOperationException("Le pilote refuse le format papier."); var result = mode; mode = 0; return result; }
        finally { if (mode != 0) Marshal.FreeHGlobal(mode); ClosePrinter(handle); }
    }
    private static void SetPaper(nint global, PageSettings settings) { if (global == 0) return; var data = GlobalLock(global); if (data == 0) return; try { SetMode(data, settings); } finally { GlobalUnlock(global); } }
    private static void SetMode(nint data, PageSettings settings)
    {
        if ((ushort)Marshal.ReadInt16(data, 68) < 84) return;
        Marshal.WriteInt32(data, 72, Marshal.ReadInt32(data, 72) | 0xF);
        Marshal.WriteInt16(data, 76, (short)(settings.Width > settings.Height ? 2 : 1));
        Marshal.WriteInt16(data, 78, 0); // custom dimensions, tenths of millimetres, unrotated paper
        Marshal.WriteInt16(data, 80, (short)Math.Round(Math.Max(settings.Width, settings.Height) * 254 / 96));
        Marshal.WriteInt16(data, 82, (short)Math.Round(Math.Min(settings.Width, settings.Height) * 254 / 96));
    }
    [StructLayout(LayoutKind.Sequential)] private struct PrintDialogData { public int Size; public nint Owner, DevMode, DevNames, Dc; public uint Flags; public ushort FromPage, ToPage, MinPage, MaxPage, Copies; public nint Instance, CustomData, PrintHook, SetupHook, PrintTemplateName, SetupTemplateName, PrintTemplate, SetupTemplate; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DocInfo { public int Size; [MarshalAs(UnmanagedType.LPWStr)] public string Name; [MarshalAs(UnmanagedType.LPWStr)] public string? Output; public nint DataType; public uint Type; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important; }
    [DllImport("comdlg32.dll", EntryPoint = "PrintDlgW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintDlg(ref PrintDialogData data);
    [DllImport("comdlg32.dll")] private static extern uint CommDlgExtendedError();
    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateDC(string driver, string device, string? output, nint mode);
    [DllImport("gdi32.dll", EntryPoint = "StartDocW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int StartDoc(nint dc, ref DocInfo info);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int StartPage(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndPage(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndDoc(nint dc);
    [DllImport("gdi32.dll")] private static extern int AbortDoc(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(nint dc, int index);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int StretchDIBits(nint dc, int x, int y, int width, int height, int sx, int sy, int sw, int sh, nint bits, ref BitmapInfo info, uint usage, uint rop);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint data);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint data);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint data);
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool OpenPrinter(string name, out nint printer, nint defaults);
    [DllImport("winspool.drv")] private static extern bool ClosePrinter(nint printer);
    [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", CharSet = CharSet.Unicode)] private static extern int DocumentProperties(nint owner, nint printer, string name, nint output, nint input, uint mode);
}
