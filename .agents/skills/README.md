# Muralis project skills

Project-local agent skills for the Muralis repository. They live here, in the repo, so that any
compatible agent working on Muralis gets the project's engineering rules without them being
installed into a user profile.

Discovery is automatic: a harness that reads `<repo>/.agents/skills` finds each
`<skill-name>/SKILL.md`, takes `name` and `description` from its YAML frontmatter, and loads the
body on demand. This README is for humans — nothing here is needed for the loader to work.

There are two sets. **`muralis-*`** are original, written from this repository's own code, tests and
docs. **`community-*`** are adapted from open-source skill repositories — every one is credited and
its adaptations described in [`THIRD_PARTY.md`](THIRD_PARTY.md).

## The fifteen skills

### Product and engineering — original to this repository

| Skill | Owns |
| --- | --- |
| `muralis-architecture` | Product boundary between Windows/Explorer and Muralis; the two Desktop Experience modes; project layering; ownership and fail-open |
| `muralis-motion-engine` | Anything that follows the pointer: magnification, pointer input and its broker, transform ownership, drag |
| `muralis-build-verification` | The build/test gate, the toolchain's machine-specific traps, what counts as a built artifact |
| `muralis-windows-shell` | Win32/COM, Explorer, the desktop layer, HWNDs, DPI, monitors, shell interop and its ownership table |
| `muralis-verification` | What "done" means; the difference between automated, runtime and human proof |
| `muralis-design-system` | Tokens, theming, materials, reference controls, typography, forbidden visual styling |
| `muralis-performance` | Hot paths, measurement method, percentiles, caches, pacing |
| `muralis-code-review` | Adversarial review of a finished change, including the failure modes this codebase actually has |

### Visual and methodology — adapted from open source

| Skill | Owns | Adapted from |
| --- | --- | --- |
| `community-frontend-design` | Designing a surface before building it: focal point, hierarchy, composition, density, restraint | `block/agent-skills` (Apache-2.0) |
| `community-design-language` | Extracting the visual language into `docs/DESIGN.md` — documents, never invents | original, method from the sources below |
| `community-interface-review` | Critiquing an implemented interface: hierarchy, spacing, typography, contrast, consistency, noise | `ColourCloudSky/review-ui-design-skill` (MIT) |
| `community-screenshot-critique` | The fresh-eyes workflow: launch, capture, review screenshot-first, revise, re-review | original workflow, epistemics from the sources below |
| `community-systematic-debugging` | Root cause before any fix; the four phases; stop after three failed fixes | `obra/superpowers` (MIT) |
| `community-verification-before-completion` | Evidence before every completion claim; the gate function | `obra/superpowers` (MIT) |
| `community-subagent-driven-development` | Executing a plan through fresh subagents with review after each task | `obra/superpowers` (MIT) |

## Cross-skill routing

Domain skills do not carry the verification rules, because those apply to every change. Load them
on top:

- **Any change to source** → the domain skill(s) **+ `muralis-build-verification`** (to build and
  test it) **+ `muralis-verification`** (before claiming it works).
- **Any complex or multi-file change** → also **`muralis-code-review`** before calling it done.
- **Anything pointer-driven or hot-path** → add **`muralis-performance`**.
- **Anything visual** (a page, card, hero, Dock, theme, material) → add
  **`community-frontend-design`** before building and **`community-interface-review`** after.
- **Any UI you must actually judge** → **`community-screenshot-critique`**, because the implementer is
  not an acceptable visual judge.
- **Any bug, crash or unexpected behaviour** → **`community-systematic-debugging`** before proposing a
  fix.
- **Any completion claim** → **`community-verification-before-completion`**.
- **A plan with several independent tasks** → **`community-subagent-driven-development`**.

Worked examples:

| Task | Skills |
| --- | --- |
| Redesign the Home hero so it reads as a flagship, not a settings card | `muralis-design-system`, `community-frontend-design`, `community-interface-review`, `muralis-verification` |
| "Look at this Home screenshot and tell me what's wrong with it" | `community-screenshot-critique`, `community-interface-review`, `muralis-design-system` |
| Dock text colour is wrong in dark mode after scrolling | `community-systematic-debugging`, `muralis-design-system`, `muralis-verification`, `muralis-build-verification` |
| Implement Nexus neighbour translation | `muralis-motion-engine`, `muralis-performance`, `community-verification-before-completion`, `community-subagent-driven-development`, `muralis-build-verification` — and deliberately **not** `community-frontend-design` |
| Change one Chinese translation string | `muralis-build-verification`, `muralis-verification` — deliberately **not** the design, motion, shell, debugging or subagent skills |

Do not load every skill for every task. A localisation or copy edit has no business pulling in the
motion engine, the shell ownership rules, or a design review.

## Adding a skill

Add one when there is a **recurring** class of task with rules that are (a) stable over months,
(b) not obvious from reading the code, and (c) currently relearned by every agent that touches it.
Prefer widening an existing skill over adding a sixteenth; the pack is deliberately small.

Requirements for a new skill:

- Directory named in kebab-case, containing `SKILL.md`.
- YAML frontmatter with `name` (matching the directory, `^[a-z0-9]+(-[a-z0-9]+)*$`) and a
  `description`. The description is what the agent routes on, so state the trigger conditions in it,
  not a summary of the topic.
- A file that parses cleanly. A skill with malformed frontmatter is **silently ignored** with only
  a log warning — no error, no entry in the catalog.
- The same section shape as the others: When to use / Core rules / Forbidden patterns / Relevant
  architecture / Required verification / Stop-escalation.
- Short, dense, executable. Reference other skills instead of copying them.

Cross-reference, never duplicate: if two skills need the same rule, one owns it and the other links
to it.

For a skill adapted from an external repository, follow the checklist at the end of
[`THIRD_PARTY.md`](THIRD_PARTY.md) — check the license first, pin the commit, import text only, and
record the adaptation before the skill lands.

## What does not belong in a skill

A skill is for knowledge that stays true. It must not contain:

- **Phase or roadmap status.** "We are on Stage B", "Stage C is next", "this is not done yet" — those
  belong in `docs/`, and they go stale.
- **Counts.** Test counts, token counts, file counts. The suite grows; a number in a skill becomes a
  false statement. Write "all existing tests must pass".
- **Current blockers or known defects.** A live bug is a task, not a rule. If the *pattern* that
  produced it is recurring, encode the pattern.
- **One-off debugging details.** A specific exception, commit, or session's findings.
- **Temporary workarounds.** If it is temporary it will outlive its removal.
- **Undecided future design.** Do not encode a plan as if it were a rule.
- **Invented abstractions.** Only name types, files and APIs that exist. Verify before writing; a
  skill that cites a class that was renamed is worse than no skill.

When repo, docs and skills disagree, the authority order is:

1. the code and its architecture tests (executable truth),
2. these skills,
3. the latest section of `docs/architecture-v2.md`,
4. earlier sections of that document (it is an append-only landing record, so it contains
   superseded mode counts and stale test counts),
5. `docs/design-foundation.md`, `docs/PHASE4D-NEXUS-MOTION.md`, `CONTRIBUTING.md`, `README.md` —
   useful, but each has known stale claims.

If a skill is found to contradict the code, fix the skill or report the contradiction — do not
"fix" the guard test to match the skill.
