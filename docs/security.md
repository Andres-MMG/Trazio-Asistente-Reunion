# Límites de seguridad y privacidad

**La prioridad local no significa invulnerabilidad, ausencia de metadatos ni portabilidad.** La aplicación actual ejecuta inferencia local y protege contenido sensible almacenado. No protege una sesión de Windows desbloqueada contra malware que ya se ejecuta como ese usuario.

Este documento describe los límites frente a amenazas de la implementación; no es una certificación de seguridad ni una promesa de cumplimiento normativo.

## Inventario de datos

| Activo | Tratamiento en reposo | Exposición restante |
|---|---|---|
| Títulos de sesión, nombres locales de hablantes, texto transcrito | Contenido AES-256-GCM antes de insertarse en SQLite | Descifrado en memoria/interfaz durante el uso |
| Correcciones, nombres de editores, texto/categoría del glosario, contenido de revisiones del modelo | Contenido protegido en almacenes de revisión | Estructura de auditoría, ID, tiempos y relaciones visibles |
| PCM pendiente | Contenido cifrado en SQLite | Búferes temporales sin cifrar durante el procesamiento |
| Audio conservado | WAV PCM16 mono 16 kHz dentro de archivos de fragmentos cifrados por fuente | Descifrado en memoria acotada para reproducción/retranscripción/exportación |
| Clave maestra | Clave de 256 bits protegida por Windows DPAPI `CurrentUser` en `master.key` | Depende de la protección de cuenta/clave de Windows |
| Preferencias / perfil local | DPAPI `CurrentUser` en `settings.dat` | Visible para la aplicación autorizada en ejecución |
| Selector de ventana de reunión | El título y el nombre de proceso existen solo en el candidato mostrado dentro del modal y se descartan al asociar o cerrar | Esas señales transitorias están expuestas al límite de memoria del selector mientras permanece abierto |
| Asociación activa y proveedor normalizado | Fuera del modal solo se mantienen temporalmente HWND, PID y enum; SQLite conserva únicamente el enum sin cifrado | Revela `Sin seleccionar`, `Google Meet`, `Microsoft Teams` u `Otra aplicación`; no conserva título, URL, PID, HWND ni proceso. La asociación se consume en toda finalización |
| Captura visual efímera 7.2a | OFF por defecto; autorización separada y no persistida por sesión. Los fotogramas WGC no se serializan ni se retienen | El contenido existe transitoriamente en memoria gráfica mientras Windows y Trazio entregan/procesan/cierran el fotograma; no hay garantía frente a inspección del proceso, paginación o volcados externos |
| Evidencia visual anónima 7.2b | Segunda autorización, de un solo uso y ligada a ventana/sesión/alcance. Solo persiste intervalos derivados cifrados de cobertura/actividad y procedencia versionada; eliminación en cascada con la sesión | Los agregados se calculan transitoriamente desde parches D3D11 acotados. Meet/Teams permanecen `Unvalidated`, por lo que el procesamiento se abstiene y presenta **No disponible** |
| Corpus y golden de evaluación | Solo agregados sintéticos sin píxeles, nombres, títulos, URL, PID, HWND, texto de reunión ni marcas de tiempo reales; se procesan offline | Son datos de regresión del repositorio, no evidencia física ni datos de producción, y no se incluyen en el paquete distribuible |
| Esquema SQLite y metadatos operativos | Sin cifrado de archivo completo | ID, marcas de tiempo, enumeraciones de fuente/proveedor, secuencias, rutas/tamaños y estructura de base de datos |
| Archivos de modelo | Sin cifrar; modelo de catálogo autenticado por tamaño/hash esperado | Los modelos personalizados aportados por el usuario no son autenticados por ese catálogo |
| Manifiestos de programa/instalador | Rutas relativas de binarios, longitudes, versión/secuencia y SHA-256; no contienen datos de reuniones | Sin firma, verifican integridad respecto del sidecar observado, no autenticidad del editor/canal |
| Exportaciones TXT, Markdown y WAV | Sin cifrar en una ruta elegida por el usuario | Fuera de los controles de cifrado, retención y eliminación de la aplicación; una bóveda sincronizada puede enviarlas a servicios externos |

Fuentes: [Crypto.cs](../src/Trazio.AsistenteReunion.Core/Crypto.cs), [SettingsStore.cs](../src/Trazio.AsistenteReunion.Core/SettingsStore.cs), [almacén de sesiones](../src/Trazio.AsistenteReunion.Core/SqliteSessionStore.cs), [almacén de revisión](../src/Trazio.AsistenteReunion.Core/SqliteReviewStore.cs), [revisiones del modelo](../src/Trazio.AsistenteReunion.Core/SqliteModelRevisionStore.cs), [almacén de audio](../src/Trazio.AsistenteReunion.Core/AudioArchiveStore.cs).

## Modelo de protección

- AES-GCM utiliza una clave de 32 bytes, nonces aleatorios de 12 bytes y etiquetas de autenticación de 16 bytes. Los datos asociados específicos de cada registro vinculan el texto cifrado con su propósito previsto.
- El canal con nombre del proceso auxiliar local está restringido al usuario actual de Windows. IPC no requiere un puerto de red abierto. Se limpian búferes en varias rutas de procesamiento/transporte, pero esto no garantiza protección contra inspección de procesos, archivos de paginación, volcados de fallos ni captura a nivel del sistema operativo.
- La aplicación escribe fragmentos de audio cifrados antes de mover atómicamente los archivos `.partial` a su ubicación definitiva. La reconciliación al inicio elimina archivos incompletos/sin referencia y metadatos obsoletos.
- La reproducción descifra un fragmento a la vez sin crear un WAV temporal sin cifrar en el almacenamiento de la aplicación. La exportación explícita es deliberadamente una operación distinta.
- La búsqueda 8.2 no crea FTS, índice, columna, archivo ni caché de consultas/resultados. Lee los blobs cifrados en orden, autentica y descifra en memoria, usa solo el texto efectivo original/corregido y conserva como máximo 100 resultados. Una etiqueta AES-GCM inválida aborta la búsqueda completa; no se silencian filas corruptas ni se publican resultados parciales como correctos. La consulta y los fragmentos descifrados sí existen transitoriamente en RAM administrada: no se promete borrado seguro ni protección frente a paginación, volcados o software con el mismo acceso de usuario.
- El diccionario global 8.3a descifra todas las entradas antes de publicar una lista y filtra términos/categorías en memoria con un máximo de 120 filas visibles. No crea FTS, índice, caché, registro ni preferencia con el filtro o sus resultados. Una entrada corrupta aborta la carga completa. Activar/desactivar modifica solo el metadato `is_active`; los blobs cifrados y su procedencia no cambian. Una entrada activa queda preparada para una aplicación futura: beta 9 todavía no modifica Whisper ni las transcripciones. Esos textos descifrados existen transitoriamente en RAM con los mismos límites frente a paginación, volcados o software del usuario.
- La clasificación de ventana usa señales conservadoras del título transitorio y nombre de proceso. Una coincidencia identifica un proveedor probable, no demuestra que exista una reunión activa ni quién habla. Señales contradictorias o insuficientes se reducen a `Otra aplicación`.
- La captura visual 7.2a exige una autorización distinta de asociar la ventana. El análisis anónimo 7.2b exige además otra autorización explícita, consumible una sola vez y ligada al HWND/PID, proveedor, sesión y alcance exactos. Pausar o detener lo visual no detiene el audio; un fallo visual se degrada sin reasignar otra ventana.
- La ruta 7.2b no conserva píxeles, imágenes o video. No ejecuta OCR ni reconoce rostros, nombres, chat, subtítulos o documentos. El extractor D3D11 solo entrega agregados numéricos acotados y el almacenamiento cifra los intervalos derivados de cobertura/actividad; las exportaciones TXT/Markdown/Obsidian no incluyen esa evidencia y permanecen sin cambios.
- La correlación está limitada a segmentos `SystemOutput`; el micrófono queda oculto para esta presentación. Sin una política de perfil validada, o ante evidencia ausente/corrupta/incompatible, las vistas en vivo e Historial muestran **No disponible**. Esto no escribe `SpeakerName`, no cambia la transcripción y no identifica hablantes.
- El script de publicación exige el ensamblado puro y sin paquetes `VisualAnalysis.dll`, con la misma versión de producto que App y Worker. Rechaza datos de reuniones, modelos, imágenes, video, volcados, registros y material de claves, además de los ejecutables/metadatos de `VisualEvaluation`, el corpus/golden sintético y directorios `tools`/`evaluation` en cualquier nivel. Esta comprobación reduce una inclusión accidental en el paquete; no impide volcados externos del sistema operativo ni reemplaza la inspección del recurso final.
- El instalador manual usa una raíz de programa distinta de la raíz de datos y payloads completos versionados. Su definición no referencia ni declara operaciones sobre DB, audio, modelos, ajustes, claves o la carpeta personalizada. App y Worker sostienen un mutex común; Setup bloquea si están activos durante el chequeo inicial y tiene deshabilitados cierre/reinicio de aplicaciones. Como App/Worker no adquieren `SetupMutex`, todavía existe una carrera si se abren después de ese chequeo; deben permanecer cerrados hasta finalizar Setup.

### Fuera de la protección

El malware del mismo usuario, administradores con acceso efectivo a su entorno, grabación de pantalla/audio, credenciales de Windows comprometidas, respaldos inseguros, capturas y exportaciones explícitas quedan fuera de esta protección. Otra aplicación puede grabar la reproducción de los parlantes. El cifrado **no es DRM** y no puede garantizar que el audio nunca salga de Trazio.

Esta beta no ofrece respaldo/restauración portátil con contraseña, recuperación organizacional de claves, flujo de rotación de claves, auditoría externa de seguridad ni afirmación formal de cumplimiento. Perder el contexto de descifrado del usuario de Windows o `master.key` puede volver ilegible el contenido histórico. No elimines/recrees la clave para “reparar” datos cifrados existentes.

## Red y autenticidad del modelo

El flujo de inferencia distribuido no tiene alternativa en la nube y no envía audio/transcripciones de reuniones a un proveedor. La configuración del modelo requiere una acción deliberada del usuario; realiza una descarga HTTPS del modelo, no una subida de transcripción. Como en cualquier descarga, el servicio de alojamiento puede observar metadatos de red/solicitud. El inicio de la aplicación no es una operación de descarga en segundo plano.

### Autenticidad del modelo

El catálogo de [WhisperModelStore.cs](../src/Trazio.AsistenteReunion.Core/WhisperModelStore.cs) fija:

| Campo | Valor |
|---|---|
| Repositorio | `ggerganov/whisper.cpp` en Hugging Face |
| Revisión | `5359861c739e955e79d9a303bcbc70fb988958b1` |
| Archivo | `ggml-base.bin` |
| Longitud | `147951465` bytes |
| SHA-256 | `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe` |

Se verifican los archivos de catálogo descargados e incluidos. Un modelo personalizado manual sigue siendo aportado por el usuario; cargarlo correctamente demuestra compatibilidad, no procedencia confiable. El proceso de retranscripción registra un hash del modelo para reproducibilidad, no como certificado general de confianza. La licencia del modelo es independiente de la licencia todavía no seleccionada para el código de la aplicación; conserva los avisos correspondientes.

Las futuras conexiones opcionales a LLM/Jev externos, plataforma/calendarios cambiarán el límite de privacidad. **No son integraciones actuales** y deben requerir consentimiento explícito, protección de credenciales, minimización de datos y comportamiento ante fallos antes de implementarse.

La existencia del código 7.2b no sustituye la validación física de WGC/GPU, accesibilidad, Meet/Teams reales ni las pruebas de 2/5 horas. Hasta completarla, los perfiles de producción permanecen `Unvalidated` y no deben habilitarse mediante configuración documental o de empaquetado.

La versión pública actual es [`v0.2.0-beta.9`](https://github.com/Andres-MMG/Trazio-Asistente-Reunion/releases/tag/v0.2.0-beta.9). Su layout final **495/495**, con `ProductVersion` `0.2.0-beta.9+8eb4c2e5a16ff34db21a34bb1ff91feb93de7375`, incluye `VisualAnalysis.dll` requerida/versionada y excluye evaluador, corpus/golden y directorios `tools`/`evaluation`; conserva exactamente cinco capacidades y cero hallazgos prohibidos, rutas locales o CodeView. El tag resuelve a `8eb4c2e5a16ff34db21a34bb1ff91feb93de7375`. Esta evidencia no cambia `Unvalidated`, **No disponible** ni la ausencia de identificación de hablantes.

El ZIP publicado mide **86,876,029 bytes** y su SHA-256 es `64861c690b4f89dd9bf347fc970761c1c95be2bcc67a075ce24f6a0f63dca7bd`. El Setup publicado mide **60,037,793 bytes**, su SHA-256 es `3bfd4ae6777f18d1a59b379ee6bd42c515d6e13481ed19774c2c16fb67988635` y Authenticode informa `NotSigned`. El manifiesto de publicación mide **123,576 bytes** y su SHA-256 es `2b96f0f7082211edeef65608815b74e6d7e9fff1e1189117dc25c305a4b403a6`; el manifiesto del Setup mide **486 bytes** y su SHA-256 es `2457b68fbe5e87eaf75d7ec51c3c02148cd18ddbf811cbb832108a07ade1b40d`. Los tamaños y digest remotos coinciden con los artefactos verificados y sus sidecars. Esto prueba integridad respecto de esos valores, no autenticidad del editor ni una instalación real: el Setup con identidad productiva no se ejecutó, la cancelación humana continúa `NOT_AUTOMATED` y sigue pendiente la validación en otra máquina o cuenta.

## Integridad del instalador y límite de rollback

`publish.ps1` ordena de forma ordinal todas las rutas relativas del payload y registra longitud/SHA-256 en un manifiesto determinista. El build productivo vuelve a publicar, solo acepta las rutas canónicas y comprueba el conjunto exacto antes de compilar. Después relee nombre, longitud, versión, secuencia, hashes, referencia al manifiesto del payload, estado Authenticode y formato de ambos sidecars. Inno Setup también verifica los datos comprimidos que extrae. No se afirma que dos compilaciones del `.exe` sean idénticas byte a byte. Estas capas detectan corrupción o manipulación respecto de los manifiestos comparados, pero el instalador **no tiene firma Authenticode**: un atacante que reemplace tanto binario como sidecars puede producir valores coherentes. Obtén los recursos solo del canal oficial y compara la evidencia publicada por un canal confiable.

La actualización es manual y offline. Estado moderno incompleto, secuencia cero, versión/secuencia contradictorias, una secuencia instalada mayor o una versión legacy desconocida se rechazan antes de copiar. Un fallo/cancelación durante Setup conserva el payload y la activación anteriores; ese rollback no cubre una ejecución posterior ni cambios de esquema que una versión nueva pueda realizar al abrir los datos. La definición de desinstalación retira programa/accesos directos/registro de instalación y no declara la raíz de datos; su comprobación física con una copia de datos existentes sigue pendiente. El borrado de versiones antiguas, la firma y la autoactualización requieren unidades futuras separadas.

App y Worker sostienen un mutex de actividad real durante su vida; Setup no fuerza cierres ni reinicios y bloquea si detecta una instancia en el chequeo inicial. Persiste una carrera residual: App/Worker podrían iniciar después de ese chequeo, por lo que deben mantenerse cerrados hasta finalizar Setup. El contrato evita que el instalador lea, copie, migre o borre datos de usuario; el harness confirma su aislamiento en workspaces e identidades desechables, pero no prueba físicamente la supervivencia de datos arbitrarios.

## Retención, eliminación y límites ante fallos

- Cada sesión **nueva** conserva audio cifrado, incluso al cargar configuraciones anteriores con retención desactivada. Siguen siendo necesarios un inicio explícito y un indicador persistente.
- La retención global ofrece 1/2/5 GB. La limpieza elimina los fragmentos elegibles más antiguos de sesiones completadas/interrumpidas y preserva transcripciones. No elimina grabaciones activas ni escrituras pendientes; por tanto, el presupuesto seleccionado no es un tope estricto durante una grabación activa.
- El audio pendiente de transcripción es distinto del archivo conservado. El trabajo pendiente elegible vence después de 24 horas; se excluyen las sesiones `Recording` activas. El inicio primero normaliza los estados de grabación obsoletos dejados por fallos a `Interrupted`.
- Un fallo puede perder el fragmento final no confirmado del archivo, como máximo 30 segundos por fuente habilitada. Los fragmentos confirmados pueden seguir disponibles; no se garantiza recuperación de cada muestra aceptada.
- Eliminar una sesión borra primero registros de base de datos y después los archivos asociados; la reconciliación gestiona restos. Es eliminación lógica, no borrado seguro certificado de SSD, respaldos o instantáneas del sistema de archivos.
- Una exportación cancelada/fallida puede dejar un archivo incompleto **sin cifrar** en el destino elegido. La nota Markdown no contiene audio, rutas ni identificadores internos, pero sí contiene el texto, hablantes y tiempos de la reunión. Trata incluso las exportaciones incompletas y las bóvedas sincronizadas como sensibles.

## Seguridad de la migración de almacenamiento

[StorageLocation.cs](../src/Trazio.AsistenteReunion.Core/StorageLocation.cs) guarda un documento localizador versionado en `HKCU\Software\Trazio\AsistenteReunion`: raíz activa, traslado pendiente, manifiesto de archivos administrados e intención de limpieza. Contiene rutas, longitudes y hashes SHA-256, **no claves ni contenido de reuniones**.

Los traslados se ejecutan antes de abrir almacenes. Los archivos se copian mediante flujos acotados, se vacían los búferes y se verifican antes de confirmar la raíz activa. La limpieza solo elimina archivos de origen incluidos en el manifiesto cuyo destino sigue coincidiendo en longitud/hash. La copia/limpieza interrumpida se reanuda; los archivos ajenos y los `.tmp`/`.partial` excluidos no son eliminados por la limpieza de migración. El contenido conflictivo del destino detiene la operación en vez de sobrescribirse.

Solo se aceptan destinos admitidos en unidades fijas locales. El almacenamiento personalizado ausente falla de forma segura con instrucciones de recuperación. La protección de la clave sigue vinculada al mismo usuario de Windows después del traslado. Consulta el [flujo de uso](user-guide.md#cambiar-la-carpeta-de-datos-de-forma-segura).

## Informar problemas de seguridad de forma segura

No incluyas contenido de reuniones ni credenciales en incidencias públicas. Informa una descripción mínima sin datos sensibles y solicita un canal privado con el mantenedor para evidencia sensible; todavía no existe un servicio dedicado de informes de seguridad ni un SLA de respuesta. Nunca adjuntes `master.key`, `settings.dat`, copias de bases de datos ni grabaciones reales a un informe público.
