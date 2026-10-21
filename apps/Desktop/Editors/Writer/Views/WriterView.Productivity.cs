using Avalonia;

using Avalonia.Controls;

using Avalonia.Input;

using Avalonia.Layout;

using Avalonia.Media;

using System.Text;



namespace Nexolume.Desktop.Editors.Writer.Views;



public partial class WriterView

{

    public bool IsFocusMode { get; private set; }

    private bool ribbonBeforeFocus;

    private bool navigationBeforeFocus, searchBeforeFocus;
    private bool spellingBeforeFocus;

    private readonly TextBlock statistics = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };



    private void SetupProductivity()

    {

        this.FindControl<Button>("NavigationButton")!.Click += (_, _) =>

        {

            var panel = this.FindControl<Border>("NavigationPanel")!;

            panel.IsVisible = !panel.IsVisible;

            if (panel.IsVisible) { this.FindControl<Border>("SearchPanel")!.IsVisible = false; this.FindControl<Border>("SpellingPanel")!.IsVisible = false; }

            RefreshProductivity();

        };

        this.FindControl<Button>("CloseNavigationButton")!.Click += (_, _) => this.FindControl<Border>("NavigationPanel")!.IsVisible = false;

        this.FindControl<Button>("FocusModeButton")!.Click += (_, _) => SetFocusMode(true);

        this.FindControl<Button>("FocusExitButton")!.Click += (_, _) => SetFocusMode(false);

        this.FindControl<Button>("StatusFitButton")!.Click += (_, _) => this.FindControl<Button>("FitPageButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        this.FindControl<Button>("ResetZoomButton")!.Click += (_, _) => this.FindControl<Slider>("ZoomSlider")!.Value = 100;

        this.FindControl<Slider>("ZoomSlider")!.PropertyChanged += (_, e) =>

        {

            if (e.Property == Slider.ValueProperty) this.FindControl<Button>("ResetZoomButton")!.Content = $"{this.FindControl<Slider>("ZoomSlider")!.Value:0} %";

        };

        this.FindControl<Button>("WordStatsButton")!.Flyout = new Flyout

        {

            Content = new StackPanel { Spacing = 12, Margin = new Thickness(8), Children =

            {

                new TextBlock { Text = "Statistiques du document", FontSize = 17, FontWeight = FontWeight.SemiBold }, statistics

            }}

        };

        AddHandler(KeyDownEvent, (_, e) =>

        {

            if (e.Key == Key.Escape && IsFocusMode) { SetFocusMode(false); e.Handled = true; }

        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

    }



    private void SetFocusMode(bool value)

    {

        if (IsFocusMode == value) return;

        var ribbon = this.FindControl<Border>("RibbonBorder")!;

        if (value) { ribbonBeforeFocus = ribbon.IsVisible; navigationBeforeFocus = this.FindControl<Border>("NavigationPanel")!.IsVisible; searchBeforeFocus = this.FindControl<Border>("SearchPanel")!.IsVisible; }

        IsFocusMode = value;
        if (value) { spellingBeforeFocus = this.FindControl<Border>("SpellingPanel")!.IsVisible; this.FindControl<Border>("SpellingPanel")!.IsVisible = false; }
        else this.FindControl<Border>("SpellingPanel")!.IsVisible = spellingBeforeFocus;

        this.FindControl<Grid>("WriterTabs")!.IsVisible = !value;

        ribbon.IsVisible = !value && ribbonBeforeFocus;

        this.FindControl<Button>("FocusExitButton")!.IsVisible = value;

        if (value)

        {

            this.FindControl<Border>("SearchPanel")!.IsVisible = false;

            this.FindControl<Border>("NavigationPanel")!.IsVisible = false;

            if (active is not null) boxes[active].Focus();

        }

        else { this.FindControl<Border>("NavigationPanel")!.IsVisible = navigationBeforeFocus; this.FindControl<Border>("SearchPanel")!.IsVisible = searchBeforeFocus; }

    }



    private void RefreshProductivity()

    {

        var paragraphs = Document.Snapshot["content"]!["paragraphs"]!.AsArray();

        var text = string.Join("\n", paragraphs.Select(p => p!["text"]!.GetValue<string>()));

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        statistics.Text = $"Mots : {words:N0}\nCaractères avec espaces : {text.EnumerateRunes().Count():N0}\nCaractères sans espaces : {text.EnumerateRunes().Count(r => !Rune.IsWhiteSpace(r)):N0}\nParagraphes : {paragraphs.Count:N0}";

        var list = this.FindControl<StackPanel>("HeadingList")!;

        list.Children.Clear();

        foreach (var p in paragraphs.Where(p => p!["style"]!["font_size"]!.GetValue<double>() >= 22 && !string.IsNullOrWhiteSpace(p["text"]!.GetValue<string>())))

        {

            var id = p!["id"]!.GetValue<string>();

            var heading = new Button

            {

                Content = new TextBlock { Text = p["text"]!.GetValue<string>(), TextTrimming = TextTrimming.CharacterEllipsis },

                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,

                Padding = new Thickness(p["style"]!["font_size"]!.GetValue<double>() < 28 ? 20 : 8, 10, 8, 10),

                Classes = { "quiet" }

            };

            ToolTip.SetTip(heading, p["text"]!.GetValue<string>());

            heading.Click += (_, _) => { active = id; boxes[id].Focus(); boxes[id].CaretIndex = 0; boxes[id].BringIntoView(); UpdateStyleButtons(); };

            list.Children.Add(heading);

        }

        if (list.Children.Count == 0) list.Children.Add(new TextBlock

        {

            Text = "Appliquez « Titre 1 » ou « Titre 2 » à un paragraphe pour structurer votre document.",

            TextWrapping = TextWrapping.Wrap, Classes = { "caption" }, Margin = new Thickness(0, 8)

        });

    }



    private void SetupParagraphMenu(string id, TextBox box)

    {

        var menu = new ContextMenu();

        void Item(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }

        Item("Orthographe…", async () => await ShowSpellingAsync());

        Item("Couper", () => { if (!TryMultiClipboard("cut")) box.Cut(); }); Item("Copier", () => { if (!TryMultiClipboard("copy")) box.Copy(); }); Item("Coller", () => { if (!TryMultiClipboard("paste")) box.Paste(); });

        menu.Items.Add(new Separator());

        foreach (var (label, direction) in new[] { ("Dupliquer le paragraphe", 0), ("Monter le paragraphe", -1), ("Descendre le paragraphe", 1) })

        {

            var item = new MenuItem { Header = label };

            item.Click += async (_, _) => await OrganizeParagraphAsync(id, direction);

            menu.Items.Add(item);

        }

        box.ContextMenu = menu;

    }



    public async Task OrganizeParagraphAsync(string id, int direction)

    {

        await FlushAsync();

        var ps = Document.Snapshot["content"]!["paragraphs"]!.AsArray();

        var index = ps.Select((p, i) => (p, i)).First(x => x.p!["id"]!.GetValue<string>() == id).i;

        var paragraph = ps[index]!;

        var text = paragraph["text"]!.GetValue<string>(); var style = paragraph["style"]!.DeepClone(); var runs = paragraph["runs"]?.DeepClone(); var image = paragraph["image"]?.DeepClone(); var table = paragraph["table"]?.DeepClone();

        if (direction == 0)

        {

            var copy = Guid.NewGuid().ToString();

            await Enqueue(() => Document.ExecuteAsync("InsertParagraph", new { id = copy, index = index + 1, text, style, runs = runs ?? new System.Text.Json.Nodes.JsonArray(), image, table }));

            if (LastError is null) active = copy;

        }

        else if (index + direction >= 0 && index + direction < ps.Count)

        {

            await Enqueue(() => Document.ExecuteAsync("Batch", new { commands = new object[]

            {

                new { command = "DeleteParagraph", @params = new { id } },

                new { command = "InsertParagraph", @params = new { id, index = index + direction, text, style, runs = runs ?? new System.Text.Json.Nodes.JsonArray(), image, table } }

            }}));

            if (LastError is null) active = id;

        }

        if (active is not null && boxes.TryGetValue(active, out var selected)) { selected.Focus(); selected.BringIntoView(); }

    }

}

