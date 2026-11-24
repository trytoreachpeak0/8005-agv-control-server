@AGENTS.md

# Claude Code

The instructions for this repository are in `AGENTS.md`, imported above so that
every coding agent reads the same text. Write new rules there, not here. This
file holds only what is specific to Claude Code, and keeps `@AGENTS.md` as its
first line: `scripts/Test-AgentInstructions.ps1` fails the `test` workflow
otherwise.

## Matt Pocock's skills

Installed as the `mattpocock-skills` plugin (user-level). Invoke them namespaced:
`/mattpocock-skills:<name>`. They are explicit-only — use one when the user names
it. `code-review` collides with the bundled `/code-review`; use
`/mattpocock-skills:code-review` for the Standards+Spec review.
