using Avalonia.Controls;
using System.Text.Json.Nodes;

namespace Nexolume.Desktop.Editors.Writer.Views;

public partial class WriterView
{
    private async Task ClearFormattingAsync()
    {
        if (multi is not null) { await ClearMultiFormattingAsync(); return; }
        if (active is null) return; var id=active;var box=boxes[id];var start=Math.Min(box.SelectionStart,box.SelectionEnd);var end=Math.Max(box.SelectionStart,box.SelectionEnd);
        await FlushAsync();group=Guid.NewGuid().ToString();
        if (start!=end) await Enqueue(()=>Document.ExecuteAsync("FormatText",new {id,start,end,style=new {bold=false,italic=false,underline=false,strike=false,superscript=false,subscript=false,small_caps=false,font_family="Aptos",font_size=16,color="#111111",highlight="none"}}));
        else await Enqueue(()=>Document.ExecuteAsync("SetParagraph",new {id,style=new {bold=false,italic=false,underline=false,strike=false,superscript=false,subscript=false,small_caps=false},runs=new JsonArray()}));
        box.Focus();box.CaretIndex=end;box.SelectionStart=start;box.SelectionEnd=end;
    }
}
