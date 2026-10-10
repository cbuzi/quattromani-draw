# Quattromani Draw

Nome prodotto: **Quattromani Draw**. Namespace, progetti ed eseguibile: **`Quattromani`** (senza "Draw").
Il nome viene dall'espressione italiana "a quattro mani": umano e LLM disegnano insieme.

Editor di grafica vettoriale open source, multipiattaforma (Windows, macOS, Linux), ispirato agli editor vettoriali professionali classici.
Caratteristica distintiva: **disegno "a quattro mani" tra umano e LLM**. Un agente esterno (Claude Code, Codex, OpenCode, Claude Desktop...) si collega all'app in esecuzione via MCP e disegna insieme all'utente sullo stesso documento.

## Stack

- .NET 10, C#
- Avalonia 12 (UI) + SkiaSharp (rendering)
- SDK C# ufficiale di MCP (`ModelContextProtocol`, `ModelContextProtocol.AspNetCore`) ospitato in Kestrel dentro l'app

## Struttura

```
Quattromani.Core   → document model, geometria, comandi, undo/redo, versioning. NESSUNA dipendenza da UI o MCP.
Quattromani.App    → Avalonia: shell, canvas Skia, strumenti, pannelli.
Quattromani.Mcp    → server MCP HTTP su localhost, ospitato nel processo dell'app.
```

Import/export SVG vive in Core finché è piccolo.

## Principi architetturali (non negoziabili)

1. **Tutto passa dal command bus.** La UI non modifica mai il documento direttamente: umano e LLM sono due client dello stesso bus.
2. **Ogni comando** è serializzabile in JSON, annullabile e porta un `author` (`human` | `llm:<nome>`).
3. **Batch = transazione**: un batch di comandi produce un solo step di undo.
4. **Versioning**: ogni comando applicato incrementa la versione del documento. Deve essere possibile ottenere le modifiche a partire da una versione N.
5. **ID stabili** per ogni oggetto, più un `name` semantico opzionale ("logo", "titolo"), utilizzabile negli indirizzamenti.
6. **Thread-safety**: i comandi da MCP arrivano da thread non-UI e vanno serializzati verso il dispatcher UI (o un lock sul documento).
7. Il **modello interno è un soprainsieme di SVG**. Le feature avanzate (Quattro-Clip, inviluppi, blend, contour) andranno modellate come oggetti veri; all'export SVG si appiattiscono.

## Document model (MVP)

- `Document` → pagine → layer → oggetti
- Oggetti: `Rectangle`, `Ellipse`, `Path` (bezier), `Group`; testo in seguito
- Stile: fill (colore solido, poi gradienti), stroke (colore, spessore)
- Trasformazione: matrice affine per oggetto

## API MCP (LLM-oriented)

Endpoint: `http://localhost:5577/mcp`
Registrazione in Claude Code: `claude mcp add --transport http quattromani http://localhost:5577/mcp`

Tool, pochi ma ricchi:

| Tool | Scopo |
|---|---|
| `get_document` | scene graph compatto in JSON (id, name, tipo, bounds, stile) |
| `get_selection` | oggetti attualmente selezionati dall'umano |
| `execute` | batch di comandi → un solo undo; restituisce id creati e nuova versione |
| `render` | PNG di documento o area, per verificare visivamente il risultato |
| `get_changes` | modifiche dalla versione N (in particolare quelle fatte dall'umano) |

Linee guida per i tool:
- Descrizioni ricche, con esempi JSON nella descrizione stessa.
- Oltre alle coordinate assolute, supportare **posizionamento relativo**: `rightOf`, `inside`, `alignTo`, `anchor`, `margin`.
- Errori esplicativi che dicano all'LLM come correggere la chiamata.
- Output compatti: niente dati inutili nel JSON.

Esempio di comando:
```json
{ "cmd": "shape.create", "type": "ellipse", "name": "sole",
  "rel": { "inside": "cielo", "anchor": "top-right", "margin": 20 },
  "fill": "#FFC800" }
```

## UI

- Deve essere **moderna e friendly**: stile Figma / Affinity e simili. **Mai** stile Inkscape/GTK.
- Toolbox verticale a sinistra, barra proprietà contestuale in alto, pannelli a destra (oggetti/layer, proprietà).
- Tema chiaro/scuro.
- Il lavoro dell'LLM deve essere visibile: evidenziazione degli oggetti creati o modificati dall'agente e indicatore di attività.

## Scope beta

Solo illustrazione: forme, path, selezione/trasformazione, fill/stroke, layer, undo/redo, salvataggio nel formato proprio (JSON) e import/export SVG.
Fuori scope per ora: CMYK, stampa, multipagina avanzato, import AI/CDR.

## Primo milestone

1. Document model + command bus + undo/redo + versioning
2. Canvas Avalonia con rendering Skia, pan/zoom, selezione e spostamento con il mouse
3. Server MCP con `get_document`, `execute` e `get_changes`

Criterio di successo: Claude Code crea un rettangolo via MCP, l'umano lo sposta con il mouse, e Claude Code vede lo spostamento con `get_changes`.

## Progetti di riferimento (solo ispirazione, non dipendenze)

- **Core2D** (C#/Avalonia, MIT): struttura model/editor/renderer, strumenti, pan/zoom, layer.
- **Penpot MCP** (MPL-2.0): come un editor espone il documento agli agenti via MCP.
- **Graphite** (Rust, Apache-2.0): editor a messaggi e document model.
- **Paper.js** (MIT): operazioni booleane su curve bezier, hit-testing, geometria dei path.
- **kurbo** (Rust, MIT/Apache): geometria bezier (bounds, lunghezze, intersezioni).
- **PixiEditor** (C#/Avalonia, LGPL-3.0): solo per capire come funziona un editor complesso su Avalonia, senza copiare codice.

## Convenzioni

- Privilegiare sempre la soluzione più semplice e breve. Niente astrazioni premature.
- **Non creare** file .md, progetti o classi di test, a meno che non venga chiesto esplicitamente.
- Nessuna dipendenza nuova senza un motivo concreto.
- **Non copiare codice GPL** (es. Inkscape, lib2geom). Si può leggere per capire gli algoritmi, poi si riscrive da zero. Codice MIT/Apache (es. Graphite) è utilizzabile come riferimento.