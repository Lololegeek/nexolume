using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexolume.Desktop.Editors.Writer;
using Nexolume.Desktop.Editors.Writer.Controls;
using Nexolume.Desktop.Editors.Writer.Views;
using OfficeBridge.Services;

internal static class RichTextChecks
{
    public static void Run()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "writer", "Texte enrichi"));
        var view = new WriterView(doc, new WritingOptions()); var window = new Window { Content = view, Width = 1440, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
        var box = view.FindControl<StackPanel>("ParagraphPanel")!.GetVisualDescendants().OfType<RichParagraphBox>().First();
        box.Focus(); window.KeyTextInput("Bonjour été 🦀 monde et amis."); Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync());
        var id = doc.Snapshot["content"]!["paragraphs"]![0]!["id"]!.GetValue<string>();
        void Select() { box.Focus(); box.CaretIndex = 14; box.SelectionStart = 8; box.SelectionEnd = 14; }
        Select(); Program.Click(view, "BoldButton"); Program.Pump(view.Pending);
        var paragraph = doc.Snapshot["content"]!["paragraphs"]![0]!;
        Program.Check(!paragraph["style"]!["bold"]!.GetValue<bool>() && paragraph["runs"]![0]!["start"]!.GetValue<int>() == 8 && paragraph["runs"]![0]!["length"]!.GetValue<int>() == 6 && paragraph["runs"]![0]!["style"]!["bold"]!.GetValue<bool>(), "Bold affects only selected UTF16 text");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(doc.Snapshot["content"]!["paragraphs"]![0]!["runs"] is null, "Undo removes the first selected style"); Program.Pump(doc.ExecuteAsync("Redo"));
        Select(); Program.Click(view, "ItalicButton"); Program.Pump(view.Pending); Program.Click(view, "UnderlineButton"); Program.Pump(view.Pending);
        view.FindControl<ComboBox>("FontFamilyBox")!.SelectedItem = "Georgia"; Program.Pump(view.Pending);
        view.FindControl<NumericUpDown>("FontSizeBox")!.Value = 24; Program.Pump(view.Pending);
        view.FindControl<ComboBox>("FontColorBox")!.SelectedIndex = 1; Program.Pump(view.Pending);
        view.FindControl<ComboBox>("HighlightBox")!.SelectedIndex = 1; Program.Pump(view.Pending);
        Program.Check(box.StyleAt(8)["font_size"]!.GetValue<double>() == 32 && box.StyleAt(8)["font_family"]!.GetValue<string>() == "Georgia" && box.StyleAt(8)["bold"]!.GetValue<bool>() && box.StyleAt(8)["italic"]!.GetValue<bool>() && box.StyleAt(8)["underline"]!.GetValue<bool>() && box.StyleAt(8)["color"]!.GetValue<string>() == "#285CB3", "Selection retains combined character styles");
        Program.Check(!box.StyleAt(0)["bold"]!.GetValue<bool>() && box.StyleAt(0)["font_size"]!.GetValue<double>() == 16, "Surrounding text keeps its style");
        box.SelectionStart = box.SelectionEnd = box.CaretIndex = 9;
        window.KeyTextInput("x"); Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync());
        Program.Check(box.Text == "Bonjour éxté 🦀 monde et amis." && box.Runs[0]!["length"]!.GetValue<int>() == 7, "Typing inside styled text extends its exact range");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(box.Text == "Bonjour été 🦀 monde et amis." && box.Runs[0]!["length"]!.GetValue<int>() == 6, "Typing undo restores text and styles"); Program.Pump(doc.ExecuteAsync("Redo"));
        // Two edits before the debounce must keep the middle formatting intact.
        box.CaretIndex = 0; box.SelectionStart = box.SelectionEnd = 0; window.KeyTextInput("1 ");
        box.CaretIndex = box.Text!.Length; box.SelectionStart = box.SelectionEnd = box.CaretIndex; window.KeyTextInput(" Fin"); Dispatcher.UIThread.RunJobs(); Program.Pump(view.FlushAsync());
        Program.Check(box.Runs[0]!["start"]!.GetValue<int>() == 10 && box.StyleAt(10)["bold"]!.GetValue<bool>() && !box.StyleAt(box.Text!.Length - 1)["bold"]!.GetValue<bool>(), "Noncontiguous draft edits preserve middle formatting");
        var joined=box.Text!;var styledBefore=box.Runs.DeepClone();
        box.CaretIndex=13;box.SelectionStart=box.SelectionEnd=13;window.KeyPressQwerty(PhysicalKey.Enter,RawInputModifiers.None);Dispatcher.UIThread.RunJobs();Program.Pump(view.Pending);
        var second=view.FindControl<StackPanel>("ParagraphPanel")!.GetVisualDescendants().OfType<RichParagraphBox>().Last();
        Program.Check(doc.Snapshot["content"]!["paragraphs"]!.AsArray().Count==2 && box.StyleAt(10)["bold"]!.GetValue<bool>() && second.StyleAt(0)["bold"]!.GetValue<bool>(),"Split preserves styles on both sides");
        second.Focus();second.CaretIndex=0;second.SelectionStart=second.SelectionEnd=0;window.KeyPressQwerty(PhysicalKey.Backspace,RawInputModifiers.None);Dispatcher.UIThread.RunJobs();Program.Pump(view.Pending);
        Program.Check(box.Text==joined && System.Text.Json.Nodes.JsonNode.DeepEquals(box.Runs,styledBefore),"Merge restores text and canonical styles");
        box.Focus();box.CaretIndex=17;box.SelectionStart=10;box.SelectionEnd=17;Program.Click(view,"ClearFormatButton");Program.Pump(view.Pending);
        Program.Check(!box.StyleAt(10)["bold"]!.GetValue<bool>() && box.StyleAt(10)["font_size"]!.GetValue<double>()==16,"Clear formatting affects selected characters");
        Program.Pump(doc.ExecuteAsync("Undo"));Program.Check(System.Text.Json.Nodes.JsonNode.DeepEquals(box.Runs,styledBefore),"Clear formatting undo");
        Program.Pump(view.OrganizeParagraphAsync(id,0));
        var copied=doc.Snapshot["content"]!["paragraphs"]![1]!;Program.Check(System.Text.Json.Nodes.JsonNode.DeepEquals(copied["runs"],styledBefore),"Duplicate paragraph includes mixed styles");Program.Pump(doc.ExecuteAsync("Undo"));
        var bad=box.Text!.IndexOf("🦀",StringComparison.Ordinal)+1;var revision=doc.Revision;bool rejected=false;
        try {Program.Pump(doc.ExecuteAsync("FormatText",new {id,start=bad,end=bad+1,style=new {bold=true}}));} catch(Exception ex) {rejected=ex.Message.Contains("boundary",StringComparison.OrdinalIgnoreCase);}
        Program.Check(rejected && doc.Revision==revision,"Invalid surrogate range is rejected atomically");
        var find=view.FindControl<TextBox>("FindInput")!;find.Text="1";view.FindControl<TextBox>("ReplaceInput")!.Text="Début";Dispatcher.UIThread.RunJobs();Program.Pump(view.ReplaceAllAsync());
        Program.Check(box.StyleAt(14)["bold"]!.GetValue<bool>() && box.Runs[0]!["start"]!.GetValue<int>()==14,"Replace all shifts rich ranges without flattening them");Program.Pump(doc.ExecuteAsync("Undo"));
        var path = Path.Combine(Program.Artifacts, "rich-text.oswriter"); Program.Pump(doc.SaveAsync(path)); using var reopened = Program.Get(DocumentSession.OpenAsync(engine, path));
        Program.Check(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["schema"]!.GetValue<int>()==3,"Rich native files use schema 3 so older versions reject rather than discard styles");
        Program.Check(System.Text.Json.Nodes.JsonNode.DeepEquals(doc.Snapshot["content"], reopened.Snapshot["content"]), "Native save/reopen preserves mixed styles");
        var presenter = box.GetVisualDescendants().OfType<RichTextPresenter>().Single();
        Program.Check(presenter.TextLayout.Height > 24 && presenter.TextLayout.TextLines.SelectMany(l => l.TextRuns).Any(r => r.Properties?.FontRenderingEmSize == 32), "Real shaped layout renders selected font size");
        var hit=presenter.TextLayout.HitTestTextRange(10,3).First();var point=presenter.TranslatePoint(new Point(hit.X+hit.Width/2,hit.Y+hit.Height/2),window)!.Value;
        window.MouseDown(point,MouseButton.Left);window.MouseUp(point,MouseButton.Left);Dispatcher.UIThread.RunJobs();
        Program.Check(box.CaretIndex>=10 && box.CaretIndex<=13,"Pointer caret uses the same rich glyph metrics");
        box.CaretIndex = 0; box.SelectionStart = box.SelectionEnd = 0; Program.Capture(window, "writer-rich-text");
        box.Text += string.Concat(Enumerable.Repeat(" Une phrase pour vérifier les retours à la ligne et la sélection avec plusieurs styles.",5));Dispatcher.UIThread.RunJobs();Program.Pump(view.FlushAsync());
        Program.Check(presenter.TextLayout.TextLines.Count>1,"Rich text wraps inside the document page");
        var theme=Application.Current!.RequestedThemeVariant;
        foreach(var light in new[]{false,true}) {
            Application.Current.RequestedThemeVariant=light?Avalonia.Styling.ThemeVariant.Light:Avalonia.Styling.ThemeVariant.Dark;
            foreach(var width in new[]{1000,1440,1920}) {window.Width=width;window.Height=width==1000?720:950;Dispatcher.UIThread.RunJobs();Program.Capture(window,$"writer-rich-{(light?"light":"dark")}-{width}");}
        }
        Application.Current.RequestedThemeVariant=theme;
        view.Detach(); window.Close(); Console.WriteLine("PASS Writer rich text: selection styles, actual shaped font sizes, Unicode typing/undo, noncontiguous drafts and native save/reopen");
    }
}
