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
artifacts/installer/              instalador y sidecars generados; ignorados por Git
```

El repositorio actual no fija un parche de SDK en `global.json`, no incluye archivo de bloqueo de dependencias versionado, flujo de CI ni actualizador automático. No describas evidencia de pruebas locales como evidencia de CI.

Los scripts `publish.ps1`, `build-installer.ps1` y `test-installer.ps1` se guardan como UTF-8 con BOM para que Windows PowerShell 5.1 interprete correctamente el texto español. No retires ese marcador al editarlos; las pruebas de contrato lo comprueban.

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
4. Comprueba ejecutables/dependencias necesarios y la coincidencia de versión de producto `0.2.0-beta.5` entre App, Worker y `Trazio.AsistenteReunion.VisualAnalysis.dll`. También valida el manifiesto `trazio-capabilities.json`, versionado junto al proyecto WPF y copiado al publicar: exige **exactamente las cinco capacidades existentes** de historial, audio cifrado, exportación Obsidian, asociación de proveedor por ventana y captura efímera consentida. La validación rechaza identificadores duplicados o versiones inválidas. La infraestructura fuente 7.2b no agrega una capacidad empaquetada de actividad/correlación anónima ni de identificación de hablantes: sus perfiles de producción permanecen `Unvalidated` y fallan de forma segura.
5. Rechaza tipos y metadatos no incluidos en la lista permitida del layout, incluidos datos de usuario, bases SQLite, audio, modelos GGML/GGUF, imágenes, video, volcados, registros y material de claves. Además rechaza explícitamente la CLI `VisualEvaluation` (`.exe`, `.dll`, `.deps.json` y `.runtimeconfig.json`), `synthetic-corpus-v1.json`, `synthetic-corpus-v1.golden.json` y cualquier directorio denominado `tools` o `evaluation`, donde sea que aparezcan. `VisualAnalysis.dll` sí es una dependencia distribuida; el evaluador offline y sus datos sintéticos no lo son. El ZIP final también debe inspeccionarse antes del SHA-256 y la subida.
6. Ejecuta la comprobación de salud del proceso auxiliar mediante canal con nombre.
7. Emite `artifacts\publish-manifest.json` con versión, secuencia monotónica y cada ruta relativa normalizada, longitud y SHA-256 en orden ordinal.

Ejecuta `artifacts\publish\Trazio.AsistenteReunion.exe`. Una comprobación de salud demuestra inicio/respuesta del proceso auxiliar, **no** carga de modelo, captura ni reconocimiento; usa la [prueba básica de inferencia](validation.md#pruebas-básicas-de-paquete-e-inferencia-real) para ese límite independiente.

El script actual de empaquetado copia el README y los avisos, no `docs/`. El README incluye un índice de documentación en línea para quienes leen desde el ZIP.

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

Versión fuente publicada: **0.2.0-beta.5** (`VersionPrefix` 0.2.0 + `VersionSuffix` beta.5); versión de ensamblado/archivo: **0.2.0.0**; secuencia de instalador: **6**. [Directory.Build.props](../Directory.Build.props) es la autoridad compartida. Cada nueva versión instalable debe aumentar `InstallerReleaseSequence`; nunca compares SemVer beta como texto. El script de publicación, la definición del instalador y las [pruebas de versión/instalador](../tests/Trazio.AsistenteReunion.Tests/InstallerPackageContractTests.cs) comprueban el contrato.

La beta 4 publicada completó `VersionMetadataTests` (4/4), `Area=VisualCapture` (132/132), el conjunto Release serial (371/371), el conjunto Release paralelo predeterminado (371/371) y la compilación (0 advertencias, 0 errores). También aprobaron el contrato de publicación, la prueba básica por canal con nombre y la comparación del layout (494/494 archivos byte a byte, 0 hallazgos prohibidos, 0 rutas fuente locales y 0 referencias CodeView). El tag `v0.2.0-beta.4` corresponde al commit `f871f20c3bf9e77b0cf9ad51134febb83c673de7`. El recurso remoto `Trazio-Asistente-Reunion-v0.2.0-beta.4-win-x64.zip` mide 86,823,005 bytes y su digest coincide exactamente con el ZIP local y el archivo lateral publicado: SHA-256 `c08d6d6df3d986d19773c6a0d3723c587c7449a37b3d1d29ef601a936768c0d6`.

La verificación independiente de beta 5 aprobó `VersionMetadataTests` **4/4**, `VisualEvaluation` **74/74**, `Area=VisualCapture` **206/206**, los conjuntos Release serial y paralelo **446/446**, compilación con **0 advertencias y 0 errores**, CLI `VE000 verified`, `publish` y smoke IPC integrado/explícito. El layout final coincidió **495/495** archivos byte a byte: `VisualAnalysis.dll` estaba presente y versionada; evaluador, corpus/golden y directorios `tools`/`evaluation` estaban ausentes; el manifiesto mantuvo cinco capacidades; hubo **0** hallazgos prohibidos, rutas fuente locales o referencias CodeView.

El tag `v0.2.0-beta.5` resuelve al commit de preparación `5f16631747dd7f7a7f68d49ba0ca9cbd659f2733`. La release es prerelease y no borrador. El recurso remoto `Trazio-Asistente-Reunion-v0.2.0-beta.5-win-x64.zip` mide **86,829,207 bytes** y su SHA-256 es `0031cab096013b7bb436221a75d874719cc1d47ed03ac4047000b4e3094ddbdf`; tamaño y digest coinciden con el ZIP verificado y el archivo lateral publicado.

### Lista reutilizable para la próxima versión

Estas casillas son un procedimiento para una versión futura; no representan tareas pendientes de la publicación beta 5 ya verificada.

- [ ] Registrar commit, versión, evidencia de pruebas y límites de validación pendientes.
- [ ] Publicar ambos ejecutables; verificar inferencia real antes de afirmar que un modelo funciona.
- [ ] Empaquetar **toda** la salida, incluidos subdirectorios nativos del entorno de ejecución/avisos.
- [ ] Excluir símbolos de depuración innecesarios y todos los datos de reuniones, claves, credenciales, registros y evidencia específica del desarrollador.
- [ ] Generar SHA-256; verificar tamaño/hash del recurso subido contra el ZIP local.
- [ ] Mantener una versión preliminar hasta cumplir la [aceptación de producción](validation.md#aceptación-manual-de-versiones).
- [ ] No inventar una licencia de aplicación ni distribuir modelos sin revisar sus condiciones.

Ejemplo de suma de comprobación para un ZIP preparado:

```powershell
Get-FileHash -Algorithm SHA256 .\artifacts\Trazio-Asistente-Reunion-v0.2.0-beta.5-win-x64.zip
```

El recurso publicado es `Trazio-Asistente-Reunion-v0.2.0-beta.5-win-x64.zip` junto a `Trazio-Asistente-Reunion-v0.2.0-beta.5-win-x64.zip.sha256`. El ZIP mide **86,829,207 bytes** y su SHA-256 es `0031cab096013b7bb436221a75d874719cc1d47ed03ac4047000b4e3094ddbdf`; el tamaño y digest del recurso remoto coinciden exactamente. Este comando solo permite volver a comprobar un archivo local: no crea un ZIP ni una versión publicada. Publicar, firmar y enviar cambios requieren autorización explícita del mantenedor.

## Límites de contribución

- Los textos de la interfaz y la documentación pública se mantienen en español; los identificadores técnicos se mantienen en inglés.
- Prefiere cambios de comportamiento acotados, pruebas asociadas y un mapa claro de evidencia.
- Preserva datos del usuario y trabajo ajeno; no uses limpieza destructiva como reparación.
- Nunca incluyas en commits transcripciones, WAV, archivos de audio, bases de datos, modelos, credenciales, claves ni capturas privadas. El cifrado no vuelve aptos los datos privados para un repositorio público.
- Preserva texto original, procedencia de correcciones y revisiones del modelo. No reescribas evidencia silenciosamente ni sugieras que un glosario entrena Whisper.
- Usa commits convencionales; sin atribución de IA ni líneas `Co-Authored-By`.
- Indica si la evidencia es automatizada, solo de código fuente, de dispositivos físicos o de larga duración.

La licencia de la aplicación sigue sin decidirse. Consulta al mantenedor antes de reutilizar/redistribuir el código como si tuviera licencia MIT. Consulta los [avisos de terceros](../THIRD-PARTY-NOTICES.md) para las condiciones de dependencias.
