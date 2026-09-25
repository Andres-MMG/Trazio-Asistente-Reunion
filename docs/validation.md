# Validación — evidencia antes de afirmar resultados

**Una compilación no es una prueba de grabación. Una comprobación de salud del proceso auxiliar no es reconocimiento de voz. Aprobar pruebas unitarias no es estabilidad de cinco horas.** Mantén separados esos niveles de evidencia.

## Evidencia actual

La versión publicada actual es [v0.2.0-beta.14](https://github.com/Andres-MMG/Trazio-Asistente-Reunion/releases/tag/v0.2.0-beta.14), una prerelease pública, no un borrador. El tag resuelve al commit `5acd1def87056941e1678247deea53b0d741919b`. El ZIP mide **86,955,605 bytes**, SHA-256 `c00c220d57ddc2bcbc52c555b18a6acc4248477536fbf9abf61b94f264de8740`; el Setup mide **60,102,067 bytes**, SHA-256 `1ae861dd1d0b25c0adf5a7a898227bcc46d6f21a11d485d6982514bfdf459841`. Los seis recursos remotos coinciden en tamaño y digest. Ninguna de estas comprobaciones sustituye evidencia física.

Beta 10 declara la secuencia de instalador **11** y está publicada. La coincidencia remota acredita los recursos observados, no la ejecución productiva del Setup, una instalación física ni las comprobaciones de hardware y duración pendientes.

La beta 10 pública incluye la revisión de historial 8.1, la búsqueda local 8.2, la gestión global del diccionario 8.3a y el intercambio JSON 8.3b. Sus pruebas automatizadas y artefactos publicados no sustituyen la validación audible, visual, de interfaz, teclado/lector de pantalla, Meet/Teams reales ni las sesiones de 2/5 horas.

Como antecedente de 8.2, el filtro enfocado de beta 8 aprobó **48/48** y el filtro ampliado **61/61** se conserva como evidencia histórica válida, no como conteo actual de beta 10.

La gestión global 8.3a está publicada desde beta 9 y continúa en beta 14. Su evidencia de publicación original incluyó **23/23** pruebas enfocadas sobre orden/filtro/conteos, duplicados independientes, activación persistente sin recifrar blobs, Undo, cascada al eliminar, corrupción fail-closed, lifecycle cancelable, copy honesta y conservación de exactamente cinco capacidades. El conjunto Release de beta 9 aprobó **574/574** en serie y en paralelo; los contratos finales aprobaron **22/22** y el harness **14/14**. Activar una entrada no cambia nada por sí solo; 8.5 exige confirmación explícita para orientar una retranscripción separada y no reemplaza texto. El instalador continúa `NotSigned`, el Setup productivo no se ha ejecutado y faltan validación visual, de teclado, lector de pantalla y pruebas físicas audibles, WGC/GPU, Meet/Teams y 2/5 horas.

La rebanada 8.3b está publicada en beta 10/secuencia 11. Su filtro enfocado de cuatro clases aprobó **48/48**, el conjunto Release completo aprobó **599/599** tanto en serie como con paralelismo predeterminado, los contratos finales aprobaron **22/22**, el harness desechable **14/14** y la solución compiló en Release con **0 advertencias y 0 errores**. El contrato cubre JSON v1 determinista sin metadatos internos, límites de 5 MiB/5.000 filas, clasificación conservadora, vista previa sin escrituras, revalidación transaccional, tabla importada cifrada aditiva, exportación atómica y exactamente cinco capacidades. Esta evidencia automatizada no sustituye una importación/exportación real ni validación visual, por teclado o lector de pantalla.

La rebanada 8.4a está publicada en beta 11/secuencia 12. El filtro Release ampliado de nueve clases aprobó **77/77** pruebas; el conjunto completo aprobó **628/628** en serie y con paralelismo predeterminado, la compilación tuvo **0 advertencias/0 errores**, los contratos finales aprobaron **22/22** y el harness desechable **14/14**. La interacción física por teclado/lector de pantalla, el cierre interactivo y el error real de guardado siguen pendientes.

La rebanada 8.4b está publicada en beta 12/secuencia 13. La suite enfocada de revisión aprobó **35/35** pruebas; el conjunto Release completo aprobó **635/635** en serie y **635/635** con paralelismo predeterminado, la compilación tuvo **0 advertencias/0 errores**, los contratos de versión/instalador/paquete aprobaron **22/22** y el harness desechable **14/14**. El payload contiene **495 archivos** y cinco capacidades. La selección y confirmación física, teclado/lector de pantalla, cancelación humana y un conflicto real siguen pendientes.

La rebanada 8.5 está publicada en beta 13/secuencia 14. La suite enfocada de planificador, integración, IPC, persistencia cifrada y contrato de interfaz aprobó **24/24**; el conjunto Release completo aprobó **645/645** en serie y **645/645** con paralelismo predeterminado; los contratos finales aprobaron **22/22**, el harness desechable **14/14** y la compilación tuvo **0 advertencias/0 errores**. El payload contiene **495 archivos** y cinco capacidades. Falta comprobar físicamente la vista previa, las tres decisiones, un modelo real y el efecto audible/textual del prompt; la orientación no es una garantía de precisión.

La rebanada 8.6 está publicada en beta 14/secuencia 15. La suite enfocada de persistencia cifrada, límites, estados, presentación y contrato de publicación aprobó **12/12**; el conjunto Release completo aprobó **657/657** en serie y **657/657** con paralelismo predeterminado; los contratos de versión e instalador aprobaron **8/8**, el harness desechable **14/14** y la compilación tuvo **0 advertencias/0 errores**. El payload contiene **495 archivos** y cinco capacidades. Falta comprobar físicamente la creación, navegación por teclado, lector de pantalla y actualización visual de estados.

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
| Evidencia histórica de beta 6/secuencia 7 | Pruebas enfocadas **10/10**; Release serial/paralelo **451/451**; layout **495/495**. Fue una versión pública y se conserva como antecedente verificable |
| Pruebas y layout final de beta 7/secuencia 8 | Versión/instalador/instancia **10/10**; conjunto Release serial **520/520**; conjunto Release paralelo **520/520**; compilación Release **0 advertencias/0 errores**; `VE000 verified`. El layout final coincidió **495/495**, con `ProductVersion` `0.2.0-beta.7+25e3f36867250599ef5026d7270fc37af85a7c44`; quedaron 0 entradas prohibidas, rutas locales o CodeView y se conservaron las cinco capacidades |
| ZIP publicado de beta 7 | `Trazio-Asistente-Reunion-v0.2.0-beta.7-win-x64.zip`: **86,851,547 bytes**, SHA-256 `951ce1653c2bbdd0d5c0a0827cab5b3c6d5c5c762f1574a3434fa937c44d00e8`; tamaño y digest del recurso remoto coinciden con el artefacto verificado y el archivo lateral |
| Setup publicado de beta 7 | `Trazio-Asistente-Reunion-v0.2.0-beta.7-Setup.exe`: **60,024,844 bytes**, SHA-256 `fb476b57e82fbf696e886831029be17d221596e5aaf0fd8cddf808714e0fe526`; Authenticode `NotSigned`; SHA-256 del manifiesto del payload `8d585ce9438c9c3778b1a4eb1f8c4de0e9ae3924ed5110a4b624ea661da4a8a0`. Tamaño y digest remotos coinciden, pero nunca se ejecutó con identidad productiva |
| Manifiesto del payload beta 7 | `publish-manifest.json`: **123,575 bytes**, SHA-256 `8d585ce9438c9c3778b1a4eb1f8c4de0e9ae3924ed5110a4b624ea661da4a8a0`; **495** archivos, exactamente cinco capacidades y 0 entradas prohibidas, rutas locales o CodeView local |
| Pruebas y layout final de beta 8/secuencia 9 | Conjunto Release serial/paralelo **543/543**; filtro enfocado de beta 8 de cinco clases **48/48**; contratos finales **22/22**; harness desechable **14/14**. El filtro ampliado **61/61** queda como evidencia histórica de 8.2. El layout final coincidió **495/495**, con `ProductVersion` `0.2.0-beta.8+20c94272261f5697a548c56754039029b23f1548`, exactamente cinco capacidades y 0 entradas prohibidas, rutas locales o CodeView local |
| ZIP publicado de beta 8 | `Trazio-Asistente-Reunion-v0.2.0-beta.8-win-x64.zip`: **86,866,781 bytes**, SHA-256 `240a792ab8388a0511fb8b25ac466feeb104fdf6779b72938302adc9570b2e3f`; tamaño y digest del recurso remoto coinciden con el artefacto verificado y el archivo lateral |
| Setup publicado de beta 8 | `Trazio-Asistente-Reunion-v0.2.0-beta.8-Setup.exe`: **60,034,031 bytes**, SHA-256 `61d87a71e2a040f10c70fa05389ca341db79e5cdcd88b2e314dcaf00c88fcaa0`; Authenticode `NotSigned`. Tamaño y digest remotos coinciden, pero nunca se ejecutó con identidad productiva |
| Manifiestos publicados de beta 8 | `publish-manifest.json`: **84,443 bytes**, SHA-256 `4844b61d0d7dcb3139acbd8489909a017a818d0b0b9a03e4a54b12db12ea0bd3`; manifiesto del Setup: **455 bytes**, SHA-256 `12afe4ddbe9eb537683fae8d1c02ae6231292470a6efd77cb38a34c95386eeaf` |
| Pruebas y layout final de beta 9/secuencia 10 | Conjunto Release serial/paralelo **574/574**; suite enfocada de 8.3a **23/23**; contratos finales **22/22**; harness desechable **14/14**. El layout final coincidió **495/495**, con `ProductVersion` `0.2.0-beta.9+8eb4c2e5a16ff34db21a34bb1ff91feb93de7375`, exactamente cinco capacidades y 0 entradas prohibidas, rutas locales o CodeView local |
| ZIP publicado de beta 9 | `Trazio-Asistente-Reunion-v0.2.0-beta.9-win-x64.zip`: **86,876,029 bytes**, SHA-256 `64861c690b4f89dd9bf347fc970761c1c95be2bcc67a075ce24f6a0f63dca7bd`; tamaño y digest del recurso remoto coinciden con el artefacto verificado y el archivo lateral |
| Setup publicado de beta 9 | `Trazio-Asistente-Reunion-v0.2.0-beta.9-Setup.exe`: **60,037,793 bytes**, SHA-256 `3bfd4ae6777f18d1a59b379ee6bd42c515d6e13481ed19774c2c16fb67988635`; Authenticode `NotSigned`. Tamaño y digest remotos coinciden, pero nunca se ejecutó con identidad productiva |
| Manifiestos publicados de beta 9 | `publish-manifest.json`: **123,576 bytes**, SHA-256 `2b96f0f7082211edeef65608815b74e6d7e9fff1e1189117dc25c305a4b403a6`; manifiesto del Setup: **486 bytes**, SHA-256 `2457b68fbe5e87eaf75d7ec51c3c02148cd18ddbf811cbb832108a07ade1b40d` |
| Pruebas y layout final de beta 10/secuencia 11 | Conjunto Release serial/paralelo **599/599**; suite enfocada de 8.3b **48/48**; contratos finales **22/22**; harness desechable **14/14**; compilación Release **0 advertencias/0 errores**. El layout final coincidió **495/495**, con `ProductVersion` `0.2.0-beta.10+52f8b016c277a5822e9aec269fc22cc055925a2e`, exactamente cinco capacidades y 0 entradas prohibidas, rutas locales o CodeView local |
| ZIP publicado de beta 10 | `Trazio-Asistente-Reunion-v0.2.0-beta.10-win-x64.zip`: **86,897,741 bytes**, SHA-256 `52d5641af82e327bbfdf510dbd732d1dee7f13a7be5294af4b993fa1dc49a42f`; tamaño y digest del recurso remoto coinciden con el artefacto verificado y el archivo lateral |
| Setup publicado de beta 10 | `Trazio-Asistente-Reunion-v0.2.0-beta.10-Setup.exe`: **60,064,281 bytes**, SHA-256 `ce67f9ac2c05fa5718f99ef31339f74961af9de1a23a00c4a31ed56226977f2f`; Authenticode `NotSigned`. Tamaño y digest remotos coinciden, pero nunca se ejecutó con identidad productiva |
| Manifiestos publicados de beta 10 | `publish-manifest.json`: **123,577 bytes**, SHA-256 `f5fb80b5dce7b32dd478f2b062de3adf40d96a2db0dc254d8b6c4e1c6d6d81f4`; manifiesto del Setup: **488 bytes**, SHA-256 `f867fc695aa9de37e266621de825958f8ef9967be989cdfd2829c83d4a197a01`; seis recursos remotos **6/6** verificados |
| Beta 11 publicada con 8.4a | Filtro Release ampliado **77/77**; conjunto Release serial/paralelo **628/628**; contratos **22/22**; harness **14/14**; solución Release **0 advertencias/0 errores**. No constituye evidencia física de teclado, lector de pantalla, audio, cierre interactivo o fallo real de guardado |
| Beta 12 publicada con 8.4b | Revisión enfocada **35/35**; conjunto Release serial/paralelo **635/635**; contratos **22/22**; harness **14/14**; solución Release **0 advertencias/0 errores**; payload **495** archivos y seis recursos remotos verificados. No constituye evidencia física de interfaz, accesibilidad, audio o 2/5 horas |
| Beta 13 publicada con 8.5 | Suite enfocada **24/24**; conjunto Release serial/paralelo **645/645**; contratos **22/22**; harness **14/14**; solución Release **0 advertencias/0 errores**; payload **495** archivos y seis recursos remotos verificados. No constituye evidencia física de precisión, interfaz, accesibilidad, audio o 2/5 horas |
| Beta 14 publicada con 8.6 | Suite enfocada **12/12**; conjunto Release serial/paralelo **657/657**; contratos de versión e instalador **8/8**; harness **14/14**; solución Release **0 advertencias/0 errores**; payload **495** archivos y seis recursos remotos verificados. No constituye evidencia física de interfaz, accesibilidad, audio o 2/5 horas |
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
pwsh -NoProfile -File .\installer\package-portable.ps1
.\installer\build-installer.ps1
.\installer\test-installer.ps1
```

`publish.ps1` genera el payload combinado y un manifiesto determinista con rutas relativas normalizadas, longitudes y SHA-256. `package-portable.ps1` relee ese inventario, rechaza diferencias y reparse points, y crea exclusivamente las entradas declaradas con orden, fecha y metadatos normalizados; verifica el ZIP y su sidecar antes del reemplazo final. Ejecuta dos empaquetados sobre el mismo payload y exige identidad byte a byte dentro de la misma compilación exacta de PowerShell/.NET; no compares como requisito flujos DEFLATE creados por runtimes distintos. Comprueba que un fallo normal revierta el par anterior y que un fallo durante esa recuperación preserve el backup; un corte abrupto entre los dos reemplazos sigue siendo un límite físico y exige volver a empaquetar/verificar. El build productivo del Setup vuelve a ejecutar `publish.ps1`, exige payload/manifiesto/salida canónicos, comprueba cada archivo antes de compilar y relee todos los campos y formatos de `.sha256` y `.manifest.json`; el binario sigue sin firma. No se usa la palabra «determinista» para el `.exe` de Inno Setup porque no se ha probado un rebuild idéntico byte a byte. No ejecutes un instalador productivo para comprobar el harness: `test-installer.ps1` exige un `TestWorkspaceRoot` bajo `%TEMP%`, crea AppId, carpeta, registro, grupo, nombre de acceso directo y mutex distintos para cada corrida, usa únicamente payloads sintéticos y limpia ese estado desechable.

El harness automatiza instalación limpia, reparación, A→B, rechazo B→A, secuencia moderna cero, contradicción versión/secuencia, estado moderno parcial, rechazo de legacy desconocida, bloqueo por aplicación abierta, manipulación, rollback durante la fase transaccional y desinstalación. El nombre del shortcut desechable incluye el `runId`; se comprueba su eliminación y que `Trazio Asistente Reunión.lnk` del escritorio permanezca ausente o intacto. Verifica archivos arbitrarios fuera del árbol desechable del programa, pero no representa la raíz predeterminada/configurada de datos ni permite afirmar su supervivencia física. Un contrato estático separado comprueba que la definición no referencia esas rutas ni contiene operaciones de copia/borrado sobre ellas. La cancelación humana mediante la interfaz de Inno Setup no se simula de forma fiable: aparece explícitamente como `NOT_AUTOMATED` y requiere aceptación manual. `AppMutex` se comprueba con un holder vivo, pero sigue existiendo la carrera de iniciar App/Worker después del chequeo inicial; ese interleaving requiere validación física. La promesa de rollback termina cuando Setup completa; no cubre el primer arranque posterior ni compatibilidad hacia atrás de los datos.

Como evidencia histórica, beta 6/secuencia 7 aprobó las pruebas enfocadas **10/10**, Release serial/paralelo **451/451** y un layout **495/495**. Fue una versión pública y sus registros permanecen disponibles para trazabilidad.

Como antecedente histórico, beta 7/secuencia 8 aprobó Release serial y paralelo **520/520** y un layout **495/495** con `ProductVersion` `0.2.0-beta.7+25e3f36867250599ef5026d7270fc37af85a7c44`.

Como antecedente histórico, beta 8/secuencia 9 aprobó Release serial y paralelo **543/543**, el filtro enfocado de cinco clases **48/48**, los contratos finales **22/22**, el harness desechable **14/14** y un layout **495/495**. El filtro ampliado **61/61** se conserva como antecedente de 8.2.

Evidencia histórica publicada de beta 9/secuencia 10: Release serial y paralelo **574/574**, suite enfocada de 8.3a **23/23**, contratos finales **22/22** y harness desechable **14/14**. El ZIP coincide **495/495** con el payload y reporta `ProductVersion` `0.2.0-beta.9+8eb4c2e5a16ff34db21a34bb1ff91feb93de7375` en App, Worker y `VisualAnalysis`; mantiene exactamente cinco capacidades. El Setup permanece `NotSigned` y no fue ejecutado productivamente. El tag y los recursos remotos están publicados y verificados.

Evidencia publicada de beta 10/secuencia 11: Release serial y paralelo **599/599**, suite enfocada de 8.3b **48/48**, contratos finales **22/22**, harness desechable **14/14** y compilación Release con **0 advertencias/0 errores**. El ZIP coincide **495/495** con el payload y reporta `ProductVersion` `0.2.0-beta.10+52f8b016c277a5822e9aec269fc22cc055925a2e` en App, Worker y `VisualAnalysis`; mantiene exactamente cinco capacidades. El Setup permanece `NotSigned` y no fue ejecutado productivamente. El tag, la prerelease y los seis recursos remotos están publicados y verificados.

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
| Identidad / selección de ventana / revisión / glosario / Markdown | [LocalProfileTests](../tests/Trazio.AsistenteReunion.Tests/LocalProfileTests.cs), [MeetingWindowSelectionTests](../tests/Trazio.AsistenteReunion.Tests/MeetingWindowSelectionTests.cs), [ReviewStoreTests](../tests/Trazio.AsistenteReunion.Tests/ReviewStoreTests.cs), [GlossaryCandidateExtractorTests](../tests/Trazio.AsistenteReunion.Tests/GlossaryCandidateExtractorTests.cs), [GlossaryExchangeTests](../tests/Trazio.AsistenteReunion.Tests/GlossaryExchangeTests.cs), [GlossaryWorkspacePresentationTests](../tests/Trazio.AsistenteReunion.Tests/GlossaryWorkspacePresentationTests.cs), [GlossaryWorkspacePublicationTests](../tests/Trazio.AsistenteReunion.Tests/GlossaryWorkspacePublicationTests.cs), [ObsidianMarkdownExportTests](../tests/Trazio.AsistenteReunion.Tests/ObsidianMarkdownExportTests.cs) |
| Revisión pendiente 8.4a/8.4b | [BatchReviewStoreTests](../tests/Trazio.AsistenteReunion.Tests/BatchReviewStoreTests.cs), [BatchReviewPresentationTests](../tests/Trazio.AsistenteReunion.Tests/BatchReviewPresentationTests.cs), [BatchReviewPublicationTests](../tests/Trazio.AsistenteReunion.Tests/BatchReviewPublicationTests.cs) |
| Navegación / comparación / revisiones de inferencia y diccionario 8.5 | [SegmentAudioNavigatorTests](../tests/Trazio.AsistenteReunion.Tests/SegmentAudioNavigatorTests.cs), [TranscriptComparisonTests](../tests/Trazio.AsistenteReunion.Tests/TranscriptComparisonTests.cs), [HistoryRetranscriptionServiceTests](../tests/Trazio.AsistenteReunion.Tests/HistoryRetranscriptionServiceTests.cs), [GlossaryPromptPlannerTests](../tests/Trazio.AsistenteReunion.Tests/GlossaryPromptPlannerTests.cs), [GlossaryAssistedRetranscriptionTests](../tests/Trazio.AsistenteReunion.Tests/GlossaryAssistedRetranscriptionTests.cs) y [GlossaryAssistedRetranscriptionPublicationTests](../tests/Trazio.AsistenteReunion.Tests/GlossaryAssistedRetranscriptionPublicationTests.cs) |
| Notas y seguimiento 8.6 | [SegmentAnnotationStoreTests](../tests/Trazio.AsistenteReunion.Tests/SegmentAnnotationStoreTests.cs), [SegmentAnnotationPresentationTests](../tests/Trazio.AsistenteReunion.Tests/SegmentAnnotationPresentationTests.cs) y [SegmentAnnotationPublicationTests](../tests/Trazio.AsistenteReunion.Tests/SegmentAnnotationPublicationTests.cs) |
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
- [ ] Probar `0,75×`, `1×`, `1,25×`, `1,5×` y `2×` en pista completa, **Escuchar fragmento** e intervalo de comparación. Confirmar que la posición lógica no se multiplica dos veces, que el tono cambia de forma audible y que ningún alcance supera su final.
- [ ] Cambiar velocidad mientras reproduce y mientras está en pausa, también varias veces seguidas y cerca de una frontera entre fragmentos. Confirmar una sola salida activa, continuidad desde la posición fuente calculada a partir de los bytes informados por el dispositivo, pausa conservada y limpieza del resaltado al finalizar/detener.
- [ ] Cerrar y reabrir la aplicación; confirmar que la velocidad vuelve a `1×` y que no apareció configuración, capability ni dato nuevo en SQLite.
- [ ] Pausar durante el descifrado y en el cambio entre dos fragmentos; confirmar que el fragmento siguiente no comienza hasta pulsar **Continuar**.
- [ ] Desconectar o hacer fallar el dispositivo al pausar/continuar; confirmar error visible, detención completa y ausencia de cierre inesperado de la aplicación.
- [ ] Iniciar la preparación de un fragmento y eliminar la sesión; confirmar que **Eliminar sesión** permanece deshabilitado durante la operación, que el borrado bloquea nuevas reproducciones hasta terminar y que, tras confirmar por una vía ya iniciada, la cancelación termina antes del borrado.
- [ ] Escuchar una fila de una fuente distinta y confirmar que la fuente, la revisión, la forma de onda y el editor visibles no cambian; solo el resaltado corresponde a la fuente transitoria reproducida. Pausar/continuar/detener debe seguir disponible, pero los saltos y el deslizador deben quedar deshabilitados durante ese intervalo acotado.
- [ ] Repetir el flujo con teclado y lector de pantalla; confirmar nombres/ayuda de navegación y estado sin anuncios excesivos.
- [ ] Corregir/deshacer; guardar solo términos modificados del glosario; comparar original/nueva revisión sin sobrescrituras.
- [ ] En beta 10, abrir **Diccionario**; comprobar orden reciente, ordinal, filtro de términos/categoría y estados Todos/Activos/Inactivos. Activar/desactivar, reiniciar y confirmar persistencia sin cambios de texto; probar duplicados por separado, fallo visible sin lista parcial y navegación/cierre durante una carga.
- [ ] Probar una exportación JSON real y una importación real con datos no sensibles: confirmar advertencia de texto plano, vista previa completa, cancelación sin escrituras, clasificación de nuevas/duplicadas/conflictos/rechazadas, confirmación atómica y ausencia de ruta/nombre del archivo en almacenamiento. Repetir por teclado y con lector de pantalla.
- [ ] En una compilación desde `main`, abrir **Pendientes de revisión** con más de una reunión: confirmar reunión reciente primero, tiempo ascendente, total exacto, máximo 100 y aviso de truncamiento.
- [ ] Abrir un pendiente y confirmar sesión/fuente/segmento original exactos, selección única y ausencia de reproducción automática o resaltado de reproducción falso.
- [ ] Marcar un original como revisado, volverlo a pendiente y guardar una corrección: confirmar que la corrección y cualquier `Undo` lo mantienen fuera de la bandeja y que no se agrega diccionario automáticamente.
- [ ] Marcar al menos dos casillas sin abrir ni reproducir las filas; comprobar el resumen y cancelar sin escrituras. Repetir confirmando y comprobar aprobación de todos. Después provocar un cambio concurrente y verificar que el lote completo se rechace, sin correcciones ni entradas de diccionario.
- [ ] Editar sin guardar e intentar cambiar de segmento, sesión, pista, versión o pendiente: confirmar bloqueo accesible hasta guardar o descartar; cambiar de pestaña y detener una grabación sin perder el borrador; provocar un fallo de guardado y comprobar que el texto se conserva. Al cerrar, confirmar que el descarte requiere una decisión explícita.
- [ ] Repetir la bandeja 8.4a/8.4b por teclado y lector de pantalla: verificar foco, nombre/ayuda completos por fila y casilla y anuncio `Polite` de carga, vacío, truncamiento y error. Esta aceptación física sigue pendiente.
- [ ] Retranscribir una fuente completa y cancelar otra ejecución; las incompletas no son comparaciones exitosas.
- [ ] Con al menos dos términos activos, iniciar una retranscripción y comprobar la vista previa. Probar **Cancelar** sin nueva revisión, **No** con revisión sin diccionario y **Sí** con etiqueta de diccionario confirmado. Incluir un conflicto, más de 64 términos y un término largo; confirmar exclusiones y ausencia de reemplazos automáticos.
- [ ] En un fragmento original, agregar Nota, Decisión y Seguimiento; completar/reabrir el seguimiento, reiniciar y confirmar persistencia. Eliminar una anotación con confirmación, borrar luego la reunión y comprobar la cascada. Repetir por teclado y lector de pantalla, verificar el límite visual y que TXT/Markdown/Obsidian no incorporen anotaciones automáticamente.
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
