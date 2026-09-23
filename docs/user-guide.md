# Grabar, revisar y mantener el control de tus reuniones

**Comienza con una prueba breve y no sensible.** Trazio es una versión preliminar para Windows 11 x64 con interfaz en español. Necesitas una CPU x64 compatible, un micrófono/dispositivo de salida funcional y espacio en disco para el modelo y el audio cifrado. El ZIP publicado incluye el entorno de ejecución de .NET.

## Primera grabación

1. Descarga el ZIP completo de Windows desde la [versión publicada v0.2.0-beta.4](https://github.com/Andres-MMG/Trazio-Asistente-Reunion/releases/tag/v0.2.0-beta.4). Verifica el archivo lateral `.sha256`: el ZIP debe medir **86,823,005 bytes** y su SHA-256 debe ser `c08d6d6df3d986d19773c6a0d3723c587c7449a37b3d1d29ef601a936768c0d6`. Extráelo en una carpeta normal de aplicaciones; no lo ejecutes desde dentro del ZIP.
2. Abre `Trazio.AsistenteReunion.exe`. No lo separes del proceso auxiliar ni de las DLL. La beta sin firma puede generar advertencias de reputación de Windows; verifica el origen de la versión y su SHA-256 antes de decidir ejecutarla. No desactives el antivirus globalmente.
3. En **Sesión en vivo**, acepta o edita **Título de la reunión**. Es un título automático de reunión, no tu nombre; se puede renombrar después en Historial.
4. En **Perfil local**, confirma **Nombre visible**, ingresa opcionalmente una organización, marca **Confirmo este nombre visible** y **Guardar perfil**. **Tu nombre en esta reunión (opcional)** cambia solo la atribución del micrófono para esta grabación.
5. Opcionalmente, en **Aplicación de reunión**, pulsa **Seleccionar…**, luego **Actualizar lista**, elige una ventana superior y **Asociar**. Trazio mostrará Google Meet, Microsoft Teams u Otra aplicación.
6. Si deseas probar la captura visual efímera, pulsa **Autorizar captura visual** y revisa el diálogo. **Cancelar** es la opción predeterminada; la autorización sirve solo para esa ventana y esa sesión. Después de iniciar con **Audio del equipo**, beta 4 ofrece un segundo diálogo, **Autorizar análisis anónimo**. Es una autorización distinta, de un solo uso, ligada a la misma ventana y sesión; no se hereda ni se reactiva silenciosamente. La función todavía requiere validación física, por lo que debe evaluarse primero con contenido no sensible.
7. Selecciona el micrófono real y el dispositivo de salida utilizado por tu reunión. Usa auriculares para reducir la recaptura por el micrófono del sonido de los parlantes.
8. Haz clic en **Descargar modelo recomendado**, o inicia la transcripción para comenzar la configuración. La captura no comienza durante la configuración. Selecciona el idioma de transcripción.
9. Elige el presupuesto de retención de audio (1, 2 o 5 GB) y luego **Iniciar transcripción**. Las sesiones nuevas siempre conservan audio cifrado; ya no es una casilla opcional.
10. Comprueba los diagnósticos de ambas fuentes y las etiquetas de transcripción. Usa **Pausar**, **Reanudar** y **Detener** deliberadamente; espera la finalización antes de cerrar la aplicación.

**Importante:** Trazio captura independientemente de los controles de silencio de Meet/Teams/Zoom. Silenciarte en la reunión no silencia Trazio. Pausa Trazio cuando deba detenerse la grabación. Las notificaciones, la música y otras aplicaciones dirigidas a la salida seleccionada también pueden capturarse. Los permisos de grabación siguen siendo tu responsabilidad.

### Qué hace la asociación de ventana

La lista de ventanas se consulta solo después de pulsar **Actualizar lista**. Esta versión elige una ventana superior de Windows, no una pestaña individual. El título y la aplicación ayudan a elegir únicamente dentro del selector y se descartan al asociar o cerrar; fuera del modal solo quedan temporalmente el identificador técnico de la ventana, su PID y el proveedor. El historial guarda únicamente el proveedor normalizado. Asociar una ventana no inicia captura visual. `v0.2.0-beta.4` mantiene la captura y el análisis anónimo como autorizaciones separadas. El sondeo WGC/D3D11 trabaja con parches acotados y solo produce agregados; se conservan cifrados intervalos derivados de cobertura/actividad, nunca píxeles, imágenes o video. No hay OCR, reconocimiento de rostros, lectura de nombres, chat, subtítulos ni documentos.

Puedes iniciar sin seleccionar. Asociar una ventana tampoco cambia el origen de audio: Trazio sigue capturando el micrófono y/o el dispositivo de salida completos. Si la ventana desaparece antes de iniciar, la sesión continúa como **Sin seleccionar**; si desaparece durante la grabación, aparece un aviso y el audio continúa sin reasignación automática. Seleccionar, cambiar y quitar quedan bloqueados mientras se graba. Al detener, interrumpir o fallar la sesión, la asociación se consume y no se hereda en la siguiente reunión.

Durante una sesión autorizada, **Pausar visual**, **Reanudar visual** y **Detener visual** controlan únicamente WGC. La aplicación muestra un estado textual independiente y el indicador **Análisis visual activo · no se guardan imágenes**. Detenerlo consume las autorizaciones de esa sesión; no se reactiva silenciosamente. En beta 4, el análisis anónimo solo puede asociarse a segmentos de **Audio del equipo** (`SystemOutput`); nunca al micrófono.

En beta 4, los perfiles de producción de Google Meet y Microsoft Teams todavía están `Unvalidated`. Aunque autorices la función, el procesamiento se abstiene y la evidencia de actividad se muestra como **No disponible** tanto en la sesión en vivo como en Historial. Esto es intencional: no hay identificación de hablantes y no cambia el texto transcrito, `SpeakerName`, TXT, Markdown ni la exportación a Obsidian. La validación física de WGC/GPU, interfaz, teclado/lector de pantalla, accesibilidad, Meet/Teams reales y sesiones de 2/5 horas sigue pendiente.

## Configuración del modelo

- El catálogo recomienda **Whisper Base multilingüe**, aproximadamente 148 MB. Una vez verificado y almacenado en caché, se selecciona automáticamente. También se puede proporcionar un modelo de catálogo verificado en una carpeta `models` junto al ejecutable.
- Las descargas ocurren solo después de una acción explícita de configuración/inicio, con progreso y cancelación. El inicio de la aplicación no descarga por sí mismo. Una descarga fallida/cancelada elimina el archivo temporal y se puede reintentar.
- **Opciones avanzadas: modelo personalizado** acepta un GGML `.bin` compatible. Se preservan las selecciones personalizadas existentes; su autenticidad es tu responsabilidad y el proceso auxiliar comprueba la compatibilidad al cargarlo.
- Un modelo de catálogo dañado se rechaza en vez de sobrescribirse silenciosamente. No se envía audio/texto de reuniones al proveedor de descarga del modelo.

La revisión, longitud y SHA-256 fijados se documentan en [autenticidad del modelo](security.md#autenticidad-del-modelo).

## Escuchar la fuente correcta

Abre **Historial**, selecciona una reunión guardada y usa **Reproductor de la reunión**:

1. Selecciona **Micrófono** o **Audio del equipo**. El nombre de la persona local corresponde al micrófono; no identifica a los participantes remotos.
2. Usa **Reproducir**, pausa, el deslizador de la línea de tiempo o **Retroceder 10 s / Avanzar 10 s**. La forma de onda representa el audio conservado de esa fuente.
3. Usa **Escuchar fragmento** en una fila de transcripción para oír la fuente y el intervalo correspondientes. Seleccionar texto para editarlo no autoriza a iniciar automáticamente la reproducción.
4. Una fuente sin audio conservado no puede reproducirse ni exportarse a WAV. La transcripción puede seguir disponible aunque su audio falte o haya sido eliminado por retención.

El reproductor maneja las fuentes por separado. Esta versión no incluye una pista mezclada de micrófono y equipo, control de velocidad ni eliminación automática de eco. Las marcas de tiempo provienen del reconocimiento y pueden necesitar interpretación humana; reproducir no garantiza que las palabras del modelo sean correctas.

## Corregir texto y recopilar terminología

1. Selecciona **Original · revisión humana**, luego un segmento, y compara **Texto original del modelo** con **Texto corregido**. Las revisiones de salida del modelo son de solo lectura en esta versión.
2. Edita el texto y elige **Guardar corrección**. El original se conserva; **Deshacer corrección** anexa una operación de deshacer en vez de eliminar la evidencia histórica.
3. Revisa los reemplazos de términos detectados. Por ejemplo, cambiar dos nombres debería proponer `Need → Meet` y `NTeams → Teams`, no toda la oración.
4. Después de guardar una corrección, selecciona las sugerencias que convenga conservar y agrégalas al diccionario. El texto sin cambios no es una regla útil de glosario.

**Límite actual:** el glosario registra terminología confirmada y su procedencia. Todavía no cambia las instrucciones de Whisper ni reemplaza palabras automáticamente. Guardar correcciones no entrena el modelo de voz. La aplicación futura del glosario pertenece a la etapa 6; el entrenamiento, a la etapa 12.

## Retranscribir y comparar versiones

- Selecciona la fuente conservada y usa **Retranscribir audio**. Configura el modelo/idioma deseado en los ajustes de sesión en vivo antes de iniciar la ejecución.
- La sesión debe estar detenida. La secuencia de fragmentos conservados debe estar completa desde la secuencia cero; no se pueden retranscribir fuentes parcialmente eliminadas como si no faltara nada.
- Se crea una nueva revisión de salida del modelo. Se preservan el original y las correcciones humanas. Las ejecuciones fallidas/canceladas no se presentan como resultados exitosos.
- Abre **Comparar transcripciones**, elige dos versiones elegibles de la misma fuente y compara. Cada fila agrupa el texto por su hora de inicio en un intervalo de 15 segundos; **Escuchar** reproduce el intervalo de la fuente correspondiente.
- Las diferencias muestran qué cambió, **no qué versión es más precisa**. Escucha antes de aceptar una redacción.

## Almacenamiento y exportaciones

La carpeta predeterminada es `%LOCALAPPDATA%\Trazio Asistente Reunion`. **Historial → Almacenamiento local** muestra la ruta activa exacta y ofrece **Abrir carpeta**, **Copiar ruta** y **Cambiar carpeta…**.

| Contenido | Ubicación / comportamiento |
|---|---|
| Datos de reuniones | `trazio-transcripts.db` y archivos auxiliares de SQLite; contenido sensible cifrado, metadatos estructurales visibles |
| Sonido conservado | `audio\`, fragmentos cifrados; no se pueden abrir directamente como WAV comunes |
| Clave y preferencias | `master.key`, `settings.dat`, protegidos para la cuenta actual de Windows |
| Modelo de voz | `models\`; los modelos no son secretos cifrados |
| Exportaciones TXT / Markdown / WAV | Sin cifrar, solo en la ruta exacta que elijas explícitamente |

La retención de audio es global entre reuniones. La limpieza puede eliminar el audio más antiguo de sesiones completadas/interrumpidas manteniendo su transcripción; las grabaciones activas están protegidas de esa limpieza. El presupuesto seleccionado **no es un límite estricto de disco** para una grabación activa. Supervisa el espacio libre durante pruebas largas.

**Exportar TXT** escribe la transcripción original de la sesión con sus correcciones humanas efectivas (no la comparación de revisión del modelo seleccionada). **Exportar a Obsidian…** usa el mismo texto efectivo y crea una nota `.md` con título, tiempos de la sesión, estado, marcas de tiempo, hablante y fuente. La actividad visual anónima no cambia esos hablantes ni se agrega a TXT/Markdown/Obsidian. **Exportar WAV** escribe la fuente conservada seleccionada, no una pista mezclada.

Para Obsidian no necesitas entregar ni configurar la bóveda: elige con el diálogo de guardado una carpeta dentro de ella. Trazio no memoriza esa ruta en esta primera integración, no copia audio y no incluye rutas ni identificadores internos. La nota comienza con frontmatter YAML y también funciona como Markdown genérico.

Las tres exportaciones están sin cifrar y fuera de la gestión de retención/migración de Trazio. Una bóveda sincronizada puede enviar la nota a servicios externos. Mantenlas privadas. Eliminar una sesión borra sus datos almacenados y audio conservado, pero no los archivos exportados previamente.

### Cambiar la carpeta de datos de forma segura

1. Detén grabación/reproducción/retranscripción y elige **Cambiar carpeta…**.
2. Elige una carpeta principal admitida en una unidad fija local disponible. Trazio crea su subcarpeta `Trazio Asistente Reunion`.
3. Reinicia la aplicación. El traslado ocurre antes de abrir los datos: copiar, vaciar búferes, verificar longitud/SHA-256 y después confirmar la nueva ubicación y limpiar archivos de origen verificados.
4. Reabre Historial y confirma la ruta activa y tus reuniones.

Se rechazan destinos de red/UNC, unidades extraíbles, raíz de unidad, subárbol de instalación, origen/destino anidados, espacio insuficiente, sin permiso de escritura o no vacíos con contenido ajeno. No se sobrescriben archivos conflictivos. Una unidad personalizada ausente provoca un fallo claro de inicio, **no** una base de datos vacía silenciosa. Los traslados/limpiezas interrumpidos se reanudan; un traslado todavía no confirmado se puede cancelar siguiendo las indicaciones de la aplicación.

Esto traslada datos para el mismo usuario de Windows. No hace portátiles los datos cifrados a otra cuenta o equipo. Consulta [seguridad y límites de recuperación](security.md).

## Actualizar la instalación ZIP

**Todavía no hay actualizador automático. No actualices reemplazando solo un EXE.**

1. Finaliza y cierra Trazio normalmente. No fuerces el cierre de una reunión activa.
2. Descarga un ZIP completo de una versión y verifica su suma de comprobación con el archivo de comprobación de esa versión.
3. Extrae en una carpeta limpia de aplicación, separada de los datos. Conserva la carpeta anterior hasta comprobar la nueva versión; no mezcles DLL de distintas versiones.
4. Inicia la nueva aplicación con la misma cuenta de Windows. Confirma versión, reuniones existentes, modelo y una prueba breve de grabación/reproducción antes de eliminar la carpeta anterior de la aplicación.

Los datos existentes están fuera de la carpeta normal de aplicación. Esa separación no es una garantía probada de compatibilidad futura al degradar/revertir el esquema: lee las notas de cada versión. No elimines la raíz de datos ni `master.key` durante la limpieza. El respaldo/restauración cifrado portátil y la reversión automática no están implementados.

## Solución de problemas

| Síntoma | Comprobación / siguiente acción |
|---|---|
| “Falta el proceso de transcripción” | Vuelve a extraer el paquete completo. Verifica que `Trazio.AsistenteReunion.Worker.exe` esté junto a la aplicación. Revisa la cuarentena de seguridad sin desactivar ampliamente la protección. |
| “No hay audio conservado” | Confirma la fuente. Las sesiones antiguas con grabación opcional o los fragmentos eliminados por retención no tienen sonido original recuperable; el texto solo no puede reconstruirlo. |
| El micrófono funciona, el sonido remoto no | Confirma que la reunión se reproduce por el dispositivo de salida seleccionado en Trazio. Asociar una ventana no aísla ni redirige su audio. Revisa los diagnósticos de cada fuente, no solo un medidor en movimiento. |
| “[Música]”, habla duplicada o palabras imprecisas | Comprueba qué fuente se reproduce, usa auriculares y compara con el audio. Puede haber ruido/eco/errores del modelo; la etiqueta no es un clasificador fiable de contenido. |
| La captura/transcripción se pausa o crece el trabajo pendiente | Revisa el error de la fuente y el estado del proceso auxiliar. No supongas que todo el audio aceptado se convirtió en texto. Detén de forma segura e inspecciona el audio conservado antes de reintentar. |
| La unidad personalizada de datos no está disponible | Reconecta la unidad local configurada; sigue la guía de recuperación al inicio. No crees manualmente otra base de datos vacía. |
| El modelo no carga | Verifica la descarga/compatibilidad, selecciona un modelo admitido y confiable, y conserva completo el paquete del proceso auxiliar. |
| Un fallo interrumpió una reunión | Reabre con la misma cuenta de Windows. Acepta explícitamente la recuperación de audio pendiente si se ofrece. La captura no se reinicia automáticamente y se puede perder el último fragmento parcial del archivo. |

Al informar un problema, comparte versión de la aplicación, versión de Windows, tipo de fuente/dispositivo, pasos, tiempos y error sin información sensible. **No** publiques transcripciones reales, audio exportado, bases de datos, archivos de clave/configuración ni capturas con información de participantes en una incidencia pública.
