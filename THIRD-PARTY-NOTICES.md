# Third-party notices

Trazio Asistente Reunión depends on the following packages. Distribution must retain the corresponding upstream notices.

- NAudio — MIT — https://github.com/naudio/NAudio
- Whisper.net and Whisper.net.Runtime — MIT — https://github.com/sandrohanea/whisper.net
- Microsoft.Data.Sqlite — MIT — https://learn.microsoft.com/dotnet/standard/data/sqlite/
- System.Security.Cryptography.ProtectedData — MIT — https://github.com/dotnet/runtime
- xUnit.net (tests only) — Apache-2.0 — https://github.com/xunit/xunit

Release packages may bundle the verified Whisper GGML Base multilingual model. When bundled, `WHISPER-MODEL-LICENSE.txt` is included beside the application and must be retained. Model licensing remains independent from the application source license.

Encrypted audio archival and WAV export use .NET cryptography and stream APIs plus the already-listed NAudio dependency; no FluidVoice or other GPL source code is included.
