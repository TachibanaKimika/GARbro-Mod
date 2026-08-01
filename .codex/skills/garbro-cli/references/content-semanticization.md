# Content Semanticization Boundary

Use this reference before describing GARbro output as a translated corpus,
semantic index, searchable knowledge base, speaker-resolved script, OCR result,
or complete game dataset.

GARbro is the decoding and safe materialization layer. It supplies strong
resource and provenance evidence; it does not infer the meaning of every asset.

## What GARbro owns

GARbro can:

- recognize supported archive, image, audio, and script formats;
- apply an explicit XP3 scheme and Hx/Cx artifacts;
- open archive indexes and safely materialize selected entries;
- preserve duplicate logical occurrences through stable `entryIndex` and
  deterministic output mappings;
- export handler-supported script text as `filtered`, `raw`, `dump`, or message
  `jsonl`;
- decode image metadata and re-encode images with writable handlers;
- enforce finite file, byte, and depth budgets using actual stream accounting;
- emit `garbro.cli/v1` machine events;
- record source, plan, output byte counts, and SHA-256 in
  `garbro.extraction-manifest/v1`.

These are decoding, transformation, and provenance claims. They are not
semantic claims.

## What GARbro does not own

GARbro does not itself perform or guarantee:

- OCR for words drawn into images;
- speech-to-text or audio transcription;
- translation or localization quality;
- language detection or normalization;
- canonical speaker/entity resolution across scripts;
- character, scene, event, emotion, or content classification;
- voice-to-line alignment when a script handler does not emit an unambiguous
  `voice` association;
- image-to-script, audio-to-script, cross-archive, or other cross-asset semantic
  links;
- deduplication by meaning;
- embeddings, vector-database ingestion, retrieval ranking, or summaries;
- lossless source reconstruction or round-trip game patching;
- corpus completeness outside handlers and modes actually reported by runtime
  discovery.

Do not fill these gaps by guessing from file names, extensions, neighboring
entries, or human-readable error messages.

## Build a downstream pipeline in layers

### 1. Decode and inventory

Use GARbro to probe, validate schemes, plan, extract, export scripts, and
convert supported images. For large data, follow
[large-library-ingest.md](large-library-ingest.md) and use `--output jsonl`.

Retain the terminal status. `partial_success`, skipped, failed, or
`not_attempted` files, an inconclusive scheme check, or an Hx generation failure
means the corpus is not proven complete.

### 2. Preserve provenance

Carry these identifiers into every downstream row or document where available:

```text
sourceArchive.path
sourceArchive.sha256
handler.tag
handler.optionsIdentity / scheme fingerprint
planFingerprint
entryIndex
entryName
occurrence and groupSize
outputRelativePath
actualBytes
outputSha256
script formatTag and mode
image source path and target format
```

Use `entryIndex` plus source archive identity as the occurrence key. Duplicate
logical names can represent distinct data; do not collapse them solely by path
or case-insensitive name.

### 3. Add modality-specific analysis

Run separate tools on materialized outputs:

- OCR on selected decoded images;
- transcription on materialized audio, using an appropriate downstream
  decoder/transcriber when needed;
- translation on script message rows or reviewed text;
- entity resolution using explicit and inferred evidence kept in separate
  fields;
- classifiers for character, event, scene, or asset type;
- embedding/index creation after content and provenance validation.

Record each tool, model/version, parameters, timestamp, input hash, confidence,
and output status. Do not overwrite GARbro's source evidence with inferred
labels.

### 4. Link assets with evidence

Prefer explicit handler output, such as a script JSONL row's `voice` field.
When linking by stems, directory proximity, timing, OCR text, or similarity,
mark the relationship as inferred and retain method/confidence. Missing
optional `name` or `voice` fields means unknown, not empty or safely inferable.

### 5. Validate before indexing

Before calling a dataset complete or building a vector index, verify:

- every GARbro run reached an acceptable terminal status;
- manifest entry statuses are current after any resume, folding append-only
  rows without allowing a later `not_attempted` audit row to erase an earlier
  materialized state;
- required outputs have measured `actualBytes` and, where needed,
  `outputSha256`;
- script handlers advertised the selected mode;
- known skipped, failed, not-attempted, duplicate, unrecognized, unsupported,
  and no-selection inputs are represented in an exception inventory;
- downstream OCR/transcription/translation failures are counted separately
  from GARbro decode failures.

## Declared metadata is not semantic or measured evidence

Do not confuse archive planning fields with final facts:

- `storedBytes` is stored archive size;
- `declaredBytes` is the best available pre-write size estimate;
- `declaredBytesSource` explains that estimate;
- `materializedSizeMayDiffer: true` is an explicit warning;
- `actualBytes` is measured final output;
- `observedBytes` is the safety-budget charge and may include failed work.

A nonzero `declaredBytes` does not prove successful decoding, content type,
language, semantic uniqueness, or final output size. Use final status,
`actualBytes`, hash, and downstream validation.

## Script mode claims

The modes support different uses:

- `filtered`: readable handler-selected dialogue/narration/choices;
- `raw`: broader decoded context where the handler supports it;
- `dump`: diagnostic structures and internal state;
- `jsonl`: handler-identified message boundaries with optional names/voices.

None promises all executable strings, all image text, all audio speech, perfect
display order, canonical identities, translation readiness, or lossless
round-trip reconstruction. A handler may support only a subset. Discover
`textModes`; never silently substitute a mode and never generalize one
handler's behavior to all formats.

## Image conversion claims

`image convert-batch` recognizes, decodes, and re-encodes images. Its
`verify-header` and `verify-decode` resume modes validate encoded outputs, not
visual equivalence, OCR correctness, or semantic content. WebP verification can
distinguish `VP8 ` lossy from `VP8L` lossless and reject a lossless/lossy preset
mismatch, but cannot prove the exact numeric quality of an arbitrary lossy
file. A successful WebP or PNG output means conversion completed under the
selected handler and budget; it does not mean the image was classified or
described.

## Suggested downstream record

One semantic document can retain evidence and inference separately:

```json
{
  "source": {
    "archiveSha256": "...",
    "entryIndex": 123,
    "entryName": "scenario/start.ks",
    "outputSha256": "..."
  },
  "garbro": {
    "handlerTag": "KiriKiri/Script",
    "scriptMode": "jsonl",
    "status": "written",
    "actualBytes": 8192
  },
  "semantic": {
    "text": "...",
    "speakerCanonicalId": null,
    "labels": [],
    "method": "downstream-model-name",
    "confidence": null
  }
}
```

The exact downstream schema is caller-owned. The important boundary is that
GARbro facts remain attributable and model-derived fields remain explicitly
inferred.
