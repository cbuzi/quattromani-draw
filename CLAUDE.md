# Quattromani Draw

Product name: **Quattromani Draw**. Namespace, projects and executable: **`Quattromani`** (without "Draw").
The name comes from the Italian expression "a quattro mani" (four hands, as in a piano duet): human and LLM draw together.

Open-source, cross-platform (Windows, macOS, Linux) vector graphics editor, inspired by the classic professional vector editors.
Distinctive feature: **"four-handed" drawing between human and LLM**. An external agent (Claude Code, Codex, OpenCode, Claude Desktop...) connects to the running app via MCP and draws with the user on the same document.

## Stack

- .NET 10, C#
- Avalonia 12 (UI) + SkiaSharp (rendering)
- Official MCP C# SDK (`ModelContextProtocol`, `ModelContextProtocol.AspNetCore`) hosted in Kestrel inside the app

## Structure

```
Quattromani.Core   → document model, geometry, commands, undo/redo, versioning, SVG. NO dependency on UI or MCP.
Quattromani.App    → Avalonia: shell, Skia canvas, tools, panels.
Quattromani.Mcp    → MCP server over HTTP on localhost, hosted inside the app process.
tests/Quattromani.Core.Tests → xUnit tests of the Core.
```

SVG import/export lives in Core as long as it stays small.

## Architectural principles (non-negotiable)

1. **Everything goes through the command bus.** The UI never modifies the document directly: human and LLM are two clients of the same bus.
2. **Every command** is JSON-serializable, undoable, and carries an `author` (`human` | `llm:<name>`).
3. **Batch = transaction**: a batch of commands produces a single undo step.
4. **Versioning**: every applied command increments the document version. It must be possible to get the changes since version N.
5. **Stable IDs** for every object, plus an optional semantic `name` ("logo", "title") usable for addressing.
6. **Thread safety**: MCP commands arrive from non-UI threads and must be serialized towards the UI dispatcher (or a lock on the document).
7. The **internal model is a superset of SVG**. Advanced features (Quattro-Clip, envelopes, blends, contours) will be modeled as real objects; they are flattened on SVG export.

## Document model

- `Document` → pages → layers → objects
- Objects: `Rectangle`, `Ellipse`, `Path` (bezier), `Group`, `Text`, `Image`
- Style: fill (solid color, gradients, patterns), stroke (color, width, dash, cap, join), opacity
- Transform: affine matrix per object

## MCP API (LLM-oriented)

Endpoint: `http://localhost:5577/mcp` (`QUATTROMANI_PORT` changes the port)
Registration in Claude Code: `claude mcp add --transport http quattromani http://localhost:5577/mcp`

Tools, few but rich:

| Tool | Purpose |
|---|---|
| `get_document` | compact JSON scene graph (id, name, type, bounds, style, layers) |
| `get_selection` / `select` | objects currently selected by the human / set the selection |
| `execute` | batch of commands → a single undo; returns created ids and the new version |
| `render` | PNG of the document or an area, to check the result visually |
| `get_changes` | changes since version N (in particular those made by the human) |
| `save_document` / `open_document` / `new_document` | documents on disk |
| `export_svg` / `export_pdf` / `export_png` | export |
| `list_fonts` | fonts available for text |

Guidelines for the tools:
- Rich descriptions, with JSON examples in the description itself.
- Besides absolute coordinates, support **relative positioning**: `rightOf`, `inside`, `alignTo`, `anchor`, `margin`.
- Explanatory errors that tell the LLM how to fix the call.
- Compact output: no useless data in the JSON.

Example command:
```json
{ "cmd": "shape.create", "type": "ellipse", "name": "sun",
  "rel": { "inside": "sky", "anchor": "top-right", "margin": 20 },
  "fill": "#FFC800" }
```

## UI

- Must be **modern and friendly**: Figma / Affinity style and the like. **Never** Inkscape/GTK style.
- Vertical toolbox on the left, contextual properties bar at the top, panels on the right (objects/layers, properties).
- Light/dark theme.
- The LLM's work must be visible: highlighting of the objects the agent created or modified, and an activity indicator.

## Beta scope

Illustration only: shapes, paths, selection/transformation, fill/stroke, layers, undo/redo, saving in the native format (JSON) and SVG import/export.
Out of scope for now: CMYK, print, advanced multi-page, AI/CDR import.

## Reference projects (inspiration only, not dependencies)

- **Core2D** (C#/Avalonia, MIT): model/editor/renderer structure, tools, pan/zoom, layers.
- **Penpot MCP** (MPL-2.0): how an editor exposes the document to agents via MCP.
- **Graphite** (Rust, Apache-2.0): message-based editor and document model.
- **Paper.js** (MIT): boolean operations on bezier curves, hit-testing, path geometry.
- **kurbo** (Rust, MIT/Apache): bezier geometry (bounds, lengths, intersections).
- **PixiEditor** (C#/Avalonia, LGPL-3.0): only to understand how a complex editor works on Avalonia, without copying code.

## Conventions

- **Everything is in English**: code, comments, error messages, documentation and **commit messages**.
- Always prefer the simplest and shortest solution. No premature abstractions.
- **Do not create** .md files or new projects unless explicitly asked.
- The Core's automated tests live in `tests/Quattromani.Core.Tests` (xUnit): when the Core's behavior changes, add or update tests there, and `dotnet test` must pass.
- No new dependency without a concrete reason.
- **Do not copy GPL code** (e.g. Inkscape, lib2geom). You can read it to understand the algorithms, then rewrite from scratch. MIT/Apache code (e.g. Graphite) can be used as a reference.
- Do not use other companies' trademarks as names of our features (the shape clipper is "Quattro-Clip").
