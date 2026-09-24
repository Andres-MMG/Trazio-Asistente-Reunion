# Documentación de ingeniería

**Trazio Asistente Reunión** es un producto de escritorio para Windows, independiente de Trazio Platforms. La versión publicada actual es [`0.2.0-beta.4`](https://github.com/Andres-MMG/Trazio-Asistente-Reunion/releases/tag/v0.2.0-beta.4); incorpora 7.2a y la infraestructura fuente de 7.2b sin afirmar identificación de hablantes ni validación de producción.

El código fuente está preparado como candidato local `0.2.0-beta.5`. Su verificación independiente aprobó metadatos **4/4**, evaluación visual **74/74**, captura visual **206/206**, Release serial/paralelo **446/446**, compilación limpia, CLI `VE000`, `publish` local y smoke IPC integrado/explícito. El layout preliminar **495/495** incluye el ensamblado puro `VisualAnalysis` y excluye la CLI offline `VisualEvaluation`, corpus/golden y directorios `tools`/`evaluation`; mantiene cinco capacidades y cero hallazgos prohibidos, rutas locales o CodeView. Beta 4 sigue siendo la única descarga pública: falta reconstruir el ZIP desde el commit de preparación y registrar tamaño/SHA-256 finales, SHA de preparación, tag y release beta 5.

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

Última revisión de la documentación contra el código fuente y la publicación: **2026-09-23**. Versión pública actual: `v0.2.0-beta.4`; candidato solo local/fuente: `0.2.0-beta.5`. 7.2b requiere un consentimiento separado de un solo uso y contiene sondeo agregado WGC/D3D11 acotado, evidencia cifrada de cobertura/actividad y presentación fail-closed solo para `SystemOutput`; no modifica transcripción, `SpeakerName` ni exportaciones. Los perfiles Meet/Teams permanecen `Unvalidated`, se abstienen y muestran **No disponible**. La evidencia automatizada y de publicación se detalla en el [registro de validación](validation.md#evidencia-actual); la validación física WGC/GPU/interfaz/accesibilidad/Meet/Teams/2 h/5 h, la firma y 7.2c–7.2e siguen pendientes.
