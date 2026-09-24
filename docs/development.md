# Desarrollar y empaquetar Trazio

**Usa el script de publicación combinada para obtener una aplicación ejecutable.** Compilar o publicar solo el proyecto WPF no coloca el proceso de inferencia junto a él.

## Requisitos y estructura

- Windows 11 x64 y un SDK .NET 10; C# 14 está configurado en los proyectos fuente.
- Una CPU x64 compatible con el entorno de ejecución CPU de Whisper incluido.
- PowerShell. Inno Setup 6 para compilar/probar el instalador; el script localiza `ISCC.exe` en `PATH`, Program Files y `%LOCALAPPDATA%\Programs\Inno Setup 6`.
- Un modelo GGML confiable y WAV de prueba autorizado solo para comprobaciones reales de inferencia.

```text
Directory.Build.props             metadatos de versión compartidos de aplicación/proceso auxiliar
src/
  Trazio.AsistenteReunion.App/     escritorio WPF, captura, revisión y reproducción
  Trazio.AsistenteReunion.Core/    contratos, almacenes, protección y recuperación
  Trazio.AsistenteReunion.Worker/  proceso Whisper local
tests/                           comprobaciones automatizadas
installer/                       publicación, pruebas básicas de proceso/inferencia, Inno Setup
docs/                            documentación de ingeniería
artifacts/publish/                aplicación combinada generada; ignorada por Git
artifacts/publish-manifest.json   inventario determinista del payload; ignorado por Git
artifacts/Trazio-*.zip            ZIP portable y sidecar generados; ignorados por Git
artifacts/installer/              instalador y sidecars generados; ignorados por Git
```

El repositorio actual no fija un parche de SDK en `global.json`, no incluye archivo de bloqueo de dependencias versionado, flujo de CI ni actualizador automático. No describas evidencia de pruebas locales como evidencia de CI.

Los scripts `publish.ps1`, `package-portable.ps1`, `build-installer.ps1` y `test-installer.ps1` se guardan como UTF-8 con BOM. `publish.ps1` y los scripts del instalador mantienen compatibilidad con Windows PowerShell 5.1; el empaquetador portable exige PowerShell Core 7.4 o posterior y no usa `Compress-Archive`. No retires el marcador al editarlos; las pruebas de contrato lo comprueban.

## Compilar

Ejecuta desde la raíz del repositorio:

```powershell
dotnet restore .\Trazio.AsistenteReunion.slnx
dotnet build .\Trazio.AsistenteReunion.slnx -c Release --no-restore
```

Sigue la [guía de validación](validation.md) para las pruebas, incluido el antecedente ya corregido de limpieza paralela de SQLite y sus regresiones. Cierra Trazio normalmente antes de probar el comportamiento de instancia única.

## Publicar una carpeta completa para Windows

Cierra primero la aplicación; no finalices la grabación activa de otra persona.

```powershell
.\installer\publish.ps1
```

[El script](../installer/publish.ps1):

1. Reemplaza únicamente `artifacts\publish` después de comprobar que esté dentro de `artifacts`.
2. Publica App y Worker como `win-x64`, autocontenidos, **no como archivo único**, en la misma carpeta.
3. Copia el README y los avisos de terceros.
4. Comprueba ejecutables/dependencias necesarios y la coincidencia de versión de producto declarada por `Directory.Build.props` entre App, Worker y `Trazio.AsistenteReunion.VisualAnalysis.dll`. La versión publicada actual es `0.2.0-beta.8`. También valida el manifiesto `trazio-capabilities.json`, versionado junto al proyecto WPF y copiado al publicar: exige **exactamente las cinco capacidades existentes** de historial, audio cifrado, exportación Obsidian, asociación de proveedor por ventana y captura efímera consentida. La validación rechaza identificadores duplicados o versiones inválidas. La infraestructura 7.2b no agrega una capacidad empaquetada de actividad/correlación anónima ni de identificación de hablantes: sus perfiles de producción permanecen `Unvalidated` y fallan de forma segura.
5. Rechaza tipos y metadatos no incluidos en la lista permitida del layout, incluidos datos de usuario, bases SQLite, audio, modelos GGML/GGUF, imágenes, video, volcados, registros y material de claves. Además rechaza explícitamente la CLI `VisualEvaluation` (`.exe`, `.dll`, `.deps.json` y `.runtimeconfig.json`), `synthetic-corpus-v1.json`, `synthetic-corpus-v1.golden.json` y cualquier directorio denominado `tools` o `evaluation`, donde sea que aparezcan. `VisualAnalysis.dll` sí es una dependencia distribuida; el evaluador offline y sus datos sintéticos no lo son. El ZIP final también debe inspeccionarse antes del SHA-256 y la subida.
6. Ejecuta la comprobación de salud del proceso auxiliar mediante canal con nombre.
7. Emite `artifacts\publish-manifest.json` con versión, secuencia monotónica y cada ruta relativa normalizada, longitud y SHA-256 en orden ordinal.

Ejecuta `artifacts\publish\Trazio.AsistenteReunion.exe`. Una comprobación de salud demuestra inicio/respuesta del proceso auxiliar, **no** carga de modelo, captura ni reconocimiento; usa la [prueba básica de inferencia](validation.md#pruebas-básicas-de-paquete-e-inferencia-real) para ese límite independiente.

La publicación combinada copia el README y los avisos, no `docs/`. El README incluye un índice de documentación en línea para quienes leen desde el ZIP.

### Crear el ZIP portable canónico

Después de ejecutar `publish.ps1`, crea el recurso portable sin parámetros personalizados:

```powershell
pwsh -NoProfile -File .\installer\package-portable.ps1
```

[El empaquetador](../installer/package-portable.ps1) acepta en modo productivo únicamente `artifacts\publish`, `artifacts\publish-manifest.json` y el nombre `Trazio-Asistente-Reunion-v<versión>-win-x64.zip` derivado de `Directory.Build.props`. Antes de escribir, rechaza archivos faltantes, adicionales o manipulados, rutas duplicadas o ambiguas, traversal, rutas absolutas y cualquier vínculo simbólico, junction o reparse point. El ZIP contiene exactamente las entradas del manifiesto, usa rutas `/`, orden ordinal, fecha fija `2000-01-01T00:00:00`, atributos externos en cero y `ZipArchive` con `CompressionLevel.Optimal`. El propio manifiesto y los sidecars no entran al ZIP porque no pertenecen al inventario del payload.

El ZIP y su `.sha256` se construyen con nombres temporales en la misma carpeta, se verifican y luego se reemplazan mediante operaciones atómicas por archivo; un error controlado revierte el par anterior y elimina temporales. Si también falla esa recuperación, el script conserva y comunica cualquier backup disponible en vez de borrarlo. El sidecar usa SHA-256 en minúsculas, dos espacios, nombre del ZIP y LF final. Dos ejecuciones con el mismo payload producen bytes y SHA-256 idénticos **cuando se usa la misma compilación exacta de PowerShell y del runtime .NET**. El script requiere como mínimo PowerShell 7.4 y .NET 8, pero no promete el mismo flujo DEFLATE entre versiones o parches distintos: registra `$PSVersionTable.PSVersion` y `[Environment]::Version` junto con la evidencia de release.

**Límite transaccional:** ZIP y sidecar son dos archivos y no pueden cambiarse atómicamente como una sola unidad. Un error normal capturado se revierte, pero un corte de energía o terminación abrupta entre ambos reemplazos puede dejar un par mixto; vuelve a ejecutar el empaquetador y verifica el sidecar antes de publicar. Las comprobaciones de rutas/reparse también presuponen un workspace de build local confiable y de un solo escritor: no son una defensa contra un actor local que altere rutas concurrentemente.

Los overrides existen solo para pruebas contractuales: requieren `-AllowTestOverrides`, todas las rutas explícitas dentro de un `TestWorkspaceRoot` desechable bajo `%TEMP%` y un nombre no productivo. Nunca uses ese modo para preparar una release.

### Instalador manual offline

Para compilar el instalador productivo:

```powershell
.\installer\build-installer.ps1
```

[El constructor](../installer/build-installer.ps1) productivo toma `Version` e `InstallerReleaseSequence` desde `Directory.Build.props`, ejecuta `publish.ps1` en la misma invocación, exige `artifacts\publish` y su manifiesto canónico, vuelve a comprobar cada byte y compila [la definición](../installer/Trazio.AsistenteReunion.iss). Genera `Trazio-Asistente-Reunion-v<versión>-Setup.exe`, su `.sha256` y un `.manifest.json`, y relee todos sus campos, nombres, longitudes, hashes y formato. `-SkipPublish`, rutas o cualquier override solo se aceptan con `-AllowTestOverrides`, todos los identificadores no productivos y todas las rutas bajo un `TestWorkspaceRoot` desechable en `%TEMP%`. El instalador sigue sin firma: SHA-256 comprueba integridad del archivo observado, no identidad del editor ni confianza del canal. Solo el manifiesto del payload tiene salida determinista; no se promete un Setup idéntico byte a byte entre compilaciones.

El instalador es por usuario, no usa red y mantiene `{app}` como raíz estable. Cada payload queda en `{app}\versions\<versión>`; accesos directos y registro activan la versión nueva dentro de la transacción de Setup. El estado moderno de versión/secuencia se valida como una unidad; valores ausentes, cero o contradictorios se rechazan antes de `[Files]`. La misma secuencia repara, una secuencia menor actualiza y una mayor bloquea el downgrade. App y Worker mantienen `Trazio.AsistenteReunion.AppRunning.v1`; Setup nunca los cierra/reinicia. Como App/Worker no adquieren `SetupMutex`, aún pueden iniciarse después del chequeo inicial de `AppMutex`: mantenlos cerrados hasta que Setup termine. La definición no contiene operaciones sobre la raíz de datos predeterminada o configurada, modelos, DB, audio, ajustes ni claves; su supervivencia real sigue requiriendo la matriz física pendiente.

Prueba el mecanismo únicamente con identificadores desechables:

```powershell
.\installer\test-installer.ps1
```

[El harness](../installer/test-installer.ps1) compila payloads sintéticos y usa AppId, workspace, carpeta, registro, grupo, nombre de acceso directo y mutex nuevos por ejecución. El shortcut desechable incluye el `runId`; el builder rechaza el nombre productivo en modo test, y el harness comprueba que el acceso directo productivo del escritorio permanezca ausente o byte a byte intacto. Cubre instalación limpia, reparación, A→B, rechazo B→A, secuencia cero, versión/secuencia contradictorias, estado moderno parcial, versión legacy desconocida, aplicación abierta, manipulación de manifiesto, fallo durante `[Files]` y desinstalación. También comprueba que archivos arbitrarios fuera del árbol de programa desechable no cambien; eso **no** sustituye una prueba física con la raíz real de datos. La cancelación interactiva no se automatiza de forma fiable y queda marcada `NOT_AUTOMATED`; no debe presentarse como evidencia aprobada.

**Límite de rollback:** Inno restaura la activación/payload anterior si Setup falla o se cancela antes de completar. No se promete rollback después de ejecutar la versión nueva ni compatibilidad hacia atrás del esquema de datos. Las versiones anteriores consumen disco hasta una futura política explícita de limpieza.

## Disciplina de versiones y publicación

Autoridad de versión de fuente: [Directory.Build.props](../Directory.Build.props). El candidato actual declara `VersionPrefix` **0.2.0**, `VersionSuffix` **beta.9** (versión producto `0.2.0-beta.9`), `InstallerReleaseSequence` **10** y versión de ensamblado/archivo **0.2.0.0**. La versión pública sigue siendo beta 8/secuencia 9; beta 7/secuencia 8 y todas las asignaciones anteriores permanecen como antecedentes históricos inmutables. Cada nueva versión instalable debe aumentar `InstallerReleaseSequence`; nunca compares SemVer beta como texto. El script de publicación, la definición del instalador y las [pruebas de versión/instalador](../tests/Trazio.AsistenteReunion.Tests/InstallerPackageContractTests.cs) comprueban el contrato y conservan los mapeos históricos.

### Candidato beta 9/secuencia 10

La metadata de fuente reserva beta 9/secuencia 10 para distribuir la gestión global básica del diccionario 8.3a. Hasta completar la compilación final, etiquetar y publicar, no se deben registrar commit, tamaños, SHA-256 ni conteos de layout como evidencia definitiva. El candidato conserva exactamente cinco capacidades. El instalador continúa `NotSigned` y el Setup productivo no se ha ejecutado; tampoco existen pruebas físicas visuales, por teclado, lector de pantalla, audibles, WGC/GPU, Meet/Teams o 2/5 horas para esta versión.

### Evidencia publicada de beta 8/secuencia 9

Beta 8/secuencia 9 distribuye la búsqueda local 8.2 y el empaquetador ZIP canónico. El tag público resuelve a `20c94272261f5697a548c56754039029b23f1548`. Release serial/paralelo aprobó **543/543**, el filtro enfocado actual de cinco clases **48/48**, los contratos finales **22/22** y el harness desechable **14/14**; el filtro ampliado **61/61** se conserva como evidencia histórica de 8.2. El layout coincidió **495/495** con `ProductVersion` `0.2.0-beta.8+20c94272261f5697a548c56754039029b23f1548` y exactamente cinco capacidades. El instalador continúa sin firma Authenticode y el Setup productivo no se ha ejecutado; tampoco existen pruebas físicas audibles, de interfaz/accesibilidad, WGC/GPU, Meet/Teams o 2/5 horas para esta versión.

La beta 4 publicada completó `VersionMetadataTests` (4/4), `Area=VisualCapture` (132/132), el conjunto Release serial (371/371), el conjunto Release paralelo predeterminado (371/371) y la compilación (0 advertencias, 0 errores). También aprobaron el contrato de publicación, la prueba básica por canal con nombre y la comparación del layout (494/494 archivos byte a byte, 0 hallazgos prohibidos, 0 rutas fuente locales y 0 referencias CodeView). El tag `v0.2.0-beta.4` corresponde al commit `f871f20c3bf9e77b0cf9ad51134febb83c673de7`. El recurso remoto `Trazio-Asistente-Reunion-v0.2.0-beta.4-win-x64.zip` mide 86,823,005 bytes y su digest coincide exactamente con el ZIP local y el archivo lateral publicado: SHA-256 `c08d6d6df3d986d19773c6a0d3723c587c7449a37b3d1d29ef601a936768c0d6`.

La verificación independiente de beta 5 aprobó `VersionMetadataTests` **4/4**, `VisualEvaluation` **74/74**, `Area=VisualCapture` **206/206**, los conjuntos Release serial y paralelo **446/446**, compilación con **0 advertencias y 0 errores**, CLI `VE000 verified`, `publish` y smoke IPC integrado/explícito. El layout final coincidió **495/495** archivos byte a byte: `VisualAnalysis.dll` estaba presente y versionada; evaluador, corpus/golden y directorios `tools`/`evaluation` estaban ausentes; el manifiesto mantuvo cinco capacidades; hubo **0** hallazgos prohibidos, rutas fuente locales o referencias CodeView.

El tag `v0.2.0-beta.5` resuelve al commit de preparación `5f16631747dd7f7a7f68d49ba0ca9cbd659f2733`. La release es prerelease y no borrador. El recurso remoto `Trazio-Asistente-Reunion-v0.2.0-beta.5-win-x64.zip` mide **86,829,207 bytes** y su SHA-256 es `0031cab096013b7bb436221a75d874719cc1d47ed03ac4047000b4e3094ddbdf`; tamaño y digest coinciden con el ZIP verificado y el archivo lateral publicado.

### Evidencia histórica publicada de beta 6/secuencia 7

Como antecedente histórico, la verificación aprobó las pruebas enfocadas canónicas de versión/instalador/instancia **10/10**, Release serial **451/451**, Release paralelo **451/451**, compilación Release con **0 advertencias/0 errores**, `VE000 verified`, `publish` y smoke IPC integrado/explícito. El layout final coincidió **495/495** archivos, rutas, hashes y contenido byte a byte, con `ProductVersion` `0.2.0-beta.6+232caf92832e2d7ef53f2578c32a230ed9bcc2e7`; el harness desechable aprobó **14/14** escenarios, con rollback de código 5 y limpieza. El tag público resuelve al commit `232caf92832e2d7ef53f2578c32a230ed9bcc2e7`. El ZIP publicado (`Trazio-Asistente-Reunion-v0.2.0-beta.6-win-x64.zip`) mide **86,830,231 bytes**, SHA-256 `4a2a5e9e1f9d305e9986f37d07613f4852e463d4024a4f5ce62b951720106070`. El Setup publicado (`Trazio-Asistente-Reunion-v0.2.0-beta.6-Setup.exe`) mide **60,003,519 bytes**, SHA-256 `e40236da97dd411fd5e17b8c8bf49dfca3dc3ffd0ec63c8c9e2681fc98a4a9c1`, Authenticode `NotSigned`; el manifiesto de payload tiene SHA-256 `b14b73a7a737c72966d557cf0ff43e84b033e4a4d606653eeb1fd01f049cd135`. Tamaños y digest remotos coinciden con los artefactos verificados. El Setup nunca se ejecutó con identidad productiva; cancelación humana y validación en otra máquina o cuenta siguen pendientes.

### Evidencia histórica publicada de beta 7/secuencia 8

La beta 7 incorpora la revisión de historial 8.1 completa y conserva el instalador de la etapa 5.1. Las velocidades `0,75×–2×` usan resampling WDL: cambian el tono y no se persisten. El tag público resuelve al commit `25e3f36867250599ef5026d7270fc37af85a7c44`. El Setup productivo permanece sin firma y no fue ejecutado.

La verificación aprobó los contratos enfocados **10/10**, Release serial y paralelo **520/520**, compilación Release con **0 advertencias/0 errores**, `VE000 verified`, `publish` PS5.1, smoke IPC integrado/explícito y harness desechable **14/14**. El ZIP publicado de **86,851,547 bytes** tiene SHA-256 `951ce1653c2bbdd0d5c0a0827cab5b3c6d5c5c762f1574a3434fa937c44d00e8` y coincide **495/495** con el manifiesto. El Setup publicado de **60,024,844 bytes** tiene SHA-256 `fb476b57e82fbf696e886831029be17d221596e5aaf0fd8cddf808714e0fe526`, Authenticode `NotSigned` y no fue ejecutado productivamente. El manifiesto de **123,575 bytes** tiene SHA-256 `8d585ce9438c9c3778b1a4eb1f8c4de0e9ae3924ed5110a4b624ea661da4a8a0`.

### Lista reutilizable para la próxima versión

Estas casillas son un procedimiento para una versión futura; no representan tareas pendientes de la publicación beta 8 ya verificada.

- [ ] Registrar commit, versión, evidencia de pruebas y límites de validación pendientes.
- [ ] Publicar ambos ejecutables; verificar inferencia real antes de afirmar que un modelo funciona.
- [ ] Empaquetar **toda** la salida, incluidos subdirectorios nativos del entorno de ejecución/avisos.
- [ ] Ejecutar `pwsh -NoProfile -File .\installer\package-portable.ps1`; repetir con el mismo payload y comprobar identidad byte a byte usando la misma compilación de PowerShell/.NET.
- [ ] Excluir símbolos de depuración innecesarios y todos los datos de reuniones, claves, credenciales, registros y evidencia específica del desarrollador.
- [ ] Generar SHA-256; verificar tamaño/hash del recurso subido contra el ZIP local.
- [ ] Mantener una versión preliminar hasta cumplir la [aceptación de producción](validation.md#aceptación-manual-de-versiones).
- [ ] No inventar una licencia de aplicación ni distribuir modelos sin revisar sus condiciones.

Ejemplo de suma de comprobación para un ZIP preparado:

```powershell
pwsh -NoProfile -File .\installer\package-portable.ps1
$version = '0.2.0-beta.9' # Debe coincidir con Directory.Build.props.
$zipPath = ".\artifacts\Trazio-Asistente-Reunion-v$version-win-x64.zip"
Get-Content -LiteralPath "$zipPath.sha256"
Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath
```

La publicación vigente es `Trazio-Asistente-Reunion-v0.2.0-beta.8-win-x64.zip` junto a su archivo `.sha256`; su tamaño **86,866,781 bytes** y SHA-256 `240a792ab8388a0511fb8b25ac466feeb104fdf6779b72938302adc9570b2e3f` corresponden exclusivamente a ese recurso publicado. El ejemplo reproduce el nombre canónico de la versión declarada, pero ejecutarlo no etiqueta, firma, sube ni publica por sí solo. Beta 7 permanece como evidencia histórica. Publicar y enviar cambios requieren autorización explícita del mantenedor.

## Límites de contribución

- Los textos de la interfaz y la documentación pública se mantienen en español; los identificadores técnicos se mantienen en inglés.
- Prefiere cambios de comportamiento acotados, pruebas asociadas y un mapa claro de evidencia.
- Preserva datos del usuario y trabajo ajeno; no uses limpieza destructiva como reparación.
- Nunca incluyas en commits transcripciones, WAV, archivos de audio, bases de datos, modelos, credenciales, claves ni capturas privadas. El cifrado no vuelve aptos los datos privados para un repositorio público.
- Preserva texto original, procedencia de correcciones y revisiones del modelo. No reescribas evidencia silenciosamente ni sugieras que un glosario entrena Whisper.
- Usa commits convencionales; sin atribución de IA ni líneas `Co-Authored-By`.
- Indica si la evidencia es automatizada, solo de código fuente, de dispositivos físicos o de larga duración.

La licencia de la aplicación sigue sin decidirse. Consulta al mantenedor antes de reutilizar/redistribuir el código como si tuviera licencia MIT. Consulta los [avisos de terceros](../THIRD-PARTY-NOTICES.md) para las condiciones de dependencias.
