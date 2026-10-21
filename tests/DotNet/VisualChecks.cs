using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Nexolume.Desktop.Home;
using Nexolume.Desktop.Editors.Writer.Views;
using Nexolume.Desktop.Editors.Sheets.Views;
using Nexolume.Desktop.Editors.Slides.Views;
using OfficeBridge.Services;
internal static class VisualChecks
{
    public static void CaptureMatrix(Window window, string name)
    {
        var theme = Application.Current!.RequestedThemeVariant; var width = window.Width; var height = window.Height;
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current.RequestedThemeVariant = variant;
            foreach (var (w, h) in new[] { (1000, 720), (1440, 950), (1920, 1080) }) { window.Width = w; window.Height = h; Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Program.Capture(window, $"{name}-{(variant == ThemeVariant.Dark ? "dark" : "light")}-{w}"); }
        }
        window.Width = width; window.Height = height; Application.Current.RequestedThemeVariant = theme; Dispatcher.UIThread.RunJobs();
    }
    public static void Home()
    {
        var home = new HomeView(); var window = new Window { Content = home, Width = 1440, Height = 950 }; window.Show();
        var paths = new[] { "writer.oswriter", "budget.ossheet", "presentation.osslides" }.Select(name => System.IO.Path.Combine(Program.Artifacts, name)).ToArray(); home.SetRecent(paths); Dispatcher.UIThread.RunJobs();
        Program.Check(home.FindControl<StackPanel>("RecentPanel")!.Children.Count == 3, "Recent file list");
        Program.Click(home, "RecentSheets"); Program.Check(home.FindControl<StackPanel>("RecentPanel")!.Children.Count == 1, "Recent type filter"); Program.Click(home, "RecentAll");
        home.FindControl<ComboBox>("RecentSort")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); Program.Check(home.FindControl<StackPanel>("RecentPanel")!.Children.Count == 3, "Recent sorting retains documents");
        home.FindControl<TextBox>("RecentSearch")!.Text = "budget"; Dispatcher.UIThread.RunJobs(); Program.Check(home.FindControl<StackPanel>("RecentPanel")!.Children.Count == 1, "Recent search");
        home.FindControl<TextBox>("RecentSearch")!.Text = ""; Dispatcher.UIThread.RunJobs(); CaptureMatrix(window, "home-refined"); window.Close();
    }
    public static void Editors()
    {
        using var engine = new OfficeEngine();
        foreach (var kind in new[] { "writer", "sheets", "slides" })
        {
            var path = System.IO.Path.Combine(Program.Artifacts, kind switch { "writer" => "writer.oswriter", "sheets" => "budget.ossheet", _ => "presentation.osslides" });
            using var doc = Program.Get(DocumentSession.OpenAsync(engine, path));
            Nexolume.Desktop.Editors.EditorView editor = kind switch { "writer" => new WriterView(doc), "sheets" => new SheetsView(doc), _ => new SlidesView(doc) };
            var window = new Window { Content = editor, Width = 1440, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
            if (editor is SheetsView sheets) { sheets.Surface.Select(0, 3); Program.Click(sheets, "SumCellButton"); Program.Pump(sheets.Pending); Program.Check(doc.Snapshot["content"]!["cells"]!["A4"]!["display"]!.GetValue<string>() == "70", "Sum above command"); var address = sheets.FindControl<TextBox>("CellAddress")!; address.Focus(); address.Text = "Z120"; window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); Program.Pump(sheets.Pending); Program.Check(sheets.Surface.Address == "Z120", "Address navigation"); sheets.Surface.Select(0, 3); }
            if (editor is SlidesView slides) { slides.FindControl<ListBox>("SlideList")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); var selected = doc.Snapshot["content"]!["slides"]![1]!["objects"]![1]!["id"]!.GetValue<string>(); slides.Surface.Select(selected); Dispatcher.UIThread.RunJobs(); Program.Click(slides, "GreenFill"); Program.Pump(slides.Pending); Program.Check(doc.Snapshot["content"]!["slides"]![1]!["objects"]![1]!["fill"]!.GetValue<string>() == "#2D806C", "Preset color command"); }
            CaptureMatrix(window, kind + "-refined"); editor.Detach(); window.Close();
        }
    }
}
