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
| Esquema SQLite y metadatos operativos | Sin cifrado de archivo completo | ID, marcas de tiempo, enumeraciones de fuente, secuencias, rutas/tamaños y estructura de base de datos |
| Archivos de modelo | Sin cifrar; modelo de catálogo autenticado por tamaño/hash esperado | Los modelos personalizados aportados por el usuario no son autenticados por ese catálogo |
| Exportaciones TXT, Markdown y WAV | Sin cifrar en una ruta elegida por el usuario | Fuera de los controles de cifrado, retención y eliminación de la aplicación; una bóveda sincronizada puede enviarlas a servicios externos |

Fuentes: [Crypto.cs](../src/Trazio.AsistenteReunion.Core/Crypto.cs), [SettingsStore.cs](../src/Trazio.AsistenteReunion.Core/SettingsStore.cs), [almacén de sesiones](../src/Trazio.AsistenteReunion.Core/SqliteSessionStore.cs), [almacén de revisión](../src/Trazio.AsistenteReunion.Core/SqliteReviewStore.cs), [revisiones del modelo](../src/Trazio.AsistenteReunion.Core/SqliteModelRevisionStore.cs), [almacén de audio](../src/Trazio.AsistenteReunion.Core/AudioArchiveStore.cs).

## Modelo de protección

- AES-GCM utiliza una clave de 32 bytes, nonces aleatorios de 12 bytes y etiquetas de autenticación de 16 bytes. Los datos asociados específicos de cada registro vinculan el texto cifrado con su propósito previsto.
- El canal con nombre del proceso auxiliar local está restringido al usuario actual de Windows. IPC no requiere un puerto de red abierto. Se limpian búferes en varias rutas de procesamiento/transporte, pero esto no garantiza protección contra inspección de procesos, archivos de paginación, volcados de fallos ni captura a nivel del sistema operativo.
- La aplicación escribe fragmentos de audio cifrados antes de mover atómicamente los archivos `.partial` a su ubicación definitiva. La reconciliación al inicio elimina archivos incompletos/sin referencia y metadatos obsoletos.
- La reproducción descifra un fragmento a la vez sin crear un WAV temporal sin cifrar en el almacenamiento de la aplicación. La exportación explícita es deliberadamente una operación distinta.

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