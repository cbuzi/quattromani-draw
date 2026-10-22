namespace Quattromani.Mcp;

/// <summary>Current selection of the app (not part of the document: it does not enter undo). Implemented by the app, which handles it on the UI thread.</summary>
public interface ISelectionService
{
    IReadOnlyList<string> Get();
    void Set(IReadOnlyList<string> ids);
}
