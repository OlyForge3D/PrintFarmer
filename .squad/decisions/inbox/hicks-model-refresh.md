# Decision Inbox: Hicks reviewer model refresh directive

**Requested by:** Jeff Papiez
**Date:** 2026-07-12T17:17:53.916-07:00
**Agent:** Scribe
**Status:** Proposed

## Directive

The GPT code reviewer is Hicks. Update Hicks persistently from GPT-5.5/older GPT-5.x references to model `gpt-5.6-sol` with reasoning effort `max`.

## Scope

- `.squad/team.md`
- `.squad/routing.md`
- `.squad/config.json`
- `.squad/casting/registry.json`
- `.squad/agents/hicks/charter.md`

## Notes

- Do not touch application code, GitHub issues, or PR #741.
- Preserve append-only semantics.
