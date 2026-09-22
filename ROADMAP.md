# Trazio Meeting Assistant — Product Roadmap

Status date: 2026-09-22  
Current maturity: advanced functional MVP / internal beta  
Current release: `0.1.1-mvp`

## Product principles

- Local-first capture and transcription.
- Starting a recording requires an explicit user action or an explicitly enabled automation rule. Every new recording retains encrypted audio and shows a persistent visible indicator.
- Transcript, audio, screenshots, identities, and calendar metadata are sensitive data.
- Automation must be configurable per account/calendar and must always provide Pause and Stop controls.
- Calendar automation, meeting-source selection, speaker attribution, and biometric identification are separate capabilities and must not be presented as the same feature.

## Current position

| Stage | Outcome | Status |
|---|---|---|
| 1 | Technical foundation and local architecture | Complete |
| 2 | Dual-source audio capture and local transcription | Complete |
| 3 | Encrypted transcript and mandatory encrypted audio history | Complete |
| 4 | Usable history and configurable storage | Complete |
| 5 | Distributable and maintainable beta | In progress |
| 5.5 | Local user identity and microphone attribution | Implemented — physical UI validation pending |
| 6 | Accuracy, retranscription, and model comparison | In progress |
| 7 | Meeting source and speaker attribution | Planned |
| 8 | Search, editing, audio navigation, and productivity | Planned |
| 9 | Optional meeting intelligence | Planned |
| 10 | Optional organizational/platform integration | Future |
| 11 | Connected accounts, calendars, and meeting automation | Future |
| 12 | Optional model training from approved corrections | Future — final stage |

The deferred two-hour and five-hour physical soak tests remain release gates for production. They do not block internal beta feature work, but the product must not be declared production-ready without them.

## Stage 5 — Distributable and maintainable beta

### Objective

Make the existing application safe to install, update, diagnose, and recover on other computers.

### Scope

- Establish source control and a version baseline.
- Move to a beta version such as `0.2.0-beta.1`.
- Produce a per-user installer that detects previous versions and preserves user data.
- Implement full-application updates with a signed manifest, SHA-256 verification, controlled shutdown, atomic replacement, and rollback.
- Update the application and Whisper model independently when the model has not changed.
- Sign the executable and installer.
- Export privacy-safe diagnostics without transcripts, retained audio, screenshots, secrets, or encryption keys.
- Remove intermittent SQLite test locking before treating the test suite as fully deterministic.

### Exit criteria

- A tester can install and update Trazio without manually copying files.
- Existing meetings and settings survive update, repair, and rollback.
- A failed update restores the previous working version.
- Diagnostic export contains no meeting content or secrets.

## Stage 5.5 — Local user identity and microphone attribution

### Objective

Give every local recording an owner identity and attribute microphone transcript segments to the confirmed local user.

### Scope

- Add a local profile with a required display name and an optional organization.
- Pre-fill the display name from the Windows account when available, but require the user to confirm or change it.
- Attribute microphone transcript segments to this confirmed local profile by default.
- Allow a meeting-specific display-name override without changing the global profile.
- Store profile data encrypted with the existing local privacy model.

This identifies the person using the configured microphone. It does not prove who is physically speaking into a shared microphone.

### Exit criteria

- Microphone segments show the confirmed local user's name.
- A meeting-specific name override does not alter the global profile.
- Profile data remains encrypted and survives restart.
> Stage 5.5 implementation status: local profile, per-meeting override, encrypted persistence, and microphone attribution are implemented. Automated identity tests pass; physical UI and capture validation remain pending.

## Stage 6 — Accuracy, correction workspace, glossary, and retranscription

> Implementation status: Stage 6 remains in progress. Stage 6A review, synchronized retained-audio navigation, append-only corrections/undo, effective TXT export, and glossary provenance are implemented. Stage 6B now adds non-destructive Whisper retranscription from retained encrypted audio into separate encrypted model revisions with cancellation and failure audit state. A read-only comparison of original/human-reviewed text against any successful model revision, or between two model revisions for the same audio source, is implemented in 15-second time buckets with an audio shortcut for each bucket. Reproducible accuracy metrics, quality profiles, and glossary initial-prompt integration remain pending.

### Objective

Turn every saved conversation into a review workspace where the user can listen, inspect, correct, and reuse terminology to improve future transcripts.

### Quick path

1. Open any saved conversation from History.
2. View its transcript with the same segment-oriented presentation used by Live session.
3. Select a segment to seek and play the corresponding retained audio.
4. Correct the text inline and preserve both the original and corrected versions.
5. Add a corrected name or technical term to the glossary when it should apply to future conversations.
6. Retranscribe retained audio into a new revision and compare results without overwriting prior work.

### Session review workspace

- Open every saved conversation in a full transcript view rather than a read-only text block.
- Keep timestamps, source, local speaker identity, and later remote-speaker attribution visible per segment.
- Select a segment to seek to its audio position and play from that point.
- Provide previous, play/pause, next, speed, and short-repeat controls for correction work.
- Highlight the currently playing segment.
- Support inline correction with Save and Undo.
- Preserve immutable original text, corrected text, editor identity, timestamp, and revision history.
- Allow transcript-only correction when no retained audio exists.
- Clearly explain that playback and retranscription are unavailable for legacy sessions without encrypted audio or sessions whose retained audio was pruned.

### Glossary and correction memory

The glossary is a controlled terminology aid, not automatic model training.

Each entry can contain:

- Preferred spelling, for example Trazio.
- Common mistaken forms or aliases.
- Category such as person, organization, product, acronym, medical term, or technical term.
- Optional language and meeting scope.
- Active/inactive state and usage count.
- Link to the correction that created the entry.

From a corrected segment, the user can choose:

- Correct this segment only.
- Replace the same error in this meeting after previewing every match.
- Add the preferred term and mistaken form to the glossary for future meetings.

Apply glossary knowledge in two bounded ways:

1. Include a size-limited list of relevant proper nouns and technical terms in the Whisper initial prompt.
2. Run deterministic post-processing only for explicit alias-to-preferred-form rules, with word boundaries, a preview, and the ability to undo.

Never silently replace ambiguous ordinary words. The original model output must remain recoverable.

### Learning boundary

Saving a correction does not retrain or permanently modify the Whisper model. Trazio learns operationally by reusing confirmed glossary terms, aliases, and corrections. Actual model learning requires a separately prepared audio/text dataset, model fine-tuning, evaluation, and a new model release. It is intentionally deferred to Stage 12, after calendar automation.

### Model comparison and retranscription

- Retranscribe a saved meeting from encrypted retained audio.
- Preserve the original transcript and every human correction.
- Create a new model-output revision rather than replacing the existing transcript.
- Compare Whisper models using exactly the same Spanish audio sample.
- Provide simple quality profiles: Fast, Balanced, and Accurate.
- Show model, language, glossary version, processing time, and transcript revision in History.
- Define a repeatable accuracy evaluation set with expected text and representative Chilean Spanish audio.
- Calculate useful correction metrics such as corrected segments, accepted glossary suggestions, and word-error comparison on the evaluation set.

### Exit criteria

- Any saved conversation opens in the segment review workspace.
- Sessions with retained audio support synchronized playback, seeking, correction, and retranscription.
- Sessions without retained audio still support transcript correction and glossary updates without pretending audio can be recovered.
- Corrections preserve the original text and are fully undoable.
- Glossary rules can improve a future transcription while recording which rule was applied.
- The same meeting can be retranscribed without recapturing audio or overwriting previous revisions.
- Model comparisons are reproducible and use the same source audio.
## Stage 7 — Meeting source and speaker attribution

### Objective

Associate a session with the selected Meet or Teams surface and attribute remote speech when reliable evidence is available.

This stage is intentionally divided into separate confidence levels. Trazio must never claim a named speaker when the available evidence only supports an anonymous speaker label.

### 7.1 User-selected meeting source

- Ask the user to select the meeting browser tab or application window.
- Detect Google Meet or Microsoft Teams from the selected tab URL/window metadata.
- Bind the selected surface to the active Trazio session.
- Keep the existing system-output capture as a fallback when no surface is selected.

Browser and operating-system privacy controls may require the source picker to be initiated by the user. Calendar automation cannot silently bypass that permission.

### 7.2 Provider adapters

- Build separate, versioned adapters for Google Meet and Microsoft Teams.
- A browser companion extension can read provider-specific accessibility/DOM signals, captions, participant labels, and active-speaker UI state after explicit permission.
- The desktop application remains the recording and encrypted-storage authority.
- If an adapter breaks after a provider UI change, continue transcription with `Remote speaker` rather than guessing a name.

### 7.3 Speaker evidence levels

Store an attribution confidence and evidence type for every named segment:

1. `Local profile`: microphone source mapped to the confirmed local user.
2. `Provider metadata`: active-speaker/caption label obtained from the selected Meet/Teams surface.
3. `User correction`: a human assigned or corrected the speaker.
4. `Diarized speaker`: audio clustering produced `Speaker 1`, `Speaker 2`, etc., without a verified name.

Audio diarization separates voices but does not reveal real names. Mapping a voice to a person would require explicit voice enrollment and introduces biometric privacy and legal obligations; it is outside the initial Stage 7 scope.

### 7.4 Optional speaker-change snapshots

- Do not use screenshots as the primary speaker-identification mechanism.
- If enabled, capture only on a detected speaker transition, not continuously.
- Prefer cropping the active participant tile and name label rather than storing the whole meeting screen.
- Encrypt snapshots, apply a short retention period, and provide independent deletion controls.
- Obtain explicit consent because snapshots may contain faces, chat, shared documents, or other sensitive content.
- Treat visual detection as supporting evidence only; a UI highlight can be delayed, ambiguous, or incorrect.

### Exit criteria

- The user can select a Meet or Teams tab/window and see the detected provider.
- Trazio continues safely when provider metadata is unavailable or an adapter breaks.
- Named remote-speaker labels include evidence and confidence; uncertain segments remain anonymous.
- Optional snapshots are encrypted, bounded, and independently removable.

## Stage 8 — History and productivity

- Full-text search across meetings.
- Advanced batch review and approval of transcript and speaker-label revisions.
- Global glossary management, duplicate detection, import, and export.
- Bookmarks, notes, tags, and follow-up markers.
- Structured export formats in addition to plain text.
- Evaluate Opus for smaller encrypted audio archives while preserving reliable seeking and export.
## Stage 9 — Optional meeting intelligence

- Local summaries.
- Decisions, commitments, and action items.
- Optional translation.
- Topic segmentation.
- All generated content must link back to transcript evidence and remain clearly marked as machine-generated.

## Stage 10 — Optional organizational/platform integration

- Explicit opt-in synchronization with Trazio Platform.
- Organization policies, retention controls, and administrative deployment.
- Shared templates and controlled vocabulary.
- This stage changes the local-only privacy boundary and therefore requires a separate security, legal, tenancy, and consent design.

## Stage 11 — Connected accounts, calendars, and meeting automation

### Objective

After the rest of the local product is stable, allow the user to connect one or more calendars that can prepare, start, and stop meeting capture automatically.

### Connected email/calendar accounts

- Support multiple connected accounts, initially Google Calendar and Microsoft 365/Outlook Calendar.
- Use OAuth authorization; never store account passwords.
- Keep account identity, calendar selection, and recording policy separate.
- Let the user select which calendars are monitored and choose a primary identity/email.
- Read only the minimum event fields required: title, start/end, organizer, attendees, provider, and join URL.
- Allow disconnecting an account and deleting its cached metadata.

### Meeting automation rules

Each connected calendar can have an explicit rule:

- Ignore events.
- Notify only.
- Prepare Trazio and wait for confirmation.
- Start local capture automatically at the scheduled time.

Automatic capture is opt-in per calendar or rule and must show a persistent recording indicator. It must never silently enable recording merely because an account was connected.

### Automatic lifecycle

- Wake or launch Trazio before the event.
- Create the session using the event title and provider metadata.
- Optionally open the meeting join URL; Trazio does not impersonate the user or bypass a lobby.
- Start at the configured offset when the automation rule allows it.
- Stop at scheduled end plus a configurable grace period.
- Use sustained silence only as a secondary stop signal, never as the sole source of truth.
- If the event is extended or the user is still active, offer to continue.
- Finalize encryption and persistence, then minimize or close according to the user's preference.
- Record why the session started and stopped: manual, calendar rule, scheduled end, silence fallback, or error recovery.

### Exit criteria

- Two or more accounts/calendars can coexist without duplicate sessions.
- An opted-in calendar event can launch, start, stop, and finalize a session without losing data.
- The user can always pause or stop automation immediately.
- Disconnecting an account removes its tokens and cached metadata without deleting local meetings.
## Stage 12 — Optional model training from approved corrections

### Objective

After calendar automation is complete, evaluate whether confirmed transcript corrections and retained audio justify training a new Trazio transcription model.

### Entry requirements

- Stages 1–11 are complete.
- Users have explicitly consented to include selected audio and corrections in a training dataset.
- Corrections have reliable revision history and approval status.
- Audio, transcript segments, timestamps, language, source, and speaker evidence can be exported without exposing unrelated meeting content.
- A representative Spanish and Chilean Spanish evaluation set exists before training begins.

### Dataset workflow

1. Select eligible meetings and corrected segments explicitly.
2. Exclude private, unapproved, low-quality, or ambiguous samples.
3. Pair each audio interval with its final human-approved transcript.
4. Remove duplicate and conflicting examples.
5. Split data into training, validation, and untouched test sets.
6. Encrypt datasets at rest and record consent, provenance, and deletion obligations.
7. Train a separate candidate model; never modify the installed production model in place.
8. Compare the candidate against the current model on the untouched test set.
9. Publish a new signed model version only when accuracy improves without unacceptable regressions.
10. Preserve rollback to the previous model.

### Safety and privacy boundaries

- Corrections do not enter training automatically.
- Dictionary entries and ordinary transcript edits remain local unless explicitly selected and approved.
- The user can revoke unprocessed samples before dataset publication.
- Training data must not contain calendar tokens, credentials, screenshots, or unrelated meeting metadata.
- A model version must record its dataset policy, evaluation results, language coverage, and known limitations.

### Exit criteria

- Training is reproducible from a versioned, consented dataset.
- The candidate demonstrably improves the agreed accuracy metrics.
- The new model is independently versioned, signed, installable, and reversible.
- Failure to improve accuracy leaves the current model unchanged.
## Deferred production validation

Before production release:

- Run the controlled microphone-only, system-only, and combined-source test.
- Run a two-hour meeting test.
- Run a five-hour meeting test.
- Verify CPU, memory, storage growth, pause/resume, device changes, crash recovery, encryption, playback, export, and segment completeness.
- Test installer update and rollback on a clean Windows computer and on a computer containing existing meetings.

## Recommended execution order

1. Finish Stage 5 distribution foundations.
2. Implement Stage 5.5 local identity and microphone attribution.
3. Complete Stage 6 retranscription and measurable model comparison.
4. Build Stage 7 source selection before attempting named remote-speaker attribution.
5. Add provider adapters and optional visual evidence only after the selected-source workflow is stable.
6. Complete Stages 8–10 and the deferred long-duration validation before production release.
7. Implement Stage 11 connected accounts, calendars, and opt-in meeting automation.
8. Evaluate Stage 12 optional model training only after calendar automation is complete.
