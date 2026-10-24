# Security

## Reporting a vulnerability

Please **do not open a public issue** for a security problem. Use GitHub's private reporting instead: the **Security** tab of the repository → **Report a vulnerability**.

Include what you found, how to reproduce it and which version you tested. This is a one-person project, so replies may take a few days; confirmed issues are fixed as soon as possible and credited in the release notes unless you prefer otherwise.

Only the latest release is supported.

## The MCP server: what it exposes

Quattromani Draw runs an MCP server inside the app so that an AI agent can draw with you. Know what that means:

- It listens on **`localhost` only** (default port 5577, `QUATTROMANI_PORT` changes it). It is not reachable from other machines.
- Requests whose `Host` or `Origin` header is not a loopback address are rejected with 403, so a web page open in your browser cannot send commands to the app (cross-origin requests, DNS rebinding).
- There is **no authentication**. Any program running as you on the same computer can connect and drive the app. That is by design: it is how Claude Code, Codex and other agents attach with a single command.
- A connected agent can do what the tools allow: edit the open document, read and write files at absolute paths you give it (`open_document`, `save_document`, exports, images and SVG import).

Practical advice:

- Only connect agents you trust, and keep the port closed to the network (don't forward it, don't bind it to other interfaces).
- Don't run the app on a shared multi-user machine where other users' programs could reach `localhost`.
- If you need a different port, set `QUATTROMANI_PORT` before starting the app.

## Out of scope

Reports that require an attacker who can already run code as your user on your machine, or that rely on you connecting an untrusted agent, are expected behavior of the design above and not treated as vulnerabilities. Malformed documents or SVG files that crash or hang the app are welcome as ordinary bug reports.
