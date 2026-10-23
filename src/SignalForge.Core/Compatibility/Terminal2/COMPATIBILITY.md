# Terminal2 compatibility notes

The types and parsers in this directory model the fields persisted by Terminal2's
`Database.cs` and the corresponding profile types in `Options.cs`. The original
source is Copyright (C) 2021-2024 Michael Heyns and is licensed under GNU
GPL-3.0-or-later; see the repository license and `external/Terminal2/LICENSE`.

## Profile files

- `.profile` is parsed as the original line-oriented `[Profile]`, `[Connect]`,
  `[Titles]`, and numeric macro-section format. Colors remain signed ARGB integers
  and font descriptions remain the stored strings.
- The model retains 192 macro slots, 48 title slots, and all 12 filter slots.
- Upstream `Database.SaveProfile` treats filter slot 11 as a transient search slot:
  it clears that slot's text and sets its mode to 1 before writing. The SIGNALFORGE
  serializer deliberately preserves imported slot 11 and does not mutate the
  profile, so custom F11 values survive an import/export round-trip. A subsequent
  save from Terminal2 itself will apply its original transient-search behavior.
- Unknown key/value lines are retained and emitted on export. Invalid typed values
  and values containing line breaks produce diagnostics rather than silent coercion
  or ambiguous profile text.
- Embedded `Name=` is retained when reading content alone. When importing a named
  file, its filename stem takes precedence, matching Terminal2's `ReadProfile(name)`.
- `.tmf` imports require the v2 header and consume title/text line pairs. A trailing
  line terminator is not treated as an additional empty line. Imported macros use
  Terminal2's `NewProfile` defaults and its `$0D` suffix-to-AddCR conversion.

## Send text and framing

- Ordinary send text is UTF-8; `$HH` inserts a byte and `$$` inserts a literal
  dollar sign, matching `FrmMain.SendDollarString` for valid escapes.
- Malformed `$HH` escapes are rejected with a typed diagnostic. The original
  `IsHex` helper accepts every character and maps invalid nibbles to zero; that
  malformed-input behavior is intentionally not reproduced.
- STX/ETX/DLE framing keeps the valid wire algorithm: DLE-stuff payload bytes
  `0x02`, `0x03`, and `0x10`; append ETX; XOR the transmitted stuffed payload and
  ETX for LRC; wrap with STX and the final LRC byte.
- Frame decoding validates the checksum and unquotes DLE-prefixed control bytes.
  It preserves literal payload DLE values; the original `RemoveDLE` removed every
  DLE byte and therefore lost a literal DLE payload. Malformed DLE escapes are
  reported explicitly.

## Filters and macro execution

- `Terminal2FilterMatcher.FindFirstMatch` follows `Filters.FindApplicableFilter`
  in `external/Terminal2/Terminal2/Filters.cs`: scan profile slots in order,
  skip empty text, use mode 0 for starts-with and mode 1 for contains, ignore
  other modes, and apply the supplied offset before matching. Ignore-case mode
  uses the legacy current-culture lowercase operation. The first match wins.
- The desktop filter editor stores an optional exact macro title on each of the
  12 slots. When a completed line arrives from a live byte session, the first
  matching filter with a nonblank title queues that macro for sequential
  execution. The queue holds at most 64 pending triggers; overflow is reported
  and later triggers are dropped. Historical trace projection and end-of-stream
  flushes never execute profile macros. A configured title resolves to the first
  macro slot with an exact, case-sensitive match.
- The desktop maps unmodified F1–F12 and Shift/Ctrl/Alt+F1–F12 into four 12-key
  rows in the selected 48-slot bank. Unlike the upstream key handler, which
  omits its active bank offset, this mapping includes the selected bank. The
  hotkey-bank selector is session UI state and does not modify the profile.
- `Terminal2MacroEngine` takes a profile slot and an injected
  `ITerminal2MacroExecutor`; the executor owns byte writes, waits, and UI or
  connection effects. `CanContinue` exposes the host's connected/stopped state
  at legacy line and repeat checkpoints, while the cancellation token handles
  user-requested cancellation. Repeat waits recheck `CanContinue` in at most
  5 ms slices. Macro text is split on `{0D}` and `{0A}` with empty lines removed,
  matching `FrmMain.LowlevelExecuteMacro` in
  `external/Terminal2/Terminal2/FrmMain.cs`. `$HH` and `$$` are decoded by the
  shared send-text parser. CR/LF options are appended to each non-empty send,
  and character delay is applied after each output byte (including CR/LF).
  Line delay is applied after each transmitted line.
- Recognized directives are `#MACRO title`, `#DISCONNECT`, `#STARTLOG`,
  `#STOPLOG`, `#DELAY n`, `#BREAK [n]`, `#FREEZE`, `#DTR`, `#RTS`, `#STX text`,
  single-`#` comments, and `##` notes. Macro titles resolve by exact,
  case-sensitive title and first matching profile slot. The desktop executor
  implements `#STARTLOG`/`#STOPLOG` using the active profile's log directory,
  filename prefix, and maximum-size option. The separate traffic log stores
  timestamped, per-event TX/RX hex and rotates at the configured size; automatic
  JSONL session recording continues regardless of this switch. `#BREAK` defaults to
  1000 microseconds and values below 1 also use that default. `#DTR` and `#RTS`
  are toggle effects; log start/stop and disconnect effects are requests for
  the host to make idempotent. The note effect text includes Terminal2's
  `-NOTE-> ` prefix and trailing newline.
- Repeat timing begins before the pass, so a slow pass consumes its interval.
  `StopAfterRepeats` counts additional passes after the initial pass: 1 means
  two total runs. A zero limit leaves enabled repeats unbounded; hosts should
  pass a cancellation token for user stop or shutdown. Macro option values are
  checked against the original options UI limits (character delay and repeat
  count through 60000, and line/repeat delays through 999999999 ms).
- Malformed `$HH` and invalid `#BREAK` values produce line-numbered errors and
  skip that line; malformed positive-delay directives and unknown macro names
  produce warnings. This deliberately surfaces malformed input instead of
  retaining Terminal2's always-true `IsHex` conversion or silent unknown-call
  behavior. Recursive `#MACRO` calls are diagnosed and skipped (maximum nesting
  is 64), rather than allowing unbounded recursion. Actual transport connection
  state, logging state, freeze UI, and BREAK/DTR/RTS operations remain the
  executor's responsibility. Traffic logging requires a profile with a valid
  existing log directory; an invalid destination is reported rather than
  silently creating a folder.
- Source references: filter behavior is in `Filters.cs:11-61`; macro splitting,
  directives, send and delay behavior, and repeat counting are in
  `FrmMain.cs:1006-1260`; title lookup is in `FrmMain.cs:1496-1508`;
  options bounds are applied in `FrmMacroOptions.cs:178-191`.

## Local profile collection

- `Terminal2ProfileCollection` manages only top-level `.profile` files in the
  configured directory. A missing directory lists as empty and is created by
  the first save. Listing uses the original case-sensitive `.profile` suffix
  and returns names in deterministic, case-insensitive order with an ordinal
  tie-breaker. `.tmf` import remains the responsibility of the existing
  importer and is not included in this collection API.
- `Load(name)` reads through `Terminal2ProfileImporter`, so the filename stem
  overrides an embedded `[Profile]` `Name`, matching `Database.ReadProfile`.
  `Save(profile)` serializes the existing profile model and replaces the
  `<Name>.profile` file only after validation and a complete same-directory
  temporary write. The temporary file is flushed before a same-directory move;
  successful replacement is atomic on filesystems that provide atomic rename.
- `Rename(oldName, newName)` moves the existing file without rewriting its
  contents, matching `Database.Rename`'s copy/delete intent. The next load uses
  the new filename stem, even if the serialized `Name=` line still has the old
  value. Rename refuses to replace an existing filesystem entry. `Delete(name)`
  removes one file and reports a missing name as an error.
- Names are stems, without `.profile`. Absolute paths, separators, control and
  Windows-invalid filename characters, reserved Windows device names, trailing
  spaces/periods, and names that resolve outside the collection directory are
  rejected before each operation. Profile-file symbolic links and reparse
  points are skipped by listing and refused by load, save, rename, and delete;
  the collection never follows a profile-file link outside its configured
  directory. Filesystem failures are returned as typed diagnostics with the
  operation and underlying reason rather than being swallowed.
- This API does not manage Terminal2's active-profile marker, backup folders,
  default-profile creation, or `.tmf` import. Those remain separate workflows.
- Source references: storage location and initialization are in
  `external/Terminal2/Terminal2/Database.cs:32-49`; file listing is in
  `Database.cs:242-258`; rename/delete are in `Database.cs:120-135` and
  `Database.cs:261-272`; save and filename-name override are in
  `Database.cs:389-500` and `Database.cs:835-836`.
