# Documentación de ingeniería

**Trazio Asistente Reunión** es un producto de escritorio para Windows, independiente de Trazio Platforms. La versión publicada actual es [`0.2.0-beta.7`](https://github.com/Andres-MMG/Trazio-Asistente-Reunion/releases/tag/v0.2.0-beta.7); incorpora 7.2a, la infraestructura de 7.2b, el instalador manual de la etapa 5.1 y la revisión de historial 8.1 sin afirmar identificación de hablantes ni validación de producción.

La fuente en `main` prepara el candidato `0.2.0-beta.8`, secuencia de instalador **9**, con la búsqueda local 8.2. Todavía no es una release: no tiene tag, commit final, ZIP, Setup, tamaños ni hashes definitivos. Mantiene exactamente cinco capacidades. La firma Authenticode, la ejecución productiva del Setup y la validación física audible, WGC/GPU, interfaz/accesibilidad, Meet/Teams y 2/5 horas siguen pendientes.

La beta 7 pública, secuencia de instalador **8**, incluye la revisión de historial 8.1: anterior/siguiente sin reproducción automática, línea de tiempo con huecos reales, resaltado independiente y velocidad temporal `0,75×–2×`. La velocidad cambia el tono, no se persiste y la validación audible/accesible continúa pendiente.

El tag de beta 7 resuelve a `25e3f36867250599ef5026d7270fc37af85a7c44`. El ZIP publicado mide **86,851,547 bytes** y su SHA-256 es `951ce1653c2bbdd0d5c0a0827cab5b3c6d5c5c762f1574a3434fa937c44d00e8`; el Setup mide **60,024,844 bytes**, su SHA-256 es `fb476b57e82fbf696e886831029be17d221596e5aaf0fd8cddf808714e0fe526` y Authenticode informa `NotSigned`. Tamaños y digest remotos coinciden con los artefactos verificados. El Setup productivo no se ejecutó.

La verificación de beta 7 aprobó las pruebas enfocadas **10/10**, Release serial/paralelo **520/520**, compilación limpia, CLI `VE000`, `publish`, smoke IPC integrado/explícito y harness desechable **14/14**. El layout final **495/495**, con `ProductVersion` `0.2.0-beta.7+25e3f36867250599ef5026d7270fc37af85a7c44`, incluye el ensamblado puro `VisualAnalysis` y excluye la CLI offline `VisualEvaluation`, corpus/golden y directorios `tools`/`evaluation`; mantiene cinco capacidades y cero hallazgos prohibidos, rutas locales o CodeView. El SHA-256 del manifiesto del payload es `8d585ce9438c9c3778b1a4eb1f8c4de0e9ae3924ed5110a4b624ea661da4a8a0`.

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

Última revisión de la documentación contra la fuente y la publicación: **2026-09-24**. Versión pública actual: `v0.2.0-beta.7`, secuencia 8; beta 6 queda como evidencia histórica. 7.2b requiere un consentimiento separado de un solo uso y contiene sondeo agregado WGC/D3D11 acotado, evidencia cifrada de cobertura/actividad y presentación fail-closed solo para `SystemOutput`; no modifica transcripción, `SpeakerName` ni exportaciones. Los perfiles Meet/Teams permanecen `Unvalidated`, se abstienen y muestran **No disponible**. La validación física audible, WGC/GPU/interfaz/accesibilidad/Meet/Teams/2 h/5 h, la firma, la ejecución productiva del Setup, la cancelación humana, otra máquina o cuenta y 7.2c–7.2e siguen pendientes.
