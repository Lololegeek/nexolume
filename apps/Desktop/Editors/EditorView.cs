using Avalonia.Controls;
using OfficeBridge.Services;
namespace Nexolume.Desktop.Editors;

public abstract class EditorView(DocumentSession document) : UserControl
{
    public DocumentSession Document { get; } = document;
    public Task Pending { get; private set; } = Task.CompletedTask;
    public Exception? LastError { get; private set; }
    public event Action<Exception>? Error;
    public event Action? DraftChanged;
    public virtual bool HasDraft => false;
    public virtual Task FlushAsync() => Pending;
    protected void DraftUpdated() => DraftChanged?.Invoke();
    protected Task Enqueue(Func<Task> action) { Pending = Run(Pending, action); return Pending; }
    private async Task Run(Task previous, Func<Task> action) { try { await previous; LastError = null; await action(); } catch (Exception ex) { LastError = ex; Services.AppLog.Write(ex); Error?.Invoke(ex); } }
    public virtual void Detach() { }
}
