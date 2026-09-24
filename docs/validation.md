# Validación — evidencia antes de afirmar resultados

**Una compilación no es una prueba de grabación. Una comprobación de salud del proceso auxiliar no es reconocimiento de voz. Aprobar pruebas unitarias no es estabilidad de cinco horas.** Mantén separados esos niveles de evidencia.

## Evidencia actual

La versión publicada actual es [`v0.2.0-beta.6`](https://github.com/Andres-MMG/Trazio-Asistente-Reunion/releases/tag/v0.2.0-beta.6), una prerelease pública. El tag resuelve al commit de preparación `232caf92832e2d7ef53f2578c32a230ed9bcc2e7`. El ZIP oficial mide **86,830,231 bytes** y su SHA-256 es `4a2a5e9e1f9d305e9986f37d07613f4852e463d4024a4f5ce62b951720106070`; el Setup oficial mide **60,003,519 bytes** y su SHA-256 es `e40236da97dd411fd5e17b8c8bf49dfca3dc3ffd0ec63c8c9e2681fc98a4a9c1`. Tamaños y digest de los recursos remotos coinciden con los artefactos verificados y sus archivos laterales. Se conserva la evidencia histórica de beta 3, beta 4 y beta 5. Ninguna de estas comprobaciones sustituye evidencia física.

Beta 6 declara la secuencia de instalador **7** y está publicada. La coincidencia remota acredita los recursos observados, no la ejecución productiva del Setup, una instalación física ni las comprobaciones de hardware y duración pendientes.

| Evidencia | Resultado registrado / límite |
|---|---|
| `v0.1.1-mvp`, conjunto Release serial | 164/164 aprobadas; evidencia histórica |
| `v0.2.0-beta.2` (`a8481ef`), conjunto Release serial | 197/197 aprobadas según la versión publicada |
| Base funcional beta 3 (`b075958`), pruebas enfocadas `Area=VisualCapture` | 54/54 aprobadas; no es evidencia física |
| Base funcional beta 3 (`b075958`), conjunto Release serial | 250/250 aprobadas; compilación Release con 0 errores y 0 advertencias |
| Metadatos/capacidad de beta 3 | 3/3 pruebas de `VersionMetadataTests`; manifiesto con 5 capacidades únicas |
| Implementación fuente 7.2b de beta 4 | Consentimiento adicional de un solo uso, sondeo WGC/D3D11 agregado y acotado, intervalos cifrados de cobertura/actividad, correlación `SystemOutput` y presentación fail-closed en vivo/Historial presentes en la versión publicada |
| Evaluador visual incorporado a la fuente de beta 5 | Herramienta de consola no empaquetada, corpus sintético agregado esquema v1 y golden canónico versionados; `verify` compara bytes sin reescribir. La verificación independiente aprobó 74/74 pruebas `VisualEvaluation`, 206/206 `Area=VisualCapture`, 446/446 del conjunto Release serial, 446/446 del conjunto Release paralelo predeterminado, la compilación con 0 advertencias/0 errores y el comando CLI `VE000 verified`. El golden canónico mide 23.193 bytes y su SHA-256 es `6BBA6F4BA88482FE6616E4145C0840EBAD2BE0A34C9F20832B44500C13BE1738`. Es regresión de software sobre datos sintéticos, no evidencia física ni una función incluida en el paquete |
| Pruebas y compilación de beta 5 | `VersionMetadataTests` 4/4; `VisualEvaluation` 74/74; `Area=VisualCapture` 206/206; conjunto Release serial 446/446; conjunto Release paralelo 446/446; compilación con 0 advertencias y 0 errores; CLI `VE000 verified` |
| `Publish` y smoke de beta 5 | `Publish` aprobado; smoke IPC integrado y smoke IPC explícito aprobados. Demuestran layout/arranque-respuesta del Worker, no captura WGC/GPU ni inferencia real |
| Layout final de beta 5 | 495/495 archivos coinciden byte a byte; `VisualAnalysis.dll` requerida y versionada con App/Worker; evaluator `.exe/.dll/.deps/.runtimeconfig`, corpus/golden y directorios `tools`/`evaluation` ausentes; exactamente cinco capacidades; 0 hallazgos prohibidos, 0 rutas fuente locales y 0 referencias CodeView |
| Paquete/distribución beta 5 | Prerelease publicado, no borrador; tag en `5f16631747dd7f7a7f68d49ba0ca9cbd659f2733`; `Trazio-Asistente-Reunion-v0.2.0-beta.5-win-x64.zip`, 86,829,207 bytes, SHA-256 `0031cab096013b7bb436221a75d874719cc1d47ed03ac4047000b4e3094ddbdf`; tamaño/digest del recurso remoto coinciden exactamente y el archivo lateral está publicado. No constituye prueba física ni firma de código |
| Pruebas y layout final de beta 6/secuencia 7 | Pruebas enfocadas canónicas de versión/instalador/instancia **10/10**; conjunto Release serial **451/451** y paralelo **451/451**; build Release **0 advertencias/0 errores**; `VE000 verified`, `publish` y smoke IPC integrado/explícito aprobados. El layout final coincidió en **495/495** rutas, hashes y contenido byte a byte, con `ProductVersion` `0.2.0-beta.6+232caf92832e2d7ef53f2578c32a230ed9bcc2e7`; quedaron 0 entradas prohibidas, rutas fuente locales o CodeView y se conservaron las cinco capacidades |
| ZIP publicado de beta 6 | `Trazio-Asistente-Reunion-v0.2.0-beta.6-win-x64.zip`: **86,830,231 bytes**, SHA-256 `4a2a5e9e1f9d305e9986f37d07613f4852e463d4024a4f5ce62b951720106070`; tamaño y digest del recurso remoto coinciden con el artefacto verificado y el archivo lateral |
| Setup publicado de beta 6 | `Trazio-Asistente-Reunion-v0.2.0-beta.6-Setup.exe`: **60,003,519 bytes**, SHA-256 `e40236da97dd411fd5e17b8c8bf49dfca3dc3ffd0ec63c8c9e2681fc98a4a9c1`; Authenticode `NotSigned`; SHA-256 del manifiesto del payload `b14b73a7a737c72966d557cf0ff43e84b033e4a4d606653eeb1fd01f049cd135`. Tamaño y digest remotos coinciden, pero nunca se ejecutó con identidad productiva |
| Harness desechable de etapa 5.1 | **14/14** escenarios; incluye rollback transaccional que devuelve código Inno Setup **5** y limpieza final. Usa identidades y espacios de trabajo desechables; no acredita preservación física de contenido arbitrario |
| Límites restantes de etapa 5.1 | El Setup está publicado, pero no firmado ni ejecutado con identidad productiva; cancelación humana `NOT_AUTOMATED`, instalación/actualización/reparación y datos de prueba en otra cuenta o equipo pendientes. Rollback solo hasta completar Setup, no cubre primer arranque posterior ni compatibilidad de datos hacia atrás. La carrera de iniciar App/Worker después del chequeo AppMutex inicial también queda pendiente de validación física |
| Perfiles de producción Meet/Teams | `Unvalidated`, sin política de detección: el procesamiento se abstiene y la evidencia se presenta como **No disponible** |
| Metadatos/capacidades de beta 4 | `VersionMetadataTests`: 4/4 aprobadas; el conjunto empaquetado permanece exactamente en 5 capacidades y no anuncia actividad/correlación visual anónima ni identificación de hablantes |
| Pruebas visuales de beta 4 | `Area=VisualCapture`: 132/132 aprobadas; no es evidencia de WGC/GPU físico |
| Conjunto Release serial de beta 4 | 371/371 aprobadas |
| Conjunto Release paralelo predeterminado de beta 4 | 371/371 aprobadas |
| Compilación Release de beta 4 | 0 advertencias y 0 errores |
| Contrato de publicación y prueba básica de beta 4 | Contrato aprobado; prueba por canal con nombre aprobada. No demuestran inferencia ni captura reales |
| Layout del paquete beta 4 | 494/494 archivos coinciden byte a byte; 0 hallazgos prohibidos, 0 rutas fuente locales y 0 referencias CodeView |
| Paquete combinado App + Worker de beta 3 | Publicado; prueba básica de salud por canal con nombre aprobada; evidencia histórica |
| Distribución pública beta 3 | ZIP/suma de comprobación disponibles como versión preliminar sin firma; evidencia histórica |
| Paquete/distribución beta 4 | Prerelease publicado, no borrador; `Trazio-Asistente-Reunion-v0.2.0-beta.4-win-x64.zip`, 86,823,005 bytes, SHA-256 `c08d6d6df3d986d19773c6a0d3723c587c7449a37b3d1d29ef601a936768c0d6`; tamaño/digest del recurso remoto coinciden exactamente y el archivo lateral está publicado. No constituye prueba física ni firma de código |
| Aceptación de captura/reproducción entre equipos de prueba | Todavía requiere una matriz de aceptación registrada |
| Prueba prolongada de dos / cinco horas | Aplazada; no aprobada por inferencia desde pruebas unitarias |
| Firma y autoactualización | No implementadas ni validadas. El SHA-256 del instalador manual comprueba integridad, no autenticidad |
| Evaluación de calidad del modelo / WER | No establecida; la comparación visual no es una métrica de precisión |

No se afirma un estado de CI. Los registros automatizados públicos no están versionados en este repositorio; mantén la evidencia futura sin datos sensibles y vinculada a una matriz de commit/modelo/dispositivo.

## Conjunto de pruebas automatizadas

El comando habitual de desarrollo es:

```powershell
dotnet test .\Trazio.AsistenteReunion.slnx -c Release
```

### Validar el instalador manual offline

Desde la raíz del repositorio y con Inno Setup 6 instalado:

```powershell
.\installer\publish.ps1
.\installer\build-installer.ps1
.\installer\test-installer.ps1
```

`publish.ps1` genera el payload combinado y un manifiesto determinista con rutas relativas normalizadas, longitudes y SHA-256. El build productivo lo vuelve a ejecutar, exige payload/manifiesto/salida canónicos, comprueba cada archivo antes de compilar y relee todos los campos y formatos de `.sha256` y `.manifest.json`; el binario sigue sin firma. No se usa la palabra «determinista» para el `.exe` de Inno Setup porque no se ha probado un rebuild idéntico byte a byte. No ejecutes un instalador productivo para comprobar el harness: `test-installer.ps1` exige un `TestWorkspaceRoot` bajo `%TEMP%`, crea AppId, carpeta, registro, grupo, nombre de acceso directo y mutex distintos para cada corrida, usa únicamente payloads sintéticos y limpia ese estado desechable.

El harness automatiza instalación limpia, reparación, A→B, rechazo B→A, secuencia moderna cero, contradicción versión/secuencia, estado moderno parcial, rechazo de legacy desconocida, bloqueo por aplicación abierta, manipulación, rollback durante la fase transaccional y desinstalación. El nombre del shortcut desechable incluye el `runId`; se comprueba su eliminación y que `Trazio Asistente Reunión.lnk` del escritorio permanezca ausente o intacto. Verifica archivos arbitrarios fuera del árbol desechable del programa, pero no representa la raíz predeterminada/configurada de datos ni permite afirmar su supervivencia física. Un contrato estático separado comprueba que la definición no referencia esas rutas ni contiene operaciones de copia/borrado sobre ellas. La cancelación humana mediante la interfaz de Inno Setup no se simula de forma fiable: aparece explícitamente como `NOT_AUTOMATED` y requiere aceptación manual. `AppMutex` se comprueba con un holder vivo, pero sigue existiendo la carrera de iniciar App/Worker después del chequeo inicial; ese interleaving requiere validación física. La promesa de rollback termina cuando Setup completa; no cubre el primer arranque posterior ni compatibilidad hacia atrás de los datos.

Evidencia de beta 6/secuencia 7: las pruebas enfocadas canónicas de versión/instalador/instancia aprobaron **10/10**; los conjuntos Release serial y paralelo aprobaron **451/451** y el build Release terminó con **0 advertencias / 0 errores**. Windows PowerShell 5.1 publicó y validó el manifiesto de **495 archivos**: conjunto exacto, orden, rutas, longitudes y SHA-256. `VE000 verified`, `publish` y smoke IPC integrado/explícito aprobaron. El ZIP y layout coincidieron **495/495** archivos byte a byte, con `ProductVersion` `0.2.0-beta.6+232caf92832e2d7ef53f2578c32a230ed9bcc2e7`. El harness PS5.1 aprobó **14 escenarios**, devolvió el código exacto 5 de rollback, confirmó vivo el holder de `AppMutex` y limpió el estado desechable. Los recursos fueron publicados y verificados remotamente, pero **el Setup con AppId productivo nunca se ejecutó**. La cancelación humana permanece `NOT_AUTOMATED`; ninguna de estas comprobaciones es validación física en otra máquina.

Antecedente resuelto: algunas colecciones paralelas dejaban intermitentemente archivos SQLite de prueba bloqueados durante la limpieza. La causa se corrigió mediante disposición determinista de cada `SqliteCommand` y limpieza de conexiones segura ante excepciones. La verificación independiente de beta 5 aprobó **446/446 pruebas** en paralelo predeterminado, respaldada por las regresiones de ciclo de vida de [SqliteCommandLifetimeTests](../tests/Trazio.AsistenteReunion.Tests/SqliteCommandLifetimeTests.cs), que ejercitan operaciones concurrentes y eliminación inmediata de los archivos de prueba. Un nuevo bloqueo debe registrarse como regresión, sin ocultarlo mediante reintentos ni atribuirlo automáticamente al problema histórico.

### Reproducir la base en serie

Este archivo temporal de configuración desactiva el paralelismo entre colecciones xUnit y conserva una base reproducible para comparar resultados o diagnosticar una regresión. No cambia el comportamiento de producción y no es una solución alternativa para el bloqueo histórico, que ya fue corregido. Ejecuta desde la raíz del repositorio después de cerrar Trazio normalmente:

```powershell
$settings = Join-Path ([IO.Path]::GetTempPath()) ("trazio-tests-" + [guid]::NewGuid().ToString('N') + ".runsettings")
$xml = '<RunSettings><RunConfiguration><MaxCpuCount>1</MaxCpuCount></RunConfiguration><xUnit><MaxParallelThreads>1</MaxParallelThreads><ParallelizeTestCollections>false</ParallelizeTestCollections></xUnit></RunSettings>'
Set-Content -LiteralPath $settings -Value $xml -Encoding UTF8
try {
    dotnet test .\Trazio.AsistenteReunion.slnx -c Release --settings $settings
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }
}
finally { Remove-Item -LiteralPath $settings -ErrorAction SilentlyContinue }
```

### Mapa de cobertura, no porcentaje de cobertura

| Comportamiento | Pruebas representativas versionadas |
|---|---|
| Cifrado / preferencias | [CryptoTests](../tests/Trazio.AsistenteReunion.Tests/CryptoTests.cs), [SettingsStoreTests](../tests/Trazio.AsistenteReunion.Tests/SettingsStoreTests.cs) |
| Procesamiento acotado / recuperación | [RecordingCoordinatorTests](../tests/Trazio.AsistenteReunion.Tests/RecordingCoordinatorTests.cs), [PipelineLogicTests](../tests/Trazio.AsistenteReunion.Tests/PipelineLogicTests.cs), [StartupRecoveryServiceTests](../tests/Trazio.AsistenteReunion.Tests/StartupRecoveryServiceTests.cs) |
| Archivo de audio / traslados de almacenamiento | [AudioArchiveStoreTests](../tests/Trazio.AsistenteReunion.Tests/AudioArchiveStoreTests.cs), [StorageMigrationServiceTests](../tests/Trazio.AsistenteReunion.Tests/StorageMigrationServiceTests.cs) |
| Identidad / selección de ventana / revisión / glosario / Markdown | [LocalProfileTests](../tests/Trazio.AsistenteReunion.Tests/LocalProfileTests.cs), [MeetingWindowSelectionTests](../tests/Trazio.AsistenteReunion.Tests/MeetingWindowSelectionTests.cs), [ReviewStoreTests](../tests/Trazio.AsistenteReunion.Tests/ReviewStoreTests.cs), [GlossaryCandidateExtractorTests](../tests/Trazio.AsistenteReunion.Tests/GlossaryCandidateExtractorTests.cs), [ObsidianMarkdownExportTests](../tests/Trazio.AsistenteReunion.Tests/ObsidianMarkdownExportTests.cs) |
| Navegación / comparación / revisiones de inferencia | [SegmentAudioNavigatorTests](../tests/Trazio.AsistenteReunion.Tests/SegmentAudioNavigatorTests.cs), [TranscriptComparisonTests](../tests/Trazio.AsistenteReunion.Tests/TranscriptComparisonTests.cs), [HistoryRetranscriptionServiceTests](../tests/Trazio.AsistenteReunion.Tests/HistoryRetranscriptionServiceTests.cs) |
| Contratos de proceso auxiliar / paquete / instalador | [IpcTests](../tests/Trazio.AsistenteReunion.Tests/IpcTests.cs), [HistoryWorkspacePublicationTests](../tests/Trazio.AsistenteReunion.Tests/HistoryWorkspacePublicationTests.cs), [VersionMetadataTests](../tests/Trazio.AsistenteReunion.Tests/VersionMetadataTests.cs), [SingleInstanceGuardTests](../tests/Trazio.AsistenteReunion.Tests/SingleInstanceGuardTests.cs), [InstallerPackageContractTests](../tests/Trazio.AsistenteReunion.Tests/InstallerPackageContractTests.cs) |
| Captura visual efímera / consentimiento / estado | [BoundedDropOldestProcessorTests](../tests/Trazio.AsistenteReunion.Tests/BoundedDropOldestProcessorTests.cs), [VisualCaptureSessionControllerTests](../tests/Trazio.AsistenteReunion.Tests/VisualCaptureSessionControllerTests.cs), [WindowsGraphicsCaptureServiceTests](../tests/Trazio.AsistenteReunion.Tests/WindowsGraphicsCaptureServiceTests.cs), [VisualCapturePresentationTests](../tests/Trazio.AsistenteReunion.Tests/VisualCapturePresentationTests.cs) |
| Actividad visual anónima / cifrado / correlación / presentación | [AnonymousVisualAnalysisActivationTests](../tests/Trazio.AsistenteReunion.Tests/AnonymousVisualAnalysisActivationTests.cs), [D3D11VisualProbeExtractorTests](../tests/Trazio.AsistenteReunion.Tests/D3D11VisualProbeExtractorTests.cs), [VisualProbePipelineTests](../tests/Trazio.AsistenteReunion.Tests/VisualProbePipelineTests.cs), [VisualProbeProfileTests](../tests/Trazio.AsistenteReunion.Tests/VisualProbeProfileTests.cs), [DeterministicVisualActivityDetectorTests](../tests/Trazio.AsistenteReunion.Tests/DeterministicVisualActivityDetectorTests.cs), [SqliteVisualProbeEvidenceSinkTests](../tests/Trazio.AsistenteReunion.Tests/SqliteVisualProbeEvidenceSinkTests.cs), [AnonymousVisualActivityCorrelatorTests](../tests/Trazio.AsistenteReunion.Tests/AnonymousVisualActivityCorrelatorTests.cs), [AnonymousVisualEvidencePresentationTests](../tests/Trazio.AsistenteReunion.Tests/AnonymousVisualEvidencePresentationTests.cs) |
| Evaluador sintético / corpus / golden / privacidad | [VisualEvaluationCorpusTests](../tests/Trazio.AsistenteReunion.Tests/VisualEvaluationCorpusTests.cs), [VisualEvaluationRunnerTests](../tests/Trazio.AsistenteReunion.Tests/VisualEvaluationRunnerTests.cs), [VisualEvaluationIsolationTests](../tests/Trazio.AsistenteReunion.Tests/VisualEvaluationIsolationTests.cs), [VisualEvaluationGoldenTests](../tests/Trazio.AsistenteReunion.Tests/VisualEvaluationGoldenTests.cs) y [contrato del corpus](../evaluation/stage-7b/README.md) |

Estas pruebas no reemplazan controladores físicos, interacción con el escritorio renderizado ni precisión de voz medida.

### Regresión sintética de actividad visual

El corpus agregado versionado puede verificarse sin acceder a WGC, superficies, datos de aplicación ni configuración de producción:

```powershell
dotnet run --project .\tools\Trazio.AsistenteReunion.VisualEvaluation -c Release -- verify `
  --corpus .\evaluation\stage-7b\synthetic-corpus-v1.json `
  --golden .\evaluation\stage-7b\synthetic-corpus-v1.golden.json
```

`VE000 verified` demuestra únicamente que la lógica actual reproduce el informe canónico sintético. `VE200 golden-mismatch` exige revisión explícita y nunca actualiza el archivo. Los valores del candidato son entradas de regresión tomadas de pruebas existentes: no son umbrales de producción, no ordenan candidatos y no habilitan los perfiles Meet/Teams. La aceptación física WGC/GPU/accesibilidad, las reuniones reales y las duraciones de 2/5 horas continúan pendientes.

La CLI y esos dos archivos de evaluación existen solo para desarrollo offline. El paquete debe contener `Trazio.AsistenteReunion.VisualAnalysis.dll`, pero no debe contener ningún artefacto `VisualEvaluation`, el corpus, el golden ni directorios `tools`/`evaluation`. Esta separación no agrega actividad/correlación visual ni identificación de hablantes al manifiesto de capacidades.

## Pruebas básicas de paquete e inferencia real

Solo inicio/IPC del proceso auxiliar:

```powershell
.\installer\smoke-worker.ps1 -WorkerPath .\artifacts\publish\Trazio.AsistenteReunion.Worker.exe
```

Carga real de modelo/transcripción con una muestra autorizada y no sensible:

```powershell
.\installer\smoke-transcription.ps1 `
  -WorkerPath .\artifacts\publish\Trazio.AsistenteReunion.Worker.exe `
  -ModelPath 'C:\TestFixtures\ggml-base.bin' `
  -AudioPath 'C:\TestFixtures\reference-es.wav' `
  -Language es -ExpectedText 'frase de referencia'
```

Las rutas/textos de la muestra son ejemplos; proporciona tus propios archivos. El script requiere **RIFF WAV PCM16, mono, 16 kHz**, acota tamaño/tiempo de entrada y comprueba salida no vacía y frases esperadas opcionales. Registra hashes, tiempos y transcripción en `artifacts/model-validation/transcription-result.json`, además de registros del proceso auxiliar. Pueden contener contenido/rutas privadas: no publiques evidencia sin depurar de reuniones reales. Comprobar una frase no es una evaluación representativa de precisión.

## Aceptación manual de versiones

Usa habla autorizada y no sensible. Registra versión de aplicación, hash de modelo, Windows/CPU/RAM, dispositivos, fuentes, duración y resultado. Marcar un elemento requiere evidencia real.

### Matriz funcional breve — aceptación registrada pendiente

El cierre funcional de la etapa 6 no marca estos controles como aprobados. La interfaz, la identidad local y la escritura real dentro de una bóveda Obsidian requieren evidencia manual separada.

- [ ] **Solo micrófono:** frases conocidas; verificar fuente/nombre, transcripción, sonido guardado y reproducción tras reinicio.
- [ ] **Solo audio del equipo:** clip conocido en el dispositivo seleccionado; verificar que no se atribuya al micrófono.
- [ ] **Ambas fuentes:** alternar habla/clips; verificar pistas independientes, tiempos y ausencia de intercambio de fuentes.
- [ ] Título automático y renombrado; perfil local y nombre por reunión funcionan independientemente.
- [ ] Abrir el selector no enumera hasta **Actualizar lista**; verificar exclusión de Trazio y que Meet/Teams/Otra se presenten sin afirmar reunión activa.
- [ ] Iniciar sin ventana; iniciar con Meet/Teams/Otra; confirmar en Historial solo el proveedor normalizado y que el título visible de la ventana no aparezca en la base.
- [ ] Cerrar la ventana antes de iniciar y durante una grabación: aviso no modal, ninguna reasignación, audio continuo y proveedor inmutable de la sesión ya iniciada.
- [ ] Confirmar que asociar una ventana superior no activa WGC y no limita WASAPI; autorizar captura visual por separado y validar Cancelar predeterminado, teclado, Enter, Escape, foco y lector de pantalla.
- [ ] Con una sesión activa que capture `SystemOutput`, abrir el segundo consentimiento de análisis anónimo; comprobar que es explícito, de un solo uso, ligado a la ventana/sesión exactas y que no se hereda ni se reactiva.
- [ ] Con WGC autorizado: verificar borde del sistema, GPU/dispositivo, redimensión, minimizar/restaurar/cerrar, controles visuales separados y continuidad de audio/transcripción ante cada salida.
- [ ] En Meet y Teams reales, confirmar que los perfiles de producción siguen `Unvalidated`, el procesamiento se abstiene y la evidencia se muestra como **No disponible** tanto en vivo como en Historial; no marcar coincidencia ni actividad hasta calibrar/validar un perfil.
- [ ] Confirmar que la evidencia visual solo aparece en filas `SystemOutput`; el micrófono queda oculto y transcripción, `SpeakerName`, TXT, Markdown y Obsidian permanecen idénticos.
- [ ] Inspeccionar almacenamiento, registros y paquete después de la sesión: no deben existir píxeles, video, screenshots ni bytes de imagen retenidos por 7.2a/7.2b. Solo pueden existir intervalos cifrados derivados de cobertura/actividad; no OCR, rostros, nombres, chat, subtítulos ni documentos.
- [ ] Pausar/reanudar/detener; no confundir intervalos pausados con sonido capturado; la finalización informa errores.
- [ ] Navegar con **Segmento anterior/siguiente** y confirmar que cambia selección/scroll sin reproducir; probar primera, intermedia, última, una sola fila y cambio de revisión.
- [ ] Recorrer fragmentos y huecos con deslizador/saltos de 10 segundos; confirmar tiempo real no comprimido, salto determinista al siguiente audio, fin exclusivo del segmento y ninguna reproducción posterior a ese límite.
- [ ] Durante reproducción, confirmar resaltado por fuente sin mover foco, selección ni editor; en huecos no debe existir fila resaltada y cambiar sesión/fuente/revisión o detener debe limpiarlo.
- [ ] Con un dispositivo de audio real, comparar el resaltado lógico con el sonido audible antes y después de huecos; registrar cualquier latencia introducida por el búfer del dispositivo.
- [ ] Pausar durante el descifrado y en el cambio entre dos fragmentos; confirmar que el fragmento siguiente no comienza hasta pulsar **Continuar**.
- [ ] Desconectar o hacer fallar el dispositivo al pausar/continuar; confirmar error visible, detención completa y ausencia de cierre inesperado de la aplicación.
- [ ] Iniciar la preparación de un fragmento y eliminar la sesión; confirmar que **Eliminar sesión** permanece deshabilitado durante la operación, que el borrado bloquea nuevas reproducciones hasta terminar y que, tras confirmar por una vía ya iniciada, la cancelación termina antes del borrado.
- [ ] Escuchar una fila de una fuente distinta y confirmar que la fuente, la revisión, la forma de onda y el editor visibles no cambian; solo el resaltado corresponde a la fuente transitoria reproducida. Pausar/continuar/detener debe seguir disponible, pero los saltos y el deslizador deben quedar deshabilitados durante ese intervalo acotado.
- [ ] Repetir el flujo con teclado y lector de pantalla; confirmar nombres/ayuda de navegación y estado sin anuncios excesivos.
- [ ] Corregir/deshacer; guardar solo términos modificados del glosario; comparar original/nueva revisión sin sobrescrituras.
- [ ] Retranscribir una fuente completa y cancelar otra ejecución; las incompletas no son comparaciones exitosas.
- [ ] Una sesión antigua/con audio eliminado permite revisión de texto, explica el sonido ausente y rechaza retranscripción imposible.
- [ ] Exportar TXT/Markdown/WAV deliberadamente; verificar que Markdown use el original efectivo con correcciones humanas, metadatos/marcas/fuentes correctos, ningún audio/ID/ruta interna y el destino elegido dentro de una bóveda de prueba; después manejar los archivos sin cifrar de forma segura.

### Resiliencia y almacenamiento — validación física pendiente

- [ ] Desconectar un dispositivo produce fallo visible y ninguna sustitución silenciosa.
- [ ] Fallo controlado del proceso auxiliar: un reinicio y después pausa segura ante fallo repetido.
- [ ] Reiniciar después de interrupción: estado obsoleto normalizado, consentimiento de recuperación y ninguna captura automática.
- [ ] La presión de retención protege audio activo, elimina fragmentos antiguos elegibles y conserva transcripciones.
- [ ] Espacio insuficiente y unidad personalizada no disponible probados sin arriesgar grabaciones valiosas.
- [ ] Traslado de carpeta de prueba interrumpido se reanuda con integridad de manifiesto/hash y sin sobrescribir archivos ajenos.
- [ ] Eliminación de sesión y reinicio reconcilian base/archivo de audio; no suponer borrado físico seguro.

### Duración y distribución — requisitos de producción pendientes

- [ ] Reunión de micrófono/salida de **dos horas** en un equipo representativo.
- [ ] Reunión de **cinco horas** con pausa/reanudación y carga pendiente realista de inferencia.
- [ ] Matriz física WGC/GPU/accesibilidad con Google Meet y Microsoft Teams reales antes de cambiar cualquier perfil de `Unvalidated` a `Validated`.
- [ ] Mediciones periódicas de CPU/memoria/disco/espacio libre y marcas de referencia para detectar contenido faltante.
- [ ] Reabrir la reunión larga; comprobar inicio/medio/final de ambas pistas y cobertura de transcripción.
- [ ] Instalación limpia/actualización del paquete completo en otra cuenta/equipo Windows con datos sintéticos.
- [ ] Validación física de instalación, actualización, reparación, cancelación y reversión en otra cuenta/equipo, con datos sintéticos y luego con una copia de prueba de datos existentes.

Todavía no se han aceptado umbrales universales de latencia/precisión/memoria. Registra mediciones y acuerda presupuestos para el hardware objetivo antes de declarar preparación para producción; nunca reemplaces umbrales ausentes con una aprobación inventada.

## Comprobaciones solo de documentación

Para documentación pasiva: verificar afirmaciones contra el código, enlaces/anclas locales, validez XML y contenido seguro del SVG, e inspeccionar visualmente la portada. No recompilar la aplicación solo para aparentar pruebas de una edición documental. Los cambios de ejecución requieren las comprobaciones de comportamiento y físicas pertinentes indicadas antes.
