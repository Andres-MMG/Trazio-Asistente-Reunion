# Trazio Asistente Reunión

Windows-first local meeting transcription. The application captures a selected microphone and/or the selected Windows output device, transcribes locally through a separate Whisper worker, and stores encrypted transcript content in SQLite.

This product is **Trazio Asistente Reunión**. It is separate from Trazio Platform.

## Current MVP

- Manual start, pause, resume, and stop.
- WASAPI microphone and system-output capture through NAudio.
- A confirmed local profile attributes microphone segments to the current user; computer-output segments retain their distinct source label and are never assigned that local name. A per-meeting override does not change the saved profile.
- 16 kHz mono PCM processing and 15-second transcription windows with a 1-second carry-over.
- Local Whisper.net worker over a current-user-only named pipe.
- Encrypted session titles, transcript text, and pending PCM using AES-256-GCM.
- Master key, saved device/model preferences, and local profile protected by Windows DPAPI for the current Windows account.
- SQLite history, TXT export, deletion, and restart-safe pending chunks. Pending audio expires after 24 hours for completed or interrupted sessions; active Recording sessions are never removed by TTL cleanup. Recent interrupted-session audio remains available for recovery.
- Mandatory encrypted audio history for every new session. Microphone and computer output are archived separately in bounded 30-second, 16 kHz mono PCM WAV chunks encrypted with AES-256-GCM.
- Per-source capture diagnostics distinguish missing/stalled PCM, archived chunks, transcription backlog, and the latest source error.

## Privacy boundary

The SQLite **file and structural metadata are not wholly encrypted**. Sensitive payload columns are encrypted before insertion. Record identifiers, timestamps, source enums, sequence numbers, and database structure remain visible. The key file and settings file are protected with Windows DPAPI `CurrentUser`; malware running as that user is outside this threat model.

No plaintext audio is written to application storage. PCM is held in bounded buffers and sent over a named pipe. Every retained WAV chunk is encrypted before its atomic `.partial` to final-file commit; SQLite contains metadata and relative paths only. Incomplete and unreferenced archive files are removed during startup reconciliation. The explicit TXT and WAV exports are plaintext and the UI warns before creating either one. WAV export writes only to the exact path selected by the user; a process or machine crash during export can leave an incomplete plaintext WAV at that chosen path. The application does not open a network port and does not contain a cloud fallback.

Encrypted audio retention is global and configurable to 1, 2, or 5 GB. Cleanup removes the oldest chunks only from completed or interrupted sessions; it never removes audio from an active recording or pending write, and it keeps the session and transcript after audio pruning. Deleting a session also removes its retained audio. The history tab reports chunk count and retained duration for each source, plays one decrypted chunk at a time without a plaintext temporary file, and exports microphone or computer audio separately. It does not create a mixed recording.

History also offers a read-only, source-specific comparison between the reviewed original transcript and successful retranscription revisions, or between two model revisions. Text is grouped by segment start time in 15-second intervals with an audio shortcut for each interval. Differences are not presented as automatic accuracy scores. No comparison changes stored transcripts or corrections.

The **History** tab shows the exact local data folder and provides **Open storage folder** and **Copy path** actions. Session rows include the local start time and state. Audio playback and WAV export are enabled only when the selected source has retained audio; transcript export is enabled only when saved text exists. Empty states explain whether a session has no transcript or whether legacy audio is unavailable or retained audio was pruned.

Muting a microphone inside Meet, Teams, or Zoom does **not** mute this application's independent microphone capture. Pause Trazio Asistente Reunión when microphone capture must stop.

## Prerequisites

- Windows 11 x64.
- .NET 10 SDK for building; the packaged app is self-contained.
- An x64 CPU supported by the Whisper.net CPU runtime.
- The recommended multilingual Whisper Base model can be downloaded once from the application (148 MB), or supplied in a `models` folder beside the executable. A custom compatible GGML `.bin` remains available under Advanced.
- Optional: Inno Setup 6 to build the installer.

## Build and test

```powershell
dotnet restore .\Trazio.AsistenteReunion.slnx
dotnet build .\Trazio.AsistenteReunion.slnx -c Release --no-restore
dotnet test .\Trazio.AsistenteReunion.slnx -c Release --no-build
```

The UI expects `Trazio.AsistenteReunion.Worker.exe` beside the application executable. Use the packaging script for a runnable combined folder:

```powershell
.\installer\publish.ps1
```

Output: `artifacts\publish`. To create an installer after publishing:

```powershell
& "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe" .\installer\Trazio.AsistenteReunion.iss
```

## First run

1. Confirm or edit the default local display name. Optionally enter a different name for only this meeting.
2. Select the microphone and output device to capture.
3. Click **Download recommended model** once, or click **Start transcription** to set it up before capture begins. No audio is captured during setup. A verified bundled/cached model is selected automatically.
4. Choose the language.
5. Start transcription. Use headphones to reduce microphone echo.
6. Choose the encrypted audio storage limit before starting. Audio retention is mandatory for new sessions, and a red indicator remains visible while encrypted audio recording is active.

Setup downloads only after an explicit click; startup never contacts the network. Downloads show progress and can be cancelled. Failure/cancellation removes the temporary file and allows retry. Existing custom model paths are preserved; damaged catalog models are rejected rather than silently overwritten. Setup and transcription controls are serialized so repeated clicks cannot start duplicate captures.

The built-in catalog pins `ggerganov/whisper.cpp` revision `5359861c739e955e79d9a303bcbc70fb988958b1`, `ggml-base.bin`, size **147951465 bytes**, SHA-256 `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe`. Source metadata: https://huggingface.co/api/models/ggerganov/whisper.cpp?blobs=true . Both downloaded and bundled catalog models are verified before use. Manual custom models are user-supplied and are not authenticated by this catalog; model compatibility is checked by the transcription worker. Transcription/audio never goes to the download provider.

The default data root is `%LOCALAPPDATA%\Trazio Asistente Reunion`. History can schedule a move to a different folder on a ready local fixed drive. The selected parent receives a `Trazio Asistente Reunion` child folder. The active root, pending move, exact managed-file manifest, and cleanup source are stored together as one versioned state document per Windows user under `HKCU\Software\Trazio\AsistenteReunion`. This locator contains paths, file lengths, and SHA-256 hashes only; it contains no encryption key or meeting content.

A scheduled move runs on the next startup, before the database, key, settings, or models are opened. Trazio copies managed files with bounded streaming, flushes them, verifies length and SHA-256, and commits the new active root only after every file matches. An interrupted move resumes safely. Conflicting or unrelated destination files stop the migration instead of being overwritten. If a configured custom drive is unavailable, startup fails closed with recovery guidance; Trazio never silently creates an empty database elsewhere. Before commit, the exact managed-file manifest is durable. The commit keeps both the new active root and cleanup intent in the same state document until cleanup finishes. Cleanup deletes only manifested source files whose destination length and SHA-256 match; excluded `.tmp`/`.partial` files and unrelated files are never deleted. Interrupted cleanup resumes on the next start.

The active data root contains:

- `trazio-transcripts.db` and SQLite sidecars: session metadata plus encrypted session titles and transcript text. The SQLite structure itself is not fully encrypted.
- `audio\`: encrypted retained-audio chunks. These files cannot be played directly.
- `master.key` and `settings.dat`: protected for the current Windows account using DPAPI.
- `models\`: local speech-recognition models and applicable license notices. Models are not secrets and are not encrypted.

Storage moves are restricted to absolute local fixed-drive folders. Network/UNC, removable, drive-root, nested source/target, installation-subtree, unwritable, low-space, and unrelated nonempty destinations are rejected. The DPAPI-protected key remains bound to the same Windows user after a move.

TXT and WAV exports are never managed or moved. They exist only after an explicit export, are plaintext, and are written to the destination selected by the user. Treat exported files as sensitive meeting data.
## Current limitations

- Windows 11 x64 only; five-hour stability and performance targets still require physical-machine validation.
- Captures an output device, not one browser tab or application. Notifications and music on that device may be transcribed.
- No remote-speaker identification, diarization, translation, summary, extension, cloud sync, or automatic meeting detection.
- Device disconnect pauses the pipeline visibly; it never switches devices silently.
- The worker is restarted once after a pipe/process failure. A second failure pauses transcription.
- On restart, encrypted pending chunks from an interrupted session can be recovered after explicit confirmation; capture never restarts automatically.
- At startup, any session left in Recording state by a crash is atomically normalized to Interrupted before TTL cleanup. Declining recovery leaves it Interrupted; it is never mistaken for an active capture.
- A stable, per-user named-pipe server is acquired before migration, database, or recovery initialization with Windows `PipeOptions.CurrentUserOnly` and a single allowed server instance. It is independent of the selected data root, its ACL is enforced for the current Windows user, and the OS releases it when the process exits or crashes.
- DPAPI ties local data to the Windows account; portable encrypted backup is not implemented.
- Audio retention is mandatory for new sessions, including upgrades from earlier settings. The WAV archive uses lossless PCM for validation and retranscription, so it consumes substantially more space than Opus; compressed audio is not implemented yet.
- Playback, segment seeking, export, and non-destructive retranscription operate source-by-source. Echo cancellation, source mixing, and compressed archival audio are not implemented yet.
- A process crash can lose the final in-memory partial chunk (at most 30 seconds per enabled source). Previously committed encrypted chunks remain referenced by SQLite and are available after restart.
- The installer is unsigned until a production code-signing certificate is configured.

## Dependency licenses

NAudio and Whisper.net use MIT licenses. Microsoft.Data.Sqlite is MIT. Whisper model licenses are independent and must be reviewed for the selected model. See `THIRD-PARTY-NOTICES.md`.


