# Script Text Extraction

This document defines the repository convention for script text extractors that
implement `IConfigurableScriptFormat`.

## Modes

Use the shared mode names in `GameRes.ScriptTextMode`:

- `filtered`: human-readable text extracted from the script. This mode may drop
  bytecode, control commands, internal strings, duplicate display-cache text, and
  other non-dialogue data.
- `raw`: decoded source-like text when the format has a meaningful text form.
  Raw mode should preserve useful context even when it is not pleasant to read.
- `jsonl`: one UTF-8 JSON object per output line. This is the preferred mode
  for translation tooling and regression checks because it preserves structure.
- `dump`: diagnostic disassembly or low-level decoded data. Use this only when
  it gives maintainers information that `raw` and `filtered` do not.

Formats should expose only the modes they can support honestly. If a mode is
requested through the UI but the format does not list it in `TextModes`,
extraction falls back to the format default.

Some standalone script formats are also exposed as small virtual archives so
their converted text can be previewed. Entries produced by these archive
handlers must implement `IScriptTextOutputEntry` and report their mode. Archive
extraction then writes only the selected mode and copies the already-converted
entry as-is instead of feeding it through script detection again. The UI's
`Both` choice means `filtered` plus `raw`; diagnostic `dump` and structured
`jsonl` output remain explicit choices.

## JSONL Schema

Use `ScriptJsonLines.CreateStream` to write JSONL. Do not hand-roll JSON
serialization in individual format handlers.

Each line represents one translatable message:

```json
{"name":"Character","voice":"voice\\sample_0001.ogg","message":"Message text"}
```

Supported fields:

- `message`: required. The readable line, choice, narration, or dialogue body.
- `name`: optional. A single speaker or character name.
- `names`: optional. Multiple pending speaker/name strings when the source
  format emits more than one name before a message.
- `voice`: optional. A voice asset path or identifier when the script format
  provides one near the message.

Prefer `name` for one speaker and `names` only when multiple names genuinely
apply to the next message. Do not emit empty messages.

## Extraction Rules

- Keep extraction narrow and format-specific. Avoid broad heuristics that turn
  unrelated Japanese strings into dialogue.
- Preserve the order that the engine would display text. Pair pending
  speaker/name strings with the next message when the bytecode or text syntax
  clearly models that relationship.
- Capture voice metadata only when the format provides an unambiguous voice
  operand, attribute, or adjacent display command.
- If a script has duplicate display-cache text after a display command, JSONL
  should emit the display command record once, not the cache copy.
- `filtered` may remain a simple line-oriented output, but when `jsonl` is
  supported it should be the source of truth for name/message/voice structure.
- Keep `raw` useful for debugging. Do not force raw text to look like filtered
  text.

## Authoring Workflow

When adding or changing a script extractor:

1. Read the closest existing extractor and match its style.
2. Add `jsonl` when the extractor can identify message boundaries. Include
   `name`, `names`, and `voice` whenever the format can provide them reliably.
3. Reuse `ScriptTextEntry` and `ScriptJsonLines`.
4. Add focused smoke tests with synthetic snippets or real samples.
5. If the script syntax is ambiguous, ask the user for raw extracted text,
   archive samples, expected JSONL rows, or engine notes before hard-coding
   heuristics.

When sample archives cannot be shared, ask for a short raw script excerpt that
contains at least one named line, one narration line, one voiced line if the
engine supports voices, and one choice if choices are in scope.
