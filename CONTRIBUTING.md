# Contributing to Muralis

Thanks for your interest in improving Muralis! This document explains how to get
started, and what we expect from contributions.

## Getting started

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Fork and clone the repository.
3. Build and run:

   ```bash
   dotnet build Muralis.slnx
   dotnet run --project src/Muralis.App
   ```

4. Run the tests before opening a pull request:

   ```bash
   dotnet test tests/Muralis.Core.Tests
   ```

Visual Studio 2026 is optional — everything works with the `dotnet` CLI.
Developer Mode is not required: Muralis is distributed as an unpackaged app.

## Project layout

| Project | Purpose |
| --- | --- |
| `src/Muralis.App` | WinUI 3 executable — views, view models, controls, Windows-specific services |
| `src/Muralis.Core` | Platform-agnostic domain logic — models, services, providers, repositories |
| `tests/Muralis.Core.Tests` | Unit tests for `Muralis.Core` |

Rules of thumb:

- Business logic belongs in `Muralis.Core` and must stay free of UI and Windows
  interop so it remains testable.
- Windows-only interop lives in `Muralis.App/Services/Platform`.
- View models must not call P/Invoke directly and must not block the UI thread.

## Coding guidelines

- Nullable reference types are enabled — no `!` suppressions without a comment.
- Use `async`/`await` end-to-end and always flow `CancellationToken`s.
- No silent `catch (Exception) { }` — log the error, and surface it to the user
  when it is actionable.
- Follow the style enforced by `.editorconfig`.
- Keep methods focused; prefer clarity over cleverness.

## Commit & pull request conventions

- Use [Conventional Commits](https://www.conventionalcommits.org/) style prefixes:
  `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`.
- Keep pull requests small and focused. Describe **what** changed and **why**,
  and include screenshots for UI changes.
- Reference related issues (`Closes #123`).

## Reporting bugs

Open an issue using the bug report template. Include your Windows version, the
Muralis version, steps to reproduce, expected vs. actual behavior, and relevant
log excerpts from `%LOCALAPPDATA%\Muralis\logs`.
