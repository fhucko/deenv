#!/bin/sh
# Regenerate the model-agnostic .agents/skills mirror from .claude/skills.
# Run from the repo root after editing any .claude/skills/*/SKILL.md.
for f in .claude/skills/*/SKILL.md; do
  out=".agents/skills/${f#.claude/skills/}"
  mkdir -p "$(dirname "$out")"
  sed -e 's/Claude/Codex/g' -e 's/\.claude\//.codex\//g' "$f" > "$out"
done
