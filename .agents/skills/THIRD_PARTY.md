# Third-party skills and attribution

This directory contains two kinds of skill:

- **`muralis-*`** — original skills written for this repository from its own code, tests and docs. Not
  third-party; no external attribution applies.
- **`community-*`** — adapted from open-source skill repositories. Every one is listed below, with
  its source, license and the specific adaptations made.

Nothing here has been copied wholesale. Each community skill was reduced to its methodology, had its
framework and agent assumptions removed, and was rewritten against Muralis's own architecture. The
adaptation summaries below state exactly what was kept, what was changed, and what was dropped.

No third-party scripts, binaries, hooks or plugins were installed or executed. Only `SKILL.md` text
was used as source material.

---

## Sources

### 1. obra/superpowers

- **Repository:** https://github.com/obra/superpowers
- **Author:** Jesse Vincent (`obra`)
- **License:** MIT — `Copyright (c) 2025 Jesse Vincent`. Verified from the repository's `LICENSE`
  file directly.
- **Source commit:** `b36e0829c6d0140e93cfef2ca599b1b07d4a7797`
- **Used by:** `community-systematic-debugging`, `community-verification-before-completion`,
  `community-subagent-driven-development`

The repository was **not** imported as a whole. It ships 14 skills plus a plugin/marketplace layer
and shell hooks; none of that machinery was taken. Only three `SKILL.md` files were used as source
material.

Adaptations common to all three:

- Cross-references to `superpowers:*` skills (e.g. `superpowers:test-driven-development`,
  `superpowers:finishing-a-development-branch`) were rewritten to point at this project's own
  skills, which are the ones that actually exist here.
- References to companion files that were not imported (`root-cause-tracing.md`,
  `defense-in-depth.md`, `condition-based-waiting.md`, and the `*-prompt.md` files) were replaced
  with equivalent self-contained instructions.
- The Claude-Code-specific orchestration (the `Task` tool, ledger files, model-selection policy,
  worktree requirements, `dotnet`/`Bash`-oriented examples) was removed and replaced with the
  subagent facilities this harness actually provides.

#### community-systematic-debugging

- **Source:** `skills/systematic-debugging/SKILL.md`
- **Kept:** the iron law (no fix before root-cause investigation); the four ordered phases; the
  boundary-instrumentation technique for multi-layer systems; backward tracing of a bad value to its
  origin; the counter-rationalisation table; the "three failed fixes means question the architecture"
  rule and its diagnostic signals.
- **Changed for Muralis:** added a "where the bug usually is not" section naming this project's real
  traps — stale incremental builds, composition transform ownership, coordinate-space mismatches,
  theme resolution across windows, thread affinity, environment-vs-product, and measurement
  reliability.
- **Removed:** the shell/CI worked example (a signing-pipeline walkthrough), the `Bash`/`grep`
  specifics, and the cross-skill pointers.

#### community-verification-before-completion

- **Source:** `skills/verification-before-completion/SKILL.md`
- **Kept:** the iron law (no completion claim without fresh verification evidence); the five-step
  gate function; the claim-to-evidence table; the red flags; the rationalization-prevention table.
- **Changed for Muralis:** the evidence ladder and the four false equalities were **not** duplicated —
  they live in `muralis-verification` and `muralis-build-verification`, and this skill cross-references
  them instead. The table's rows were retargeted at this project's commands and artifacts.
- **Removed:** the VCS/commit/PR-centric framing that assumed a particular git workflow.

#### community-subagent-driven-development

- **Source:** `skills/subagent-driven-development/SKILL.md` (32 KB in the original)
- **Kept:** fresh context per task; a written brief per task rather than inherited context; review
  after each task separating spec compliance from code quality; independent review; never trusting a
  subagent's success report; bounded fix loops; "rulings, not stalls"; integrate before the final
  review.
- **Changed for Muralis:** the four stop conditions were extended with this project's own — an
  architecture-level decision, and **stage/phase boundaries, which a human owns**. The visual-review
  slot was wired to `community-screenshot-critique` and its fresh-eyes, screenshot-first rule.
- **Removed:** the entire Go-style orchestration layer — the ledger, the dispatch prompt files, the
  model-selection matrix, git-worktree setup, and the DOT flow diagram. None of those files were
  imported, so no instruction in the adapted skill depends on them.

### 2. block/agent-skills

- **Repository:** https://github.com/block/agent-skills
- **Author:** Block, Inc. (`block`), published as an open-source agent-skill marketplace
- **License:** Apache-2.0 — verified from the repository metadata and its `LICENSE` file. Note that
  Apache-2.0 requires attribution and a statement of changes; both are provided here.
- **Source commit:** `329a55d1500748a0a48d64629757cbe2622e34bf`
- **Used by:** `community-frontend-design`

#### community-frontend-design

- **Source:** `frontend-design/SKILL.md`
- **Kept:** the design-thinking pass performed *before* implementation — purpose, audience,
  constraints, differentiation; the demand for a deliberate focal point and a stated direction; the
  emphasis on intentionality over intensity; matching implementation complexity to the design vision;
  and the catalogue of generic AI-interface failure modes.
- **Changed for Muralis — and this is an inversion, not a trim.** The source's central instruction is
  to "pick an extreme" aesthetic, to make "unexpected choices", to avoid common fonts and to vary the
  design between themes. That is the *opposite* of correct here: Muralis has an established,
  token-enforced visual identity (premium, calm, cinematic, layered, restrained) that an agent must
  execute rather than re-invent. The adapted skill therefore instructs the agent to design **within**
  the locked design system, and explicitly forbids inventing colours, fonts or spacing. The design
  *method* was kept; the licence to invent was removed.
- **Removed as framework-bound (web/React/CSS):** the "use CSS variables" instruction (→ semantic
  tokens and `ThemeResource`), CSS-only motion and the Motion library for React (→ WinUI composition
  and the project's shared durations), browser breakpoints (→ window sizes), grid-breaking/asymmetric
  web compositions and decorative backgrounds such as gradient meshes, noise textures and custom
  cursors (→ material levels, elevation and edge highlights), and the instruction to treat system
  font stacks as a failure (→ the project's single foundation font is correct).
- **Removed as agent-specific:** the instruction addressing a specific vendor's model by name and
  exhorting it to be maximally creative.

### 3. ColourCloudSky/review-ui-design-skill

- **Repository:** https://github.com/ColourCloudSky/review-ui-design-skill
- **Author:** ColourCloudSky
- **License:** MIT — `Copyright (c) 2026 ColourCloudSky`. Verified from the repository's `LICENSE`
  file directly.
- **Source commit:** `4fb0bfaddfb57a9eb3e5bfb7bcd478cb98c1f23f`
- **Used by:** `community-interface-review`

#### community-interface-review

- **Source:** `review-ui-design/SKILL.md` (the references/ knowledge files were **not** imported)
- **Kept — this source's review discipline is framework-neutral and was the main reason it was
  chosen:** the explicit evidence-priority order (user goal → design system and specs → platform
  convention → accessibility → general principles → style trends, with trends last); the rule that a
  design system outranks the reviewer's taste; the four passes (three-second impression → structure →
  detail → counter-evidence); the counter-evidence pass that asks whether a finding is deliberate, has
  a business or accessibility reason, or would regress something that works; separating **severity**
  from **confidence**; deduplicating findings by root cause rather than by symptom; requiring every
  finding to carry location, symptom, impact, action and a way to verify it; preserving strengths so a
  fix does not cost more than the defect; never asserting an unmeasurable pixel, colour or font value;
  translating vague reactions ("not premium") into diagnosable language; no quota of findings; and not
  claiming states are missing when they were merely not captured.
- **Changed for Muralis:** the authority order was concretised against this project's foundation and
  token layer; the evidence channels were replaced (no Figma — measurement, element bounds, the
  running app and the design tokens instead); and a Muralis-specific review-target list was added
  (hero-as-settings-card, Dock-as-gaming-bar, "coming soon" as fake disabled control, glass
  everywhere, accent overuse, wallpaper competing with the UI, both themes).
- **Removed:** all Figma tooling and workflows; the annotated-image and suggestion-image generation
  requirements (not applicable to a text agent, and the annotated-image pipeline is a mechanism this
  project does not have); the mandated Chinese-language report template, since that is one author's
  house style rather than part of the method — its underlying requirement, that findings be specific
  and traceable, is kept; and the reference knowledge files, which were not imported.
- **One technique borrowed from elsewhere:** the blur/squint hierarchy test and the observation that
  component inconsistency is the most common defect in incrementally built interfaces are ideas taken
  from `jezweb/claude-skills` (MIT), which is otherwise web- and Claude-Code-bound and was not adapted.
  The two web/iOS thresholds that source uses (24×24 CSS px focus targets, 44×44 pt touch targets) were
  deliberately **not** carried over, as neither applies to a Windows desktop app.

### 4. Evaluated but not used

Recorded so the decision is auditable. Reasons are factual, not aesthetic.

| Candidate | License finding | Outcome |
| --- | --- | --- |
| `dickwu/apple-design-skill` (656★) | **No license.** Repository metadata reports `license: null` and there is no `LICENSE` file. Its README has a licensing section that only states the Apple HIG text belongs to Apple and that the skill is "provided as they are; use them at your own discretion" — which is not a grant. | Rejected: unlicensed. Architecturally interesting (a cross-platform UI reviewer based on the Apple HIG, explicitly covering desktop frameworks), but unusable without a license. |
| `Ashutos1997/claude-design-auditor-skill` (78★) | **No license file.** The README claims "MIT" in its final section, but no `LICENSE` exists and the API reports no license. | Rejected: provenance unclear despite the README claim. A README sentence is not a license grant; would need the author to add a real `LICENSE`. |
| `simota/design-skills` (1★) | **No license.** | Rejected: unlicensed. |
| `jezweb/claude-skills` (1,013★, MIT) | MIT, but the skill declares `compatibility: claude-code-only` and is web/Tailwind-coupled. | Rejected: agent- and framework-bound. |
| `garrytan/gstack` (133k★, MIT) | MIT, but it is an orchestration harness that shells out to bundled binaries, runs a preamble script on every invocation, downloads a third-party binary and writes telemetry. | Rejected: not portable, and its operation would require executing third-party scripts and installing binaries, which this task forbids. |
| `anthropics/knowledge-work-plugins` (Apache-2.0) | Cleanly licensed and credible; has `design/skills/design-critique/SKILL.md`. | Not used as the primary source: the critique skill is thin (~4 KB) next to the ColourCloudSky methodology, which covers the same ground far more rigorously. Kept as a candidate if the interface-review skill is later widened. |
| Aggregator-only listings (SkillsMP, LobeHub, awesomeskill.ai) | No real upstream repository located. | Rejected: unverifiable provenance. Skills were only used when traced to a real, licensed repository. |

---

## License notice for adapted material

The adapted skills in this directory are derivative works of the sources above and remain subject to
their licenses:

```
MIT License

Copyright (c) 2025 Jesse Vincent                      (obra/superpowers)
Copyright (c) 2026 ColourCloudSky                     (review-ui-design-skill)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Material adapted from `block/agent-skills` is used under the Apache License, Version 2.0
(`Copyright (c) Block, Inc.`). A copy of that license is available at
https://www.apache.org/licenses/LICENSE-2.0. The changes made to that material are stated in full in
the `community-frontend-design` section above, as that license requires.

---

## Adding a community skill later

1. Find it in a **real repository** with a named owner — not an aggregator listing or an
   unattributed fork.
2. **Check the license first.** No license file means no permission, regardless of what a README
   claims. Record the SPDX identifier and the copyright line.
3. Pin the commit you read, so the adaptation is reproducible.
4. Check for scripts, binaries, hooks and network calls. This project does not install or execute
   third-party code; import text only, and if a skill only works with a bundled binary, do not use it.
5. Read the whole `SKILL.md` before adapting, and check what it cross-references — a skill that
   depends on files you are not importing must be rewritten so nothing dangles.
6. Strip the framework and agent assumptions, then re-ground the rules in this repository's actual
   code, tests and docs. An adapted skill that cites a file this project does not have is worse than
   no skill.
7. Add an entry here **before** the skill lands.
