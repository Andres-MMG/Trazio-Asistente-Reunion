# Validación — evidencia antes de afirmar resultados

**Una compilación no es una prueba de grabación. Una comprobación de salud del proceso auxiliar no es reconocimiento de voz. Aprobar pruebas unitarias no es estabilidad de cinco horas.** Mantén separados esos niveles de evidencia.

## Evidencia actual

La última versión publicada es [`v0.2.0-beta.2`](https://github.com/Andres-MMG/Trazio-Asistente-Reunion/releases/tag/v0.2.0-beta.2), código `a8481ef`. El código fuente posterior agrega 7.2a y necesita una nueva ejecución completa, comprobación de paquete y pruebas físicas antes de una publicación.

| Evidencia | Resultado registrado / límite |
|---|---|
| `v0.1.1-mvp`, conjunto Release serial | 164/164 aprobadas; evidencia histórica |
| `v0.2.0-beta.2` (`a8481ef`), conjunto Release serial | 197/197 aprobadas según la versión publicada |
| Código posterior, pruebas enfocadas `Area=VisualCapture` | 54/54 aprobadas; no es una ejecución completa ni física |
| Conjunto de pruebas paralelo predeterminado | Fallos intermitentes de bloqueo de archivos al limpiar pruebas SQLite; sin resolver |
| Paquete combinado App + Worker | Publicado; prueba básica de salud por canal con nombre aprobada |
| Distribución pública | ZIP/suma de comprobación disponibles como versión preliminar sin firma |
| Aceptación de captura/reproducción entre equipos de prueba | Todavía requiere una matriz de aceptación registrada |
| Prueba prolongada de dos / cinco horas | Aplazada; no aprobada por inferencia desde pruebas unitarias |
| Instalador firmado, actualización y reversión automáticas | No implementados/validados |
| Evaluación de calidad del modelo / WER | No establecida; la comparación visual no es una métrica de precisión |

No se afirma un estado de CI. Los registros automatizados públicos no están versionados en este repositorio; mantén la evidencia futura sin datos sensibles y vinculada a una matriz de commit/modelo/dispositivo.

## Conjunto de pruebas automatizadas

El comando habitual de desarrollo es:

```powershell
dotnet test .\Trazio.AsistenteReunion.slnx -c Release
```

Problema conocido: las colecciones paralelas dejan intermitentemente archivos de bases SQLite de prueba bloqueados durante la limpieza (`ReviewStoreTests` / `SqliteSessionStoreTests` son ejemplos observados). No repitas hasta obtener verde descartando los fallos. Registra el resultado, compáralo con una ejecución en serie y mantén abierta la corrección del aislamiento de pruebas en la etapa 5.

### Reproducir la base en serie

Este archivo temporal de configuración de ejecución desactiva el paralelismo entre colecciones xUnit; no cambia el comportamiento de producción ni corrige la causa subyacente. Ejecuta desde la raíz del repositorio después de cerrar Trazio normalmente:

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
| Contratos de proceso auxiliar / paquete | [IpcTests](../tests/Trazio.AsistenteReunion.Tests/IpcTests.cs), [HistoryWorkspacePublicationTests](../tests/Trazio.AsistenteReunion.Tests/HistoryWorkspacePublicationTests.cs), [VersionMetadataTests](../tests/Trazio.AsistenteReunion.Tests/VersionMetadataTests.cs) |
| Captura visual efímera / consentimiento / estado | [BoundedDropOldestProcessorTests](../tests/Trazio.AsistenteReunion.Tests/BoundedDropOldestProcessorTests.cs), [VisualCaptureSessionControllerTests](../tests/Trazio.AsistenteReunion.Tests/VisualCaptureSessionControllerTests.cs), [WindowsGraphicsCaptureServiceTests](../tests/Trazio.AsistenteReunion.Tests/WindowsGraphicsCaptureServiceTests.cs), [VisualCapturePresentationTests](../tests/Trazio.AsistenteReunion.Tests/VisualCapturePresentationTests.cs) |

Estas pruebas no reemplazan controladores físicos, interacción con el escritorio renderizado ni precisión de voz medida.

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
- [ ] Confirmar que asociar una ventana superior no activa WGC y no limita WASAPI; autorizar visual por separado y validar Cancelar predeterminado, teclado, Enter, Escape, foco y lector de pantalla.
- [ ] Con WGC autorizado: verificar borde del sistema, redimensión, minimizar/restaurar/cerrar, controles visuales separados y continuidad de audio/transcripción ante cada salida.
- [ ] Inspeccionar almacenamiento, registros y paquete después de la sesión: no deben existir videos, screenshots ni bytes de imagen retenidos por 7.2a.
- [ ] Pausar/reanudar/detener; no confundir intervalos pausados con sonido capturado; la finalización informa errores.
- [ ] Navegar entre fragmentos/huecos; usar la acción de audio de cada segmento, pausa y saltos de 10 segundos.
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
- [ ] Mediciones periódicas de CPU/memoria/disco/espacio libre y marcas de referencia para detectar contenido faltante.
- [ ] Reabrir la reunión larga; comprobar inicio/medio/final de ambas pistas y cobertura de transcripción.
- [ ] Instalación limpia/actualización del paquete completo en otra cuenta/equipo Windows con datos sintéticos.
- [ ] Validación de actualización/reparación/reversión que preserven datos una vez que existan esos mecanismos de distribución.

Todavía no se han aceptado umbrales universales de latencia/precisión/memoria. Registra mediciones y acuerda presupuestos para el hardware objetivo antes de declarar preparación para producción; nunca reemplaces umbrales ausentes con una aprobación inventada.

## Comprobaciones solo de documentación

Para documentación pasiva: verificar afirmaciones contra el código, enlaces/anclas locales, validez XML y contenido seguro del SVG, e inspeccionar visualmente la portada. No recompilar la aplicación solo para aparentar pruebas de una edición documental. Los cambios de ejecución requieren las comprobaciones de comportamiento y físicas pertinentes indicadas antes.
