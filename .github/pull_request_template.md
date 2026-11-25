<!-- Anything larger than a small fix needs an agreed issue first: see CONTRIBUTING.md. -->

## What this changes, and why

<!-- Link the issue, for example "Closes #123". -->

## How it was tested

<!-- The Windows version, and the Jellyfin version: a test server, never someone else's.
     For a change you can see: the themes you checked it in, and dark or light.
     For a change under backend\: the off-screen backend suite's result. -->

## Checklist

- [ ] `dotnet test ui\FluentFin.Core.Tests` passes
- [ ] `dotnet test ui\FluentFin.Tests -p:Platform=x64` passes
- [ ] For a change under `backend\`: the off-screen backend suite passes (`tools\README.md`)
- [ ] Reached the change with the keyboard as well as the mouse
- [ ] Checked against the design rules in `docs/DESIGN-RULES.md`
- [ ] A change to the messages between the UI and the backend updates `docs/BRIDGE.md` and both sides
- [ ] No home-folder path, PC name, server address, user name or token in the code, tests, docs, screenshots or logs
