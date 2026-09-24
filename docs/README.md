# Documentación de ingeniería

**Trazio Asistente Reunión** es un producto de escritorio para Windows, independiente de Trazio Platforms. La versión publicada actual es [`0.2.0-beta.6`](https://github.com/Andres-MMG/Trazio-Asistente-Reunion/releases/tag/v0.2.0-beta.6); incorpora 7.2a, la infraestructura de 7.2b y el instalador manual de la etapa 5.1 sin afirmar identificación de hablantes ni validación de producción.

El tag de beta 6 resuelve a `232caf92832e2d7ef53f2578c32a230ed9bcc2e7`. El ZIP publicado mide **86,830,231 bytes** y su SHA-256 es `4a2a5e9e1f9d305e9986f37d07613f4852e463d4024a4f5ce62b951720106070`; el Setup mide **60,003,519 bytes**, su SHA-256 es `e40236da97dd411fd5e17b8c8bf49dfca3dc3ffd0ec63c8c9e2681fc98a4a9c1` y Authenticode informa `NotSigned`. Tamaños y digest remotos coinciden con los artefactos verificados. El Setup productivo no se ejecutó.

La verificación de beta 6 aprobó las pruebas enfocadas **10/10**, Release serial/paralelo **451/451**, compilación limpia, CLI `VE000`, `publish`, smoke IPC integrado/explícito y harness desechable **14/14**. El layout final **495/495**, con `ProductVersion` `0.2.0-beta.6+232caf92832e2d7ef53f2578c32a230ed9bcc2e7`, incluye el ensamblado puro `VisualAnalysis` y excluye la CLI offline `VisualEvaluation`, corpus/golden y directorios `tools`/`evaluation`; mantiene cinco capacidades y cero hallazgos prohibidos, rutas locales o CodeView. El SHA-256 del manifiesto del payload es `b14b73a7a737c72966d557cf0ff43e84b033e4a4d606653eeb1fd01f049cd135`.

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

Última revisión de la documentación contra la fuente y la publicación: **2026-09-24**. Versión pública actual: `v0.2.0-beta.6`, secuencia 7; beta 5 queda como evidencia histórica. 7.2b requiere un consentimiento separado de un solo uso y contiene sondeo agregado WGC/D3D11 acotado, evidencia cifrada de cobertura/actividad y presentación fail-closed solo para `SystemOutput`; no modifica transcripción, `SpeakerName` ni exportaciones. Los perfiles Meet/Teams permanecen `Unvalidated`, se abstienen y muestran **No disponible**. La validación física WGC/GPU/interfaz/accesibilidad/Meet/Teams/2 h/5 h, la firma, la ejecución productiva del Setup, la cancelación humana, otra máquina o cuenta y 7.2c–7.2e siguen pendientes.
