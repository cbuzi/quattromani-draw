# Contributing to Quattromani Draw

Thank you for considering a contribution! Bug reports, ideas and pull requests are all welcome.

## Build and test

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build Quattromani.sln
dotnet test                                    # the Core's tests
dotnet run --project src/Quattromani.App       # start the app (MCP server on http://localhost:5577/mcp)
```

To try a release package locally: `scripts/package.sh osx-arm64` (or `osx-x64`, `win-x64`, `linux-x64`); the result lands in `dist/`.

## Ground rules

- **Everything goes through the command bus.** The UI never edits the document directly: a human and an agent are two clients of the same bus. A new capability is a new command (JSON-serializable, undoable, with an author), usable from the UI and from MCP.
- **`Quattromani.Core` depends on neither the UI nor MCP.** Anything that needs Skia or Avalonia stays in `Quattromani.App` and reaches the Core through small interfaces.
- **Error messages are written for two readers**: the human and the LLM. Say what is wrong and how to fix it, with an example when it helps.
- **Add or update a test** in `tests/Quattromani.Core.Tests` when you change the Core's behavior.
- Keep it simple: no premature abstractions, no new dependency without a concrete reason.
- **Do not copy GPL code from other projects** (Inkscape, lib2geom…) except as allowed by this project's own license. Reading it to understand an algorithm is fine; then rewrite it from scratch. MIT / Apache code (for example Graphite) is fine as a reference.
- Code, comments and messages are in English.

## Licensing of contributions

Quattromani Draw is released under the [GPL-3.0](LICENSE). The maintainer may also offer the project under other terms (for example a commercial license for partners who cannot use the GPL). For that to be possible, contributions need a simple agreement. **By submitting a contribution (a pull request, a patch…) you confirm that:**

1. you wrote it, or you otherwise have the right to submit it;
2. it is licensed to everyone under the GPL-3.0, together with the rest of the project;
3. you grant the maintainer a perpetual, worldwide, non-exclusive, royalty-free, irrevocable license to use, modify, sublicense and relicense your contribution under any terms, including proprietary ones.

You keep the copyright on your contribution.

> This agreement is a plain-language draft and has not been reviewed by a lawyer. Large contributions may be asked to sign a fuller contributor license agreement.
