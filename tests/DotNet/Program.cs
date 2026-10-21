using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OfficeBridge.Services;
using Nexolume.Desktop;
using Nexolume.Desktop.Editors.Writer.Views;
using Nexolume.Desktop.Home;
internal static class Program
{
    public static string Artifacts = Path.GetFullPath("artifacts/validation");
    [STAThread]
    public static void Main()
    {
        Directory.CreateDirectory(Artifacts);
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new() { UseHeadlessDrawing = false }).SetupWithoutStarting();
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
        try { PaginationChecks.Run(); RichTextChecks.Run(); Writer(); OfficeFeatureChecks.Writer(); ProductivityChecks.Writer(); ImageChecks.Writer(); TableChecks.Writer(); SelectionChecks.Writer(); LinkChecks.Writer(); FootnoteChecks.Writer(); RevisionChecks.Writer(); RevisionChecks.Columns(); FrameChecks.Writer(); WritingChecks.Run(); ReviewChecks.Spelling(); ReviewChecks.Search(); Console.WriteLine("PASS Avalonia Writer editing, rendered styles, undo/redo, native save/reopen"); Sheets(); OfficeSuiteChecks.Sheets(); ProductivityChecks.Sheets(); SheetsChecks.Run(); Slides(); OfficeSuiteChecks.Slides(); ProductivityChecks.Slides(); SlidesChecks.Run(); Home(); Shell(); VisualChecks.Home(); VisualChecks.Editors(); Console.WriteLine("PASS UI refinements: recent search, sum command, direct navigation and 24 theme/size renders"); }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    }
    public static void Pump(Task task) { var until = DateTime.UtcNow.AddSeconds(30); while (!task.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); if (DateTime.UtcNow > until) throw new TimeoutException(); } task.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs(); }
    public static T Get<T>(Task<T> task) { Pump(task); return task.Result; }
    public static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Click(Control root, string name) { var button = root.FindControl<Button>(name)!; Check(button.IsEnabled, name + " disabled"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    public static void Capture(Window window, string name) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96)); bitmap.Render(window); bitmap.Save(Path.Combine(Artifacts, name + ".png")); }
    private static void Writer()
    {
        using var engine = new OfficeEngine(); using var doc = Get(DocumentSession.CreateAsync(engine, "writer", "Notes de travail"));
        var editor = new WriterView(doc); Exception? error = null; editor.Error += ex => error = ex;
        var window = new Window { Width = 1200, Height = 900, Content = editor }; window.Show(); Dispatcher.UIThread.RunJobs();
        var box = editor.FindControl<StackPanel>("ParagraphPanel")!.GetVisualDescendants().OfType<TextBox>().First(); box.Focus(); window.KeyTextInput("Bonjour été 🦀"); Dispatcher.UIThread.RunJobs(); Pump(editor.FlushAsync()); Check(doc.Snapshot["content"]!["paragraphs"]![0]!["text"]!.GetValue<string>() == "Bonjour été 🦀", "UI text did not reach Rust");
        Click(editor, "BoldButton"); Pump(editor.Pending); Check(box.FontWeight == FontWeight.Bold, "bold rendering");
        Click(editor, "ItalicButton"); Pump(editor.Pending); Check(box.FontStyle == FontStyle.Italic, "italic rendering");
        Click(editor, "UnderlineButton"); Pump(editor.Pending); Check(((Grid)box.Parent!).Children[1].IsVisible, "underline rendering");
        Pump(doc.ExecuteAsync("Undo")); Check(!((Grid)box.Parent!).Children[1].IsVisible, "UI undo"); Pump(doc.ExecuteAsync("Redo"));
        Pump(editor.AddParagraphAsync()); var second = editor.FindControl<StackPanel>("ParagraphPanel")!.GetVisualDescendants().OfType<TextBox>().Last(); second.Focus(); window.KeyTextInput("Un deuxième paragraphe, vraiment modifiable."); Dispatcher.UIThread.RunJobs(); Pump(editor.FlushAsync());
        var path = Path.Combine(Artifacts, "writer.oswriter"); Pump(doc.SaveAsync(path)); using var reopened = Get(DocumentSession.OpenAsync(engine, path)); Check(System.Text.Json.Nodes.JsonNode.DeepEquals(doc.Snapshot["content"], reopened.Snapshot["content"]), "UI Writer roundtrip");
        Capture(window, "writer"); Check(error is null, error?.ToString() ?? ""); editor.Detach(); window.Close();
    }
    private static void Sheets()
    {
        using var engine = new OfficeEngine(); using var doc = Get(DocumentSession.CreateAsync(engine, "sheets", "Budget"));
        var editor = new Nexolume.Desktop.Editors.Sheets.Views.SheetsView(doc); Exception? error = null; editor.Error += ex => error = ex;
        var window = new Window { Width = 1200, Height = 800, Content = editor }; window.Show(); Dispatcher.UIThread.RunJobs();
        void Set(int row, string text) { editor.Surface.Select(0, row); Dispatcher.UIThread.RunJobs(); var input = editor.FindControl<TextBox>("CellInput")!; input.Focus(); input.SelectAll(); window.KeyTextInput(text); Dispatcher.UIThread.RunJobs(); Pump(editor.FlushAsync()); }
        Set(0, "10"); Set(1, "20"); Set(2, "=A1+A2"); Check(doc.Snapshot["content"]!["cells"]!["A3"]!["display"]!.GetValue<string>() == "30", "UI formula");
        Set(0, "15"); Check(doc.Snapshot["content"]!["cells"]!["A3"]!["display"]!.GetValue<string>() == "35", "UI recalc");
        Pump(doc.ExecuteAsync("Undo")); Check(doc.Snapshot["content"]!["cells"]!["A3"]!["display"]!.GetValue<string>() == "30", "UI formula undo"); Pump(doc.ExecuteAsync("Redo"));
        editor.Surface.Focus(); window.KeyPressQwerty(Avalonia.Input.PhysicalKey.ArrowRight, Avalonia.Input.RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); Check(editor.Surface.Column == 1, "grid keyboard navigation");
        var path = Path.Combine(Artifacts, "budget.ossheet"); Pump(doc.SaveAsync(path)); using var reopened = Get(DocumentSession.OpenAsync(engine, path)); Check(System.Text.Json.Nodes.JsonNode.DeepEquals(doc.Snapshot["content"], reopened.Snapshot["content"]), "Sheets roundtrip"); Capture(window, "sheets"); Check(error is null, error?.ToString() ?? ""); editor.Detach(); window.Close();
        Console.WriteLine("PASS Avalonia Sheets input, formulas, dependency recalculation, undo/redo, keyboard, save/reopen");
    }
    private static void Home() { var window = new Window { Width = 1360, Height = 900, Content = new HomeView() }; window.Show(); Capture(window, "home"); window.Close(); }
    private static void Slides()
    {
        using var engine = new OfficeEngine(); using var doc = Get(DocumentSession.CreateAsync(engine, "slides", "Présentation"));
        var editor = new Nexolume.Desktop.Editors.Slides.Views.SlidesView(doc); Exception? error = null; editor.Error += ex => error = ex;
        var window = new Window { Width = 1440, Height = 900, Content = editor }; window.Show(); Dispatcher.UIThread.RunJobs();
        Pump(editor.AddSlideAsync()); Pump(editor.AddObjectAsync("text")); var text = editor.FindControl<TextBox>("ObjectText")!; text.Text = "Une idée, plusieurs perspectives."; Dispatcher.UIThread.RunJobs(); Pump(editor.FlushAsync()); Pump(editor.AddObjectAsync("rectangle"));
        var objects = doc.Snapshot["content"]!["slides"]![1]!["objects"]!.AsArray(); var rect = objects[1]!; var id = rect["id"]!.GetValue<string>(); var x = rect["x"]!.GetValue<double>(); var y = rect["y"]!.GetValue<double>();
        var surface = editor.Surface;
        Avalonia.Point Position(double px, double py) => surface.TranslatePoint(new Avalonia.Point(px, py), window)!.Value;
        window.MouseDown(Position(x + 40, y + 40), Avalonia.Input.MouseButton.Left); window.MouseMove(Position(x + 140, y + 110)); window.MouseUp(Position(x + 140, y + 110), Avalonia.Input.MouseButton.Left); Dispatcher.UIThread.RunJobs(); Pump(editor.Pending);
        var moved = objects[1]!["x"]!.GetValue<double>(); Check(Math.Abs(moved - (x + 100)) < 1, "mouse drag did not commit");
        Pump(doc.ExecuteAsync("Undo")); Check(Math.Abs(objects[1]!["x"]!.GetValue<double>() - x) < 1, "drag undo"); Pump(doc.ExecuteAsync("Redo"));
        rect = objects[1]!; var right = rect["x"]!.GetValue<double>() + rect["width"]!.GetValue<double>(); var bottom = rect["y"]!.GetValue<double>() + rect["height"]!.GetValue<double>();
        window.MouseDown(Position(right, bottom), Avalonia.Input.MouseButton.Left); window.MouseMove(Position(right + 70, bottom + 40)); window.MouseUp(Position(right + 70, bottom + 40), Avalonia.Input.MouseButton.Left); Dispatcher.UIThread.RunJobs(); Pump(editor.Pending); Check(Math.Abs(objects[1]!["width"]!.GetValue<double>() - 310) < 1, "mouse resize");
        var path = Path.Combine(Artifacts, "presentation.osslides"); Pump(doc.SaveAsync(path)); using var reopened = Get(DocumentSession.OpenAsync(engine, path)); Check(System.Text.Json.Nodes.JsonNode.DeepEquals(doc.Snapshot["content"], reopened.Snapshot["content"]), "Slides roundtrip"); Capture(window, "slides"); Check(error is null, error?.ToString() ?? ""); editor.Detach(); window.Close();
        Console.WriteLine("PASS Avalonia Slides add slides/text/shape, mouse move/resize, undo/redo, save/reopen");
    }
    private static void Shell()
    {
        var window = new Nexolume.Desktop.Shell.MainWindow(); window.Show(); Pump(window.CreateAsync("writer"));
        var tabs = window.FindControl<TabControl>("DocumentTabs")!; var tab = (TabItem)tabs.SelectedItem!; var editor = (WriterView)tab.Content!;
        var box = editor.FindControl<StackPanel>("ParagraphPanel")!.GetVisualDescendants().OfType<TextBox>().First(); box.Focus(); window.KeyTextInput("Un espace familier pour vos documents."); Dispatcher.UIThread.RunJobs(); Pump(editor.FlushAsync());
        Click(editor, "HeadingStyleButton"); Pump(editor.Pending); Check(box.FontSize == 30, "heading style");
        var zoom = editor.FindControl<Slider>("ZoomSlider")!; zoom.Value = 90;
        Capture(window, "office-word-layout");
        Click(window, "ThemeButton"); Capture(window, "office-light"); Click(window, "ThemeButton");
        Pump(editor.Document.SaveAsync(Path.Combine(Artifacts, "shell.oswriter"))); Check(Get(window.CloseTabAsync(tab)), "close saved document"); Pump(window.CreateAsync("writer")); var unsaved = (TabItem)tabs.SelectedItem!;
        var cancel = window.CloseTabAsync(unsaved); Dispatcher.UIThread.RunJobs(); var dialog = window.OwnedWindows.Single(); Capture(dialog, "close-document-dialog"); Click(dialog, "CancelButton"); Check(!Get(cancel), "Cancel closing retains document");
        var discard = window.CloseTabAsync(unsaved); Dispatcher.UIThread.RunJobs(); Click(window.OwnedWindows.Single(), "DiscardButton"); Check(Get(discard), "Discard closes document");
        window.Close(); Dispatcher.UIThread.RunJobs();
        Console.WriteLine("PASS Shell: native window, document tabs, heading style, zoom, themes, close");
    }
}

