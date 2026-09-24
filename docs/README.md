# Documentación de ingeniería

**Trazio Asistente Reunión** es un producto de escritorio para Windows, independiente de Trazio Platforms. La versión publicada actual es [`0.2.0-beta.9`](https://github.com/Andres-MMG/Trazio-Asistente-Reunion/releases/tag/v0.2.0-beta.9); incorpora 7.2a, la infraestructura de 7.2b, el instalador manual de la etapa 5.1, la revisión de historial 8.1, la búsqueda local 8.2 y el diccionario global 8.3a sin afirmar identificación de hablantes ni validación de producción.

La beta 9 usa la secuencia de instalador **10**, mantiene exactamente cinco capacidades y conserva el empaquetador ZIP canónico. La firma Authenticode, la ejecución productiva del Setup y la validación física audible, WGC/GPU, interfaz/accesibilidad, Meet/Teams y 2/5 horas siguen pendientes.

La beta 9 pública incluye la revisión de historial 8.1: anterior/siguiente sin reproducción automática, línea de tiempo con huecos reales, resaltado independiente y velocidad temporal `0,75×–2×`. La velocidad cambia el tono, no se persiste y la validación audible/accesible continúa pendiente.

La etapa 8.3a publicada agrega una pestaña global de diccionario con filtro local y activación por entrada. Esta rebanada no aplica el diccionario a Whisper, no modifica transcripciones ni agrega una capacidad nueva al paquete. La firma, la ejecución productiva del Setup y la validación visual, por teclado, lector de pantalla, audible, WGC/GPU, Meet/Teams y 2/5 horas permanecen pendientes.

El tag de beta 9 resuelve a `8eb4c2e5a16ff34db21a34bb1ff91feb93de7375`. El ZIP publicado mide **86,876,029 bytes** y su SHA-256 es `64861c690b4f89dd9bf347fc970761c1c95be2bcc67a075ce24f6a0f63dca7bd`; el Setup mide **60,037,793 bytes**, su SHA-256 es `3bfd4ae6777f18d1a59b379ee6bd42c515d6e13481ed19774c2c16fb67988635` y Authenticode informa `NotSigned`. El manifiesto de publicación mide **123,576 bytes**, SHA-256 `2b96f0f7082211edeef65608815b74e6d7e9fff1e1189117dc25c305a4b403a6`; el manifiesto del Setup mide **486 bytes**, SHA-256 `2457b68fbe5e87eaf75d7ec51c3c02148cd18ddbf811cbb832108a07ade1b40d`. Tamaños y digest remotos coinciden con los artefactos verificados. El Setup productivo no se ejecutó.

La verificación de beta 9 aprobó Release serial/paralelo **574/574**, la suite enfocada de 8.3a **23/23**, los contratos finales **22/22** y el harness desechable **14/14**. El layout final **495/495**, con `ProductVersion` `0.2.0-beta.9+8eb4c2e5a16ff34db21a34bb1ff91feb93de7375`, incluye el ensamblado puro `VisualAnalysis` y excluye la CLI offline `VisualEvaluation`, corpus/golden y directorios `tools`/`evaluation`; mantiene exactamente cinco capacidades. Beta 8 queda como evidencia histórica; su filtro ampliado **61/61** sigue siendo únicamente un antecedente de 8.2.

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

Última revisión de la documentación contra la fuente y la publicación: **2026-09-24**. Versión pública actual: `v0.2.0-beta.9`, secuencia 10; beta 8 queda como evidencia histórica. 7.2b requiere un consentimiento separado de un solo uso y contiene sondeo agregado WGC/D3D11 acotado, evidencia cifrada de cobertura/actividad y presentación fail-closed solo para `SystemOutput`; no modifica transcripción, `SpeakerName` ni exportaciones. Los perfiles Meet/Teams permanecen `Unvalidated`, se abstienen y muestran **No disponible**. La validación física audible, WGC/GPU/interfaz/accesibilidad/Meet/Teams/2 h/5 h, la firma, la ejecución productiva del Setup, la cancelación humana, otra máquina o cuenta y 7.2c–7.2e siguen pendientes.
