using Avalonia.Controls;
using Avalonia.Threading;
using OfficeBridge.Services;
using Nexolume.Desktop.Editors.Slides.Views;
internal static class SlidesChecks
{
    public static void Run()
    {
        using var engine = new OfficeEngine(); using var doc = Program.Get(DocumentSession.CreateAsync(engine, "slides", "Formes et diapositive")); var view = new SlidesView(doc); var window = new Window { Content = view, Width = 1500, Height = 950 }; window.Show(); Dispatcher.UIThread.RunJobs();
        System.Text.Json.Nodes.JsonNode Slide() => doc.Snapshot["content"]!["slides"]![0]!;
        System.Text.Json.Nodes.JsonNode Object(int i) => Slide()["objects"]![i]!;
        foreach (var kind in new[] { "rounded", "triangle", "diamond", "line" }) { Program.Pump(view.AddObjectAsync(kind)); }
        Program.Check(Slide()["objects"]!.AsArray().Count == 4 && Object(2)["kind"]!.GetValue<string>() == "diamond", "New shapes are added");
        Program.Pump(view.AddObjectAsync("text")); Dispatcher.UIThread.RunJobs();
        Program.Click(view, "ObjectItalicButton"); Program.Pump(view.Pending); Program.Click(view, "ObjectUnderlineButton"); Program.Pump(view.Pending); Program.Click(view, "ObjectAlignCenterButton"); Program.Pump(view.Pending);
        var text = Object(4);
        Program.Check(text["italic"]!.GetValue<bool>() && text["underline"]!.GetValue<bool>() && text["align"]!.GetValue<string>() == "center", "Italic, underline and alignment");
        view.FindControl<NumericUpDown>("ObjectRotation")!.Value = 30; Program.Pump(view.Pending); view.FindControl<NumericUpDown>("ObjectOpacity")!.Value = 50; Program.Pump(view.Pending);
        view.FindControl<ComboBox>("ObjectLineColor")!.SelectedIndex = 3; Program.Pump(view.Pending); view.FindControl<NumericUpDown>("ObjectLineWidth")!.Value = 6; Program.Pump(view.Pending);
        text = Object(4);
        Program.Check(text["rotation"]!.GetValue<double>() == 30 && Math.Abs(text["opacity"]!.GetValue<double>() - 0.5) < 1e-9 && text["line"]!.GetValue<string>() == "#C00000" && text["line_width"]!.GetValue<double>() == 6, "Rotation, opacity and outline");
        view.FindControl<ComboBox>("SlideBackgroundBox")!.SelectedIndex = 5; Program.Pump(view.Pending); view.FindControl<ComboBox>("SlideTransitionBox")!.SelectedIndex = 1; Program.Pump(view.Pending);
        var notes = view.FindControl<TextBox>("SlideNotes")!; notes.Text = "Accueillir le public"; Program.Pump(view.FlushAsync());
        Program.Check(Slide()["background"]!.GetValue<string>() == "#1B2A41" && Slide()["transition"]!.GetValue<string>() == "fade" && Slide()["notes"]!.GetValue<string>() == "Accueillir le public" && view.Surface.Background == "#1B2A41", "Slide background, transition and notes");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(Slide()["notes"]!.GetValue<string>() == "" && Slide()["transition"]!.GetValue<string>() == "none", "Slide property undo");
        Program.Pump(doc.ExecuteAsync("Redo")); Program.Pump(doc.ExecuteAsync("Redo"));
        var show = new SlideshowWindow(doc.Snapshot["content"]!["slides"]!.AsArray(), 0); show.Show(); Dispatcher.UIThread.RunJobs(); show.ToggleNotes(); Program.Check(show.NotesVisible, "Slideshow notes toggle"); show.Close();
        var path = Path.Combine(Program.Artifacts, "slides-design.osslides"); Program.Pump(doc.SaveAsync(path)); using var reopened = Program.Get(DocumentSession.OpenAsync(engine, path)); Program.Check(System.Text.Json.Nodes.JsonNode.DeepEquals(doc.Snapshot["content"], reopened.Snapshot["content"]), "Slides design roundtrip");
        var before = Slide()["objects"]!.AsArray().Count; var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="); Program.Pump(view.AddImageAsync(png, "Pixel")); Dispatcher.UIThread.RunJobs();
        var picture = Object(before); Program.Check(Slide()["objects"]!.AsArray().Count == before + 1 && picture["kind"]!.GetValue<string>() == "image" && picture["image"]!["mime"]!.GetValue<string>() == "image/png" && picture["image"]!["alt"]!.GetValue<string>() == "Pixel" && picture["width"]!.GetValue<double>() > 100, "Image object is inserted from bytes");
        Program.Pump(doc.ExecuteAsync("ResizeObject", new { slide = Slide()["id"]!.GetValue<string>(), id = picture["id"]!.GetValue<string>(), width = 200.0, height = 100.0 })); Program.Check(Object(before)["image"] is not null && Object(before)["width"]!.GetValue<double>() == 200, "Resizing keeps the picture data");
        var picturePath = Path.Combine(Program.Artifacts, "slides-image.osslides"); Program.Pump(doc.SaveAsync(picturePath)); using var pictureReopened = Program.Get(DocumentSession.OpenAsync(engine, picturePath)); Program.Check(System.Text.Json.Nodes.JsonNode.DeepEquals(doc.Snapshot["content"], pictureReopened.Snapshot["content"]), "Image survives save and reopen");
        Program.Pump(doc.ExecuteAsync("Undo")); Program.Pump(doc.ExecuteAsync("Undo")); Program.Check(Slide()["objects"]!.AsArray().Count == before, "Undo removes the inserted image");
        Program.Capture(window, "slides-design"); view.Detach(); window.Close(); Console.WriteLine("PASS Slides shapes, text styling, rotation/opacity/outline, slide background/transition/notes");
    }
}
