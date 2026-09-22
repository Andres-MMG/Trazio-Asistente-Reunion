# Avisos de terceros

Trazio Asistente Reunión depende de los siguientes paquetes. La distribución debe conservar los avisos correspondientes de sus proveedores originales.

- NAudio — MIT — https://github.com/naudio/NAudio
- Whisper.net y Whisper.net.Runtime — MIT — https://github.com/sandrohanea/whisper.net
- Microsoft.Data.Sqlite — MIT — https://learn.microsoft.com/dotnet/standard/data/sqlite/
- System.Security.Cryptography.ProtectedData — MIT — https://github.com/dotnet/runtime
- xUnit.net (solo pruebas) — Apache-2.0 — https://github.com/xunit/xunit

Los paquetes de distribución pueden incluir el modelo multilingüe Whisper GGML Base verificado. Cuando se incluye, `WHISPER-MODEL-LICENSE.txt` se entrega junto a la aplicación y debe conservarse. La licencia del modelo es independiente de la licencia del código fuente de la aplicación.

El archivo de audio cifrado y la exportación WAV utilizan criptografía y API de flujos de .NET, además de la dependencia NAudio ya indicada; no se incluye código fuente de FluidVoice ni de otros proyectos GPL.
