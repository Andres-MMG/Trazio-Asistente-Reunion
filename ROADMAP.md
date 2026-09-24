# Trazio Asistente Reunión — Hoja de ruta del producto

- Fecha del estado: 2026-09-24
- Madurez actual: MVP funcional avanzado / versión preliminar pública
- Versión publicada actual: [`0.2.0-beta.6`](https://github.com/Andres-MMG/Trazio-Asistente-Reunion/releases/tag/v0.2.0-beta.6), secuencia de instalador **7**

Este es el plan canónico de etapas. **La beta 6/secuencia 7 publica la etapa 5.1: instalador manual offline por usuario, versionado y fail-safe. El ZIP y el Setup remotos están verificados, pero el Setup permanece sin firma y no se ejecutó con identidad productiva. Siguen pendientes cancelación humana, validación física en otra máquina o cuenta y autoactualización.** La etapa 6 tiene una línea base funcional implementada; 7.1a, 7.2a y la infraestructura de 7.2b están publicadas, todavía pendientes de validación física. Beta 6 distribuye el ensamblado compartido puro y sin paquetes `VisualAnalysis`; el evaluador offline, el corpus sintético agregado y el golden canónico permanecen en el código fuente y fuera del paquete. Esa regresión sintética no valida superficies reales. 7.2b falla de forma segura en producción porque los perfiles Meet/Teams permanecen `Unvalidated`: se abstiene y presenta la evidencia como No disponible. No existe identificación de hablantes, no se agrega una capacidad empaquetada y la etapa 7 no está completa. La identidad local (5.5) está implementada, todavía sin validación física. Consulta la [documentación de ingeniería](docs/README.md), el [corpus sintético](evaluation/stage-7b/README.md) y la [evidencia de validación](docs/validation.md). El alcance futuro indicado a continuación es un objetivo, no una afirmación de que ya se distribuya.

## Principios del producto

- Captura y transcripción con prioridad local.
- Iniciar una grabación requiere una acción explícita del usuario o una regla de automatización habilitada explícitamente. Cada nueva grabación conserva audio cifrado y muestra un indicador visible persistente.
- Las transcripciones, el audio, las capturas de pantalla, las identidades y los metadatos de calendario son datos sensibles.
- La automatización debe ser configurable por cuenta/calendario y siempre debe ofrecer controles de Pausar y Detener.
- La automatización de calendarios, la selección de fuente de reunión, la atribución de hablantes y la identificación biométrica son capacidades independientes y no deben presentarse como una sola función.

## Situación actual

| Etapa | Resultado | Estado |
|---|---|---|
| 1 | Base técnica y arquitectura local | Implementada — validación de producción pendiente |
| 2 | Captura de audio de dos fuentes y transcripción local | Implementada — validación de producción pendiente |
| 3 | Transcripción cifrada e historial de audio cifrado obligatorio | Implementada — validación de producción pendiente |
| 4 | Historial utilizable y almacenamiento configurable | Implementada — validación de producción pendiente |
| 5 | Beta distribuible y mantenible | Etapa 5.1 publicada en beta 6/secuencia 7 y validación automatizada aprobada — firma, ejecución productiva del Setup, validación física y cancelación humana pendientes |
| 5.5 | Identidad del usuario local y atribución del micrófono | Implementada — validación física de interfaz pendiente |
| 6 | Revisión, corrección, glosario de procedencia y retranscripción versionada | Línea base funcional implementada — validación física pendiente |
| 7 | Fuente de reunión y atribución de hablantes | 7.1a/7.2a y la infraestructura de 7.2b publicadas en beta 6; evaluador/corpus sintético presentes solo en fuente — perfiles de producción no validados, sin identificación de hablantes y con validación física pendiente; 7.2c+ planificadas |
| 8 | Búsqueda, revisión por lotes, glosario global y productividad | Planificada — edición y navegación de audio básicas ya entregadas en la etapa 6 |
| 9 | Inteligencia de reuniones opcional | Planificada |
| 10 | Integración organizacional/con plataforma opcional | Futura |
| 11 | Cuentas conectadas, calendarios y automatización de reuniones | Futura |
| 12 | Entrenamiento opcional de modelos con correcciones aprobadas | Futura — etapa final |

Las pruebas físicas prolongadas de dos y cinco horas, aplazadas, siguen siendo requisitos para publicar en producción. No bloquean el desarrollo funcional de la beta interna, pero no se debe declarar el producto listo para producción sin ellas.

## Etapa 5 — Beta distribuible y mantenible

### Objetivo

Permitir instalar, actualizar, diagnosticar y recuperar la aplicación existente de forma segura en otros equipos.

### Base implementada

- Código público en GitHub, ZIP completo y Setup manual para Windows con sumas de comprobación; [`0.2.0-beta.6`](https://github.com/Andres-MMG/Trazio-Asistente-Reunion/releases/tag/v0.2.0-beta.6) está publicado como prerelease. El tag resuelve al commit `232caf92832e2d7ef53f2578c32a230ed9bcc2e7`. El ZIP de **86,830,231 bytes**, SHA-256 `4a2a5e9e1f9d305e9986f37d07613f4852e463d4024a4f5ce62b951720106070`, y el Setup de **60,003,519 bytes**, SHA-256 `e40236da97dd411fd5e17b8c8bf49dfca3dc3ffd0ec63c8c9e2681fc98a4a9c1`, coinciden con los recursos remotos y sus archivos laterales.
- Script de publicación combinada de aplicación/proceso auxiliar con comprobaciones de paquete y prueba básica de salud por canal con nombre.
- El contrato publicado exige `VisualAnalysis.dll` con la misma versión de producto que App/Worker y excluye la CLI `VisualEvaluation`, sus archivos de ejecución, el corpus/golden y cualquier directorio `tools` o `evaluation`; el manifiesto conserva exactamente las cinco capacidades existentes.
- La verificación independiente aprobó metadatos **4/4**, evaluación visual **74/74**, captura visual **206/206**, Release serial/paralelo **446/446**, compilación sin advertencias/errores, CLI `VE000`, `publish` y smoke IPC integrado/explícito. El layout final coincidió **495/495** archivos byte a byte, con cinco capacidades y cero hallazgos prohibidos, rutas locales o referencias CodeView.
- Beta 6 incluye un instalador Inno Setup manual/offline por usuario. Mantiene una raíz estable y payloads en `versions/<versión>`, repara la misma secuencia, actualiza solo hacia secuencias mayores y rechaza downgrades o versiones legacy desconocidas antes de copiar.
- App y Worker retienen el mismo mutex de actividad; el instalador no los cierra ni reinicia y bloquea si cualquiera está abierto durante su chequeo inicial. El named pipe continúa siendo la autoridad de instancia única de la aplicación. Sigue pendiente cerrar o aceptar explícitamente la carrera de una App/Worker que se inicie después de ese chequeo.
- `publish.ps1` emite un manifiesto determinista de ruta relativa, longitud y SHA-256. `build-installer.ps1` productivo vuelve a publicar, exige rutas e identidades canónicas, valida completamente los sidecars y produce instalador, `.sha256` y manifiesto sin firma; no se afirma reproducibilidad byte a byte del `.exe`. El harness usa AppId, workspace, ruta, registro, grupo y mutex desechables y nunca la instalación productiva.
- Beta 6/secuencia 7: compilación Release con 0 advertencias/errores; pruebas enfocadas 10/10, Release serial y paralelo 451/451; `VE000 verified`, `publish` y smoke IPC integrado/explícito aprobados. El layout final coincide en 495/495 archivos y reporta `ProductVersion` `0.2.0-beta.6+232caf92832e2d7ef53f2578c32a230ed9bcc2e7`; el harness desechable aprobó 14/14. El Setup publicado informa `NotSigned`; el SHA-256 del manifiesto de payload es `b14b73a7a737c72966d557cf0ff43e84b033e4a4d606653eeb1fd01f049cd135`. El Setup productivo no se ejecutó; tampoco se completaron cancelación humana ni validación en otra máquina o cuenta.

### Alcance pendiente

- Publicar y validar el instalador en equipos/cuentas representativos con datos sintéticos y después con una copia de prueba de datos existentes.
- Firmar el manifiesto/instalador y definir un canal confiable de distribución; SHA-256 sin firma comprueba integridad, no autenticidad.
- Diseñar autoactualización solo después de estabilizar el flujo manual. No descargar ni instalar silenciosamente mientras exista una grabación activa.
- Actualizar la aplicación y el modelo Whisper de forma independiente cuando el modelo no haya cambiado.
- Firmar el ejecutable y el instalador.
- Exportar diagnósticos que protejan la privacidad, sin transcripciones, audio conservado, capturas, secretos ni claves de cifrado.
- El bloqueo intermitente de archivos SQLite de prueba ya está corregido y cubierto por regresiones; no es trabajo pendiente. Reabrirlo solo ante evidencia nueva.

### Criterios de salida

- Una persona de pruebas puede instalar, reparar y actualizar Trazio sin copiar archivos manualmente en una matriz física registrada.
- Las reuniones y configuraciones existentes sobreviven a instalación, actualización, reparación y desinstalación; el instalador nunca lee, copia, migra ni elimina la raíz de datos.
- Una actualización fallida antes de completar Setup conserva la activación y el payload funcional anteriores. No se promete rollback después del primer arranque ni compatibilidad de datos hacia atrás.
- La exportación de diagnósticos no contiene contenido de reuniones ni secretos.

## Etapa 5.5 — Identidad del usuario local y atribución del micrófono

### Objetivo

Dar a cada grabación local una identidad de propietario y atribuir los segmentos transcritos del micrófono al usuario local confirmado.

### Alcance

- Agregar un perfil local con nombre visible obligatorio y organización opcional.
- Completar previamente el nombre visible desde la cuenta de Windows cuando esté disponible, pero exigir que el usuario lo confirme o cambie.
- Atribuir por defecto los segmentos transcritos del micrófono a este perfil local confirmado.
- Permitir un nombre visible específico para la reunión sin cambiar el perfil global.
- Guardar los datos del perfil cifrados con el modelo de privacidad local existente.

Esto identifica a la persona que usa el micrófono configurado. No demuestra quién está hablando físicamente por un micrófono compartido.

### Criterios de salida

- Los segmentos del micrófono muestran el nombre del usuario local confirmado.
- Un nombre específico para una reunión no modifica el perfil global.
- Los datos del perfil permanecen cifrados y sobreviven al reinicio.
> Estado de implementación de la etapa 5.5: perfil local, nombre por reunión, persistencia cifrada y atribución del micrófono implementados. Las pruebas automatizadas de identidad pasan; la validación física de interfaz y captura sigue pendiente.

## Etapa 6 — Revisión, corrección, glosario y retranscripción

**Estado: línea base funcional implementada.** Este cierre describe capacidades presentes en el código; no equivale a aceptación con dispositivos físicos ni preparación para producción.

### Línea base entregada

| Capacidad | Comportamiento implementado |
|---|---|
| Espacio de revisión | Abre conversaciones guardadas por segmentos con marcas de tiempo, fuente e identidad local disponible. |
| Audio sincronizado | Reproduce por fuente, navega por línea de tiempo y permite escuchar el fragmento asociado al segmento seleccionado. |
| Correcciones humanas | Guarda y deshace correcciones sin sobrescribir el texto original. |
| Glosario con procedencia | Extrae términos modificados después de una edición humana y guarda de forma explícita entradas cifradas enlazadas a su corrección de origen. Es evidencia para uso futuro: **todavía no cambia Whisper ni nuevas transcripciones**. |
| Retranscripción versionada | Procesa el audio cifrado conservado en una revisión independiente, con estado y hash de modelo, sin reemplazar el original ni las correcciones humanas. |
| Comparación | Compara revisiones exitosas por intervalos sobre la misma fuente y ofrece acceso al audio correspondiente; no presenta la diferencia visual como una métrica de precisión. |
| Exportación manual | Exporta TXT, WAV por fuente y una nota Markdown compatible con Obsidian a un destino elegido explícitamente. La nota usa el original efectivo con correcciones humanas y no incluye audio, rutas ni identificadores internos. |

### Criterios de aceptación de la línea base

- Cualquier conversación guardada se abre en el espacio de revisión por segmentos.
- Una sesión con audio conservado permite reproducción por fuente y segmento, navegación temporal y retranscripción.
- Una sesión sin audio permite revisar y corregir texto sin afirmar que el sonido se puede recuperar.
- Una corrección conserva el texto original, registra su procedencia y se puede deshacer.
- Una entrada de glosario se crea solo por acción explícita, permanece cifrada y conserva la corrección que la originó para uso futuro.
- Una retranscripción crea una nueva revisión versionada sin volver a capturar audio ni sobrescribir revisiones previas.
- La exportación a Obsidian es manual, genera Markdown sin cifrar y advierte el límite de privacidad antes de escribir en la carpeta elegida.

### Trabajo reasignado

- La navegación avanzada (anterior/siguiente, velocidad, resaltado continuo y revisión por lotes) pasa a la etapa 8.
- La aplicación futura del glosario a instrucciones de Whisper o posprocesamiento con vista previa/deshacer pasa a la etapa 8. Guardar una entrada no reentrena el modelo.
- Los perfiles de calidad y la evaluación medible de precisión no son un requisito de cierre funcional de esta etapa; forman parte de la validación de producción aplazada.
- Siguen pendientes la validación física de la interfaz de revisión y una exportación real hacia una bóveda de Obsidian de prueba.

## Etapa 7 — Fuente de reunión y atribución de hablantes

**Estado: en curso.** La rebanada 7.1a asocia opcionalmente una ventana superior. La 7.2a agrega consentimiento por sesión y captura WGC efímera. La infraestructura fuente de 7.2b agrega un consentimiento adicional de un solo uso, sondeo agregado WGC/D3D11 acotado, evidencia cifrada de cobertura/actividad y presentación fail-closed en vivo/Historial. La biblioteca compartida `VisualAnalysis`, publicada desde beta 5, continúa vigente en beta 6; el evaluador no empaquetado con [corpus sintético agregado y golden canónico](evaluation/stage-7b/README.md) permanece como herramienta de fuente. Es una regresión determinista, no calibración física ni aceptación de producción. Los perfiles de producción Meet/Teams permanecen `Unvalidated`, no tienen política de detección y se abstienen: la evidencia aparece como **No disponible**. No existe identificación de hablantes.

### Objetivo

Asociar una sesión con la superficie seleccionada de Meet o Teams y atribuir el habla remota cuando exista evidencia fiable.

Esta etapa se divide intencionalmente en niveles de confianza separados. Trazio nunca debe afirmar un nombre de hablante cuando la evidencia disponible solo respalde una etiqueta anónima.

### 7.1 Fuente de reunión seleccionada por el usuario

#### 7.1a — Ventana superior y proveedor normalizado

**Implementada en código; validación física pendiente.** El usuario abre un selector explícito y actualiza la lista de ventanas superiores visibles. Trazio clasifica de forma conservadora `Google Meet`, `Microsoft Teams` u `Otra aplicación`; sin selección conserva `Sin seleccionar`.

- La selección es opcional y nunca bloquea **Iniciar transcripción**.
- El título y el nombre de proceso existen solo dentro del selector y se descartan al asociar o cerrar. Fuera del modal permanecen temporalmente HWND, PID y proveedor; la sesión persiste únicamente el enum del proveedor y las sesiones antiguas quedan como `Sin seleccionar`.
- Antes de iniciar se revalida la misma ventana y PID. Si se perdió, Trazio avisa sin modal, inicia con `Sin seleccionar` y no reasigna otra ventana.
- Durante la grabación no se puede cambiar ni quitar la asociación. Una comprobación acotada avisa si la ventana desaparece, sin detener ni cambiar la captura de audio. Toda finalización, incluso interrumpida o fallida, consume la asociación para que no pase a otra reunión.
- La captura de audio sigue siendo WASAPI del dispositivo completo. Asociar una ventana **no activa** el análisis visual y **no** aísla el audio de esa ventana.

Esta primera rebanada selecciona una **ventana superior**, no una pestaña individual. La lectura de pestaña, URL, DOM, subtítulos o estado del hablante requiere un adaptador y permiso independientes; no forma parte de 7.1a. La automatización de calendarios tampoco puede omitir silenciosamente ese permiso.

### 7.2 Análisis visual y adaptadores de proveedores

**7.2a y la infraestructura fuente de 7.2b están implementadas; la validación física continúa pendiente.** El [plan técnico de la etapa 7.2](docs/stage-7-visual-speaker-plan.md) mantiene la captura WGC y el análisis anónimo como consentimientos separados. La autorización de análisis es consumible una sola vez y está ligada a la ventana/sesión/alcance exactos. El extractor D3D11 trabaja con parches y solo entrega agregados; no retiene píxeles, imágenes o video. Los intervalos derivados de cobertura/actividad se cifran y solo se correlacionan con `SystemOutput`. La transcripción, `SpeakerName` y las exportaciones TXT/Markdown/Obsidian no cambian.

7.2b no lee OCR, rostros, nombres, chat, subtítulos ni documentos. Su presentación de actividad anónima no equivale a atribución de hablante. Hasta validar y calibrar físicamente perfiles de Meet/Teams, el procesamiento de producción se abstiene y tanto la vista en vivo como Historial muestran **No disponible**.

El trabajo se divide en rebanadas verificables:

1. **7.2a:** incluida desde beta 3: HWND/PID revalidado, consentimiento separado, frame pool de dos buffers, ciclo de vida y descarte inmediato; faltan WGC físico, interfaz/lector de pantalla y prueba de dos horas.
2. **7.2b:** infraestructura publicada en beta 6: consentimiento adicional de un solo uso, sondeo agregado D3D11 acotado, eventos derivados cifrados, correlación exclusiva con `SystemOutput` y presentación fail-closed. El código fuente incluye un evaluador de consola no empaquetado, corpus sintético agregado y verificación golden byte a byte, sin promover umbrales. Los perfiles de producción siguen `Unvalidated`; faltan calibración física, WGC/GPU/accesibilidad, Meet/Teams reales y pruebas de 2/5 horas.
3. **7.2c:** adaptador versionado de Google Meet web mediante una extensión con permiso mínimo y WGC como respaldo.
4. **7.2d:** adaptador de Microsoft Teams web/escritorio; extensión para web y WGC para escritorio/respaldo.
5. **7.2e:** evaluación de precisión, accesibilidad, recursos y duración; aprobar 2 horas antes de intentar 5 horas.

La estrategia recomendada es híbrida. La extensión del navegador aporta señales semánticas más fiables en Meet/Teams web; WGC cubre Teams escritorio y el fallback anónimo. UI Automation es solo una señal secundaria. La aplicación de escritorio conserva la autoridad de grabación y almacenamiento cifrado.

### 7.3 Niveles de evidencia del hablante

Guardar confianza de atribución y tipo de evidencia para cada segmento con nombre:

1. `Perfil local`: fuente de micrófono asociada al usuario local confirmado.
2. `Metadatos del proveedor`: etiqueta de hablante activo/subtítulo obtenida desde la superficie seleccionada de Meet/Teams.
3. `Corrección del usuario`: una persona asignó o corrigió el hablante.
4. `Hablante diarizado`: la agrupación de audio produjo `Hablante 1`, `Hablante 2`, etc., sin un nombre verificado.

La diarización de audio separa voces, pero no revela nombres reales. Asociar una voz con una persona requeriría un registro explícito de voz e introduce obligaciones legales y de privacidad biométrica; queda fuera del alcance inicial de la etapa 7.

### 7.4 Capturas opcionales al cambiar de hablante

- Esta capacidad futura está separada de 7.2: el análisis planificado descarta frames y no guarda imágenes por defecto.
- No usar capturas de pantalla como mecanismo principal de identificación de hablantes.
- Si se habilitan, capturar solo al detectar una transición de hablante, no continuamente.
- Preferir recortar el recuadro del participante activo y la etiqueta de nombre en vez de almacenar toda la pantalla de la reunión.
- Cifrar las capturas, aplicar una retención breve y proporcionar controles independientes de eliminación.
- Obtener consentimiento explícito porque las capturas pueden contener rostros, chat, documentos compartidos u otro contenido sensible.
- Tratar la detección visual solo como evidencia complementaria; un resaltado de interfaz puede estar retrasado, ser ambiguo o incorrecto.

### Criterios de salida

- El usuario puede seleccionar una ventana superior y ver el proveedor detectado; la validación física de 7.1a sigue pendiente. La selección de pestaña individual permanece fuera de esta rebanada.
- Los consentimientos de captura 7.2a y análisis anónimo 7.2b son distintos de la selección y entre sí; el de análisis es de un solo uso. No se conservan píxeles, imágenes o video y su aceptación física sigue pendiente.
- Trazio continúa de forma segura cuando los metadatos del proveedor no están disponibles o falla un adaptador.
- Las etiquetas de hablantes remotos con nombre incluyen evidencia y confianza; los segmentos inciertos permanecen anónimos.
- Los fotogramas de 7.2a/7.2b no se persisten; los intervalos derivados de cobertura/actividad de 7.2b se cifran, se acotan por sesión y se eliminan con ella.

## Etapa 8 — Historial y productividad

- Búsqueda de texto completo entre reuniones.
- **8.1a implementada en código; validación física pendiente:** segmento anterior/siguiente sobre las filas cargadas sin reproducción automática, línea de tiempo que conserva los huecos reales del audio y resaltado continuo separado de la selección de edición. Cambiar sesión, fuente o revisión y detener limpia la operación anterior.
- **8.1b implementada en código; validación audible pendiente:** selector temporal de `0,75×`, `1×`, `1,25×`, `1,5×` y `2×` para pista, fragmento e intervalo comparado. El cambio reinicia desde la posición fuente calculada a partir de los bytes informados por el dispositivo, conserva pausa y límite final, y no altera las marcas lógicas. Usa WDL de NAudio sin dependencia nueva; deliberadamente cambia el tono y no pretende hacer *time-stretch* natural.
- Navegación avanzada restante: revisión por lotes.
- Revisión y aprobación avanzadas por lotes de revisiones de transcripción y etiquetas de hablantes.
- Gestión global del glosario, detección de duplicados, importación y exportación.
- Aplicar glosario de forma acotada a instrucciones de Whisper o posprocesamiento determinista, siempre con vista previa, procedencia y deshacer; nunca reemplazar silenciosamente términos ambiguos.
- Marcadores, notas, etiquetas e indicadores de seguimiento.
- Ampliar los formatos estructurados más allá de la nota Markdown/Obsidian ya entregada y evaluar integración directa solo si conserva el control explícito del usuario.
- Evaluar Opus para archivos de audio cifrados más pequeños, preservando navegación y exportación fiables.

La evidencia automatizada de 8.1a cubre límites de navegación, listas reemplazadas y fuentes mezcladas; huecos, fronteras y fin exclusivo de intervalos; reloj de salida resistente a lectura adelantada; cancelación con espera; pausa entre fragmentos; reproducción transitoria sin cambiar fuente/revisión; resaltado por fuente y solapamientos deterministas; contrato XAML, accesibilidad declarada y ausencia de reproducción implícita. 8.1b agrega catálogo cerrado, DSP sin dispositivo, posición fuente por velocidad, límites previos al resampling, reinicio pausado y reemplazo generacional. No reemplaza una prueba audible con dispositivo real, teclado completo ni lector de pantalla.

## Etapa 9 — Inteligencia de reuniones opcional

- Resúmenes opcionales de reuniones mediante una API LLM externa inicialmente, con dirección del proveedor y clave API configurables; después podría reemplazarse por un servicio compatible autoalojado.
- Explorar un modelo generativo que proponga preguntas candidatas sustentadas en evidencia y Jev que devuelva juicios tipados; validar y acotar esas preguntas en el código de la aplicación. Ninguna de las dos integraciones está implementada actualmente.
- Exigir activación explícita antes de que el contenido de la transcripción salga del equipo; proteger credenciales y registrar la procedencia del proveedor/modelo. La grabación y revisión locales deben continuar sin esta integración.
- Decisiones, compromisos y acciones pendientes.
- Traducción opcional.
- Segmentación por temas.
- Todo contenido generado debe enlazar a la evidencia de la transcripción y permanecer claramente marcado como generado por máquina.

## Etapa 10 — Integración organizacional/con plataforma opcional

- Sincronización con Trazio Platforms mediante activación explícita.
- Políticas organizacionales, controles de retención y despliegue administrativo.
- Plantillas compartidas y vocabulario controlado.
- Esta etapa cambia el límite de privacidad exclusivamente local y, por tanto, requiere un diseño separado de seguridad, aspectos legales, aislamiento entre organizaciones y consentimiento.

## Etapa 11 — Cuentas conectadas, calendarios y automatización de reuniones

### Objetivo

Una vez que el resto del producto local sea estable, permitir que el usuario conecte uno o varios calendarios capaces de preparar, iniciar y detener la captura de reuniones automáticamente.

### Cuentas de correo/calendario conectadas

- Admitir varias cuentas conectadas, inicialmente Google Calendar y Microsoft 365/Outlook Calendar.
- Usar autorización OAuth; nunca almacenar contraseñas de cuentas.
- Mantener separadas la identidad de cuenta, la selección de calendarios y la política de grabación.
- Permitir que el usuario seleccione qué calendarios se supervisan y elija una identidad/correo principal.
- Leer solo los campos mínimos necesarios del evento: título, inicio/fin, organizador, asistentes, proveedor y URL de acceso.
- Permitir desconectar una cuenta y eliminar sus metadatos en caché.

### Reglas de automatización de reuniones

Cada calendario conectado puede tener una regla explícita:

- Ignorar eventos.
- Solo notificar.
- Preparar Trazio y esperar confirmación.
- Iniciar captura local automáticamente a la hora programada.

La captura automática requiere activación por calendario o regla y debe mostrar un indicador persistente de grabación. Nunca debe habilitar silenciosamente la grabación solo porque se conectó una cuenta.

### Ciclo de vida automático

- Activar o iniciar Trazio antes del evento.
- Crear la sesión usando el título del evento y los metadatos del proveedor.
- Abrir opcionalmente la URL de acceso a la reunión; Trazio no suplanta al usuario ni omite una sala de espera.
- Iniciar con el desfase configurado cuando la regla de automatización lo permita.
- Detener al finalizar la hora programada más un período de gracia configurable.
- Usar el silencio sostenido solo como señal secundaria de detención, nunca como única fuente de verdad.
- Si el evento se extiende o el usuario sigue activo, ofrecer continuar.
- Finalizar el cifrado y la persistencia, luego minimizar o cerrar según la preferencia del usuario.
- Registrar por qué comenzó y terminó la sesión: acción manual, regla de calendario, fin programado, alternativa por silencio o recuperación de error.

### Criterios de salida

- Dos o más cuentas/calendarios pueden coexistir sin sesiones duplicadas.
- Un evento de calendario autorizado puede abrir, iniciar, detener y finalizar una sesión sin perder datos.
- El usuario siempre puede pausar o detener la automatización inmediatamente.
- Desconectar una cuenta elimina sus tokens y metadatos en caché sin borrar reuniones locales.
## Etapa 12 — Entrenamiento opcional de modelos con correcciones aprobadas

### Objetivo

Después de completar la automatización de calendarios, evaluar si las correcciones confirmadas de transcripciones y el audio conservado justifican entrenar un nuevo modelo de transcripción de Trazio.

### Requisitos de entrada

- Las etapas 1–11 están completas.
- Los usuarios han consentido explícitamente incluir audio y correcciones seleccionados en un conjunto de entrenamiento.
- Las correcciones tienen historial de revisiones y estado de aprobación fiables.
- El audio, los segmentos transcritos, las marcas de tiempo, el idioma, la fuente y la evidencia de hablantes se pueden exportar sin exponer contenido ajeno de reuniones.
- Existe un conjunto representativo de evaluación de español y español de Chile antes de comenzar el entrenamiento.

### Flujo de trabajo del conjunto de datos

1. Seleccionar explícitamente reuniones elegibles y segmentos corregidos.
2. Excluir muestras privadas, no aprobadas, de baja calidad o ambiguas.
3. Emparejar cada intervalo de audio con su transcripción final aprobada por una persona.
4. Eliminar ejemplos duplicados y contradictorios.
5. Dividir los datos en conjuntos de entrenamiento, validación y prueba sin intervenir.
6. Cifrar los conjuntos de datos en reposo y registrar consentimiento, procedencia y obligaciones de eliminación.
7. Entrenar un modelo candidato independiente; nunca modificar directamente el modelo de producción instalado.
8. Comparar el candidato con el modelo actual sobre el conjunto de prueba sin intervenir.
9. Publicar una nueva versión firmada del modelo solo cuando mejore la precisión sin regresiones inaceptables.
10. Conservar la posibilidad de volver al modelo anterior.

### Límites de seguridad y privacidad

- Las correcciones no entran automáticamente al entrenamiento.
- Las entradas de diccionario y las ediciones habituales de transcripción permanecen locales salvo selección y aprobación explícitas.
- El usuario puede revocar muestras no procesadas antes de publicar el conjunto de datos.
- Los datos de entrenamiento no deben contener tokens de calendario, credenciales, capturas ni metadatos ajenos de reuniones.
- Una versión del modelo debe registrar su política de datos, resultados de evaluación, cobertura lingüística y limitaciones conocidas.

### Criterios de salida

- El entrenamiento es reproducible desde un conjunto de datos versionado y consentido.
- El candidato mejora de forma demostrable las métricas de precisión acordadas.
- El nuevo modelo se versiona independientemente, está firmado, se puede instalar y revertir.
- Si no mejora la precisión, el modelo actual permanece sin cambios.
## Validación de producción aplazada

Antes de publicar en producción:

- Ejecutar la prueba controlada de solo micrófono, solo sistema y ambas fuentes.
- Validar físicamente la identidad local y la atribución del micrófono implementadas en la etapa 5.5.
- Validar la interfaz de revisión y la exportación manual hacia una bóveda de Obsidian de prueba.
- Definir perfiles de calidad y un conjunto repetible de evaluación con audio/texto representativo del español de Chile; registrar métricas reproducibles antes de afirmar mejoras de precisión.
- Validar físicamente WGC/GPU, teclado/lector de pantalla y accesibilidad; calibrar perfiles de Meet/Teams reales antes de cambiar su estado `Unvalidated` o mostrar evidencia distinta de **No disponible**.
- Ejecutar una prueba de reunión de dos horas.
- Ejecutar una prueba de reunión de cinco horas.
- Verificar CPU, memoria, crecimiento de almacenamiento, pausa/reanudación, cambios de dispositivos, recuperación de fallos, cifrado, reproducción, exportación e integridad de segmentos.
- Probar actualización y reversión del instalador en un equipo Windows limpio y en uno con reuniones existentes.

## Orden de ejecución recomendado

1. Publicar y validar físicamente la base manual/offline de la etapa 5.1; mantener pendientes firma, autoactualización y requisitos de producción.
2. Mantener la línea base funcional de la etapa 6 y completar su validación física pendiente sin ampliar silenciosamente su alcance.
3. Validar físicamente la selección de fuente 7.1a antes de intentar atribuir nombres a hablantes remotos.
4. Validar físicamente 7.2a/7.2b con WGC/GPU, interfaz/lector de pantalla, Meet/Teams reales y una sesión de dos horas antes de validar perfiles de producción; mantener `Unvalidated`, la abstención y la retención cero de píxeles/imágenes/video hasta contar con evidencia. Ejecutar la prueba de cinco horas solo después.
5. Implementar en la etapa 8 la navegación avanzada y la aplicación controlada del glosario.
6. Completar las etapas 8–10 y la validación prolongada aplazada antes de publicar en producción.
7. Implementar las cuentas conectadas, calendarios y automatización de reuniones con activación voluntaria de la etapa 11.
8. Evaluar el entrenamiento opcional de modelos de la etapa 12 solo después de completar la automatización de calendarios.
