using Avalonia.Threading;
using Quattromani.Mcp;

namespace Quattromani.App;

/// <summary>Selezione del canvas esposta all'MCP: le richieste arrivano da thread non-UI e passano dal dispatcher.</summary>
sealed class CanvasSelection(CanvasView canvas) : ISelectionService
{
    public IReadOnlyList<string> Get() => Dispatcher.UIThread.Invoke(() => canvas.SelectedIds.ToList());
    public void Set(IReadOnlyList<string> ids) => Dispatcher.UIThread.Invoke(() => canvas.SetSelection(ids));
}
