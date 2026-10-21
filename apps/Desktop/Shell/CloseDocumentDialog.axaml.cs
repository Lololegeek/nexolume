using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace Nexolume.Desktop.Shell;

public partial class CloseDocumentDialog : Window
{
    public CloseDocumentDialog(string title) { AvaloniaXamlLoader.Load(this); this.FindControl<TextBlock>("DocumentName")!.Text = title; this.FindControl<Button>("DiscardButton")!.Click += (_, _) => Close("discard"); this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close("cancel"); this.FindControl<Button>("ConfirmSaveButton")!.Click += (_, _) => Close("save"); }
}
