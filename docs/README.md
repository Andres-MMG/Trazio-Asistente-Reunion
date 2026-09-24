# Documentación de ingeniería

**Trazio Asistente Reunión** es un producto de escritorio para Windows, independiente de Trazio Platforms. La versión publicada actual es [`0.2.0-beta.8`](https://github.com/Andres-MMG/Trazio-Asistente-Reunion/releases/tag/v0.2.0-beta.8); incorpora 7.2a, la infraestructura de 7.2b, el instalador manual de la etapa 5.1, la revisión de historial 8.1 y la búsqueda local 8.2 sin afirmar identificación de hablantes ni validación de producción.

La beta 8 usa la secuencia de instalador **9**, mantiene exactamente cinco capacidades y agrega el empaquetador ZIP canónico. La firma Authenticode, la ejecución productiva del Setup y la validación física audible, WGC/GPU, interfaz/accesibilidad, Meet/Teams y 2/5 horas siguen pendientes.

La beta 8 pública incluye la revisión de historial 8.1: anterior/siguiente sin reproducción automática, línea de tiempo con huecos reales, resaltado independiente y velocidad temporal `0,75×–2×`. La velocidad cambia el tono, no se persiste y la validación audible/accesible continúa pendiente.

La rama `main`, posterior a beta 8 y todavía no publicada, incorpora 8.3a: una pestaña global de diccionario con filtro local y activación por entrada. Esta rebanada no aplica el diccionario a Whisper ni agrega una capacidad nueva al paquete.

El tag de beta 8 resuelve a `20c94272261f5697a548c56754039029b23f1548`. El ZIP publicado mide **86,866,781 bytes** y su SHA-256 es `240a792ab8388a0511fb8b25ac466feeb104fdf6779b72938302adc9570b2e3f`; el Setup mide **60,034,031 bytes**, su SHA-256 es `61d87a71e2a040f10c70fa05389ca341db79e5cdcd88b2e314dcaf00c88fcaa0` y Authenticode informa `NotSigned`. Tamaños y digest remotos coinciden con los artefactos verificados. El Setup productivo no se ejecutó.

La verificación de beta 8 aprobó Release serial/paralelo **543/543**, el filtro enfocado actual de cinco clases **48/48**, los contratos finales **22/22** y el harness desechable **14/14**. El filtro ampliado **61/61** se conserva como evidencia histórica de 8.2. El layout final **495/495**, con `ProductVersion` `0.2.0-beta.8+20c94272261f5697a548c56754039029b23f1548`, incluye el ensamblado puro `VisualAnalysis` y excluye la CLI offline `VisualEvaluation`, corpus/golden y directorios `tools`/`evaluation`; mantiene exactamente cinco capacidades.

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

Última revisión de la documentación contra la fuente y la publicación: **2026-09-24**. Versión pública actual: `v0.2.0-beta.8`, secuencia 9; beta 7 queda como evidencia histórica. 7.2b requiere un consentimiento separado de un solo uso y contiene sondeo agregado WGC/D3D11 acotado, evidencia cifrada de cobertura/actividad y presentación fail-closed solo para `SystemOutput`; no modifica transcripción, `SpeakerName` ni exportaciones. Los perfiles Meet/Teams permanecen `Unvalidated`, se abstienen y muestran **No disponible**. La validación física audible, WGC/GPU/interfaz/accesibilidad/Meet/Teams/2 h/5 h, la firma, la ejecución productiva del Setup, la cancelación humana, otra máquina o cuenta y 7.2c–7.2e siguen pendientes.
