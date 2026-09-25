# Documentación de ingeniería

**Trazio Asistente Reunión** es un producto de escritorio para Windows, independiente de Trazio Platforms. La versión publicada actual es [0.2.0-beta.15](https://github.com/Andres-MMG/Trazio-Asistente-Reunion/releases/tag/v0.2.0-beta.15); incorpora la configuración segura del proveedor externo 9.1 sin activar llamadas de red, resúmenes ni envío de transcripciones.

La beta 15 usa la secuencia de instalador **16**, mantiene exactamente cinco capacidades y conserva el empaquetador ZIP canónico. La firma Authenticode, la ejecución productiva del Setup y la validación física audible, importación/exportación real, WGC/GPU, interfaz/accesibilidad, Meet/Teams y 2/5 horas siguen pendientes.

La beta 15 pública conserva la revisión de historial 8.1: anterior/siguiente sin reproducción automática, línea de tiempo con huecos reales, resaltado independiente y velocidad temporal 0,75×–2×. La velocidad cambia el tono, no se persiste y la validación audible/accesible continúa pendiente.

La etapa 8.3a agrega una pestaña global de diccionario con filtro local y activación por entrada. La etapa 8.5 permite confirmar términos activos para orientar una retranscripción separada; no reemplaza texto, no modifica el original y no agrega una capacidad nueva al paquete. La firma, la ejecución productiva del Setup y la validación visual, por teclado, lector de pantalla, audible, WGC/GPU, Meet/Teams y 2/5 horas permanecen pendientes.

La etapa 8.3b está publicada en `0.2.0-beta.10`, secuencia **11**: intercambio JSON v1, vista previa en memoria, importación cifrada atómica y exportación explícita sin cifrar. Conserva duplicados históricos, señala conflictos y no fusiona ni elimina entradas automáticamente.

La etapa 8.6 publica notas, decisiones y seguimientos manuales vinculados a un segmento. El contenido queda cifrado, los cambios de estado son explícitos y las anotaciones no se exportan ni alimentan IA automáticamente.

Las etapas 8.4a/8.4b permanecen publicadas desde 0.2.0-beta.12. La etapa 8.5 está publicada en 0.2.0-beta.13, secuencia **14**: utiliza solo términos preferidos confirmados como prompt inicial local de Whisper, conserva el original y crea una revisión separada.

El tag de beta 15 resuelve a 9a5c4af367e805fe1a8d0cd0a86722e03b1c1812. El ZIP publicado mide **86,959,816 bytes** y su SHA-256 es 0010af9e97d162835d0b109823fec00035a2013d2cd0ab8ee21fb9d75d7e4e7c; el Setup mide **60,104,288 bytes**, su SHA-256 es 6fc40804fa7a3add3a2e823cb5e4c818f8fd07a39853612606e559f2aa883c8d y Authenticode informa NotSigned. El manifiesto de publicación mide **84,445 bytes**, SHA-256 e0b4140026683af5672c83a14ade1322b8472e3711dd2c84837f7ab0d210761e; el manifiesto del Setup mide **458 bytes**, SHA-256 41a3f4a554fcfed1f5ad9c1f9819e7cfc7f4fd4da62194668b6a034558a0c8b0. Los seis recursos remotos coinciden con los artefactos verificados. El Setup productivo no se ejecutó.

La verificación de beta 15 aprobó Release serial/paralelo **671/671**, las pruebas focalizadas de proveedor y metadatos **22/22**, el harness desechable **14/14** y la compilación con **0 advertencias y 0 errores**. El layout final contiene **495** archivos, con ProductVersion 0.2.0-beta.15+9a5c4af367e805fe1a8d0cd0a86722e03b1c1812, incluye el ensamblado puro VisualAnalysis y excluye la CLI offline VisualEvaluation, corpus/golden y directorios tools/evaluation; mantiene exactamente cinco capacidades. Beta 14 y versiones anteriores quedan como evidencia histórica.

### Etapa 9.1 publicada en beta 15

La pestaña **Inteligencia** permite preparar un proveedor compatible con una URL exacta, un modelo y una clave API. La configuración se protege con DPAPI para el usuario actual, la clave no se vuelve a mostrar y los destinos remotos exigen HTTPS; HTTP queda limitado a loopback para servicios autoalojados en el mismo equipo.

Esta rebanada **no contiene un cliente de red**: guardar, reemplazar o eliminar la clave no envía transcripciones, audio ni metadatos. El consentimiento por operación, la vista previa exacta del texto saliente y cualquier resumen o clasificación pertenecen a la siguiente rebanada.

## Elige el siguiente paso

| Quiero… | Consulta |
|---|---|
| Descargar la aplicación y grabar mi primera reunión | [Guía de uso](user-guide.md) |
| Entender qué está construido y qué viene después | [Hoja de ruta del producto](../ROADMAP.md) |
| Entender los componentes, el flujo de datos y las decisiones de ingeniería | [Arquitectura](architecture.md) |
| Revisar el diseño, la implementación 7.2a/7.2b y los límites del análisis visual | [Plan técnico de la etapa 7.2](stage-7-visual-speaker-plan.md) |
| Compilar, empaquetar o contribuir | [Guía de desarrollo](development.md) |
| Entender el cifrado, las exportaciones y los límites de recuperación | [Seguridad y privacidad](security.md) |
| Reproducir comprobaciones o validar una versión | [Guía de validación](validation.md) |

## Interpretar el estado con honestidad

- **Implementado** significa que la capacidad existe en el código fuente. No significa que se hayan aprobado todos los escenarios con dispositivos físicos.
- **En curso** significa que hay un subconjunto funcional, pero no se cumplen todos los criterios de salida de la etapa.
- **Planificado** significa una intención de producto/diseño, no una capacidad disponible en el ZIP.
- **Validación pendiente** significa que todavía falta evidencia; una compilación correcta no la reemplaza.

La [hoja de ruta](../ROADMAP.md) es el plan canónico de etapas. Las versiones de dependencias provienen de los archivos de proyecto; los metadatos de versión provienen de [Directory.Build.props](../Directory.Build.props). El [registro de validación](validation.md#evidencia-actual) separa la evidencia automatizada de las comprobaciones de hardware y larga duración pendientes.

Última revisión de la documentación contra la fuente y la publicación: **2026-09-24**. Versión pública actual: v0.2.0-beta.15, secuencia 16; beta 14 y versiones anteriores quedan como evidencia histórica. 7.2b requiere un consentimiento separado de un solo uso y contiene sondeo agregado WGC/D3D11 acotado, evidencia cifrada de cobertura/actividad y presentación fail-closed solo para SystemOutput; no modifica transcripción, SpeakerName ni exportaciones. Los perfiles Meet/Teams permanecen Unvalidated, se abstienen y muestran **No disponible**. La validación física audible, WGC/GPU/interfaz/accesibilidad/Meet/Teams/2 h/5 h, la firma, la ejecución productiva del Setup, la cancelación humana, otra máquina o cuenta y 7.2c–7.2e siguen pendientes.
