namespace Quattromani.Mcp;

/// <summary>Selezione corrente dell'app (non fa parte del documento: non entra nell'undo). La implementa l'app, che la gestisce sul thread UI.</summary>
public interface ISelectionService
{
    IReadOnlyList<string> Get();
    void Set(IReadOnlyList<string> ids);
}
