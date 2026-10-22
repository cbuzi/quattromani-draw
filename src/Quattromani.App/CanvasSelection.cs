using Avalonia.Threading;
using Quattromani.Mcp;

namespace Quattromani.App;

/// <summary>Canvas selection exposed to MCP: requests arrive from non-UI threads and go through the dispatcher.</summary>
sealed class CanvasSelection(CanvasView canvas) : ISelectionService
{
    public IReadOnlyList<string> Get() => Dispatcher.UIThread.Invoke(() => canvas.SelectedIds.ToList());
    public void Set(IReadOnlyList<string> ids) => Dispatcher.UIThread.Invoke(() => canvas.SetSelection(ids));
}
