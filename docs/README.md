# Documentación de ingeniería

**Trazio Asistente Reunión** es un producto de escritorio para Windows, independiente de Trazio Platforms. La versión publicada actual es [`0.2.0-beta.10`](https://github.com/Andres-MMG/Trazio-Asistente-Reunion/releases/tag/v0.2.0-beta.10); incorpora 7.2a, la infraestructura de 7.2b, el instalador manual de la etapa 5.1, la revisión de historial 8.1, la búsqueda local 8.2, el diccionario global 8.3a y el intercambio JSON 8.3b sin afirmar identificación de hablantes ni validación de producción.

La beta 10 usa la secuencia de instalador **11**, mantiene exactamente cinco capacidades y conserva el empaquetador ZIP canónico. La firma Authenticode, la ejecución productiva del Setup y la validación física audible, importación/exportación real, WGC/GPU, interfaz/accesibilidad, Meet/Teams y 2/5 horas siguen pendientes.

La beta 10 pública incluye la revisión de historial 8.1: anterior/siguiente sin reproducción automática, línea de tiempo con huecos reales, resaltado independiente y velocidad temporal `0,75×–2×`. La velocidad cambia el tono, no se persiste y la validación audible/accesible continúa pendiente.

La etapa 8.3a publicada agrega una pestaña global de diccionario con filtro local y activación por entrada. Esta rebanada no aplica el diccionario a Whisper, no modifica transcripciones ni agrega una capacidad nueva al paquete. La firma, la ejecución productiva del Setup y la validación visual, por teclado, lector de pantalla, audible, WGC/GPU, Meet/Teams y 2/5 horas permanecen pendientes.

La etapa 8.3b está publicada en `0.2.0-beta.10`, secuencia **11**: intercambio JSON v1, vista previa en memoria, importación cifrada atómica y exportación explícita sin cifrar. Conserva duplicados históricos, señala conflictos y no fusiona ni elimina entradas automáticamente.

El tag de beta 10 resuelve a `52f8b016c277a5822e9aec269fc22cc055925a2e`. El ZIP publicado mide **86,897,741 bytes** y su SHA-256 es `52d5641af82e327bbfdf510dbd732d1dee7f13a7be5294af4b993fa1dc49a42f`; el Setup mide **60,064,281 bytes**, su SHA-256 es `ce67f9ac2c05fa5718f99ef31339f74961af9de1a23a00c4a31ed56226977f2f` y Authenticode informa `NotSigned`. El manifiesto de publicación mide **123,577 bytes**, SHA-256 `f5fb80b5dce7b32dd478f2b062de3adf40d96a2db0dc254d8b6c4e1c6d6d81f4`; el manifiesto del Setup mide **488 bytes**, SHA-256 `f867fc695aa9de37e266621de825958f8ef9967be989cdfd2829c83d4a197a01`. Los seis recursos remotos coinciden con los artefactos verificados. El Setup productivo no se ejecutó.

La verificación de beta 10 aprobó Release serial/paralelo **599/599**, la suite enfocada de 8.3b **48/48**, los contratos finales **22/22**, el harness desechable **14/14** y la compilación con **0 advertencias y 0 errores**. El layout final **495/495**, con `ProductVersion` `0.2.0-beta.10+52f8b016c277a5822e9aec269fc22cc055925a2e`, incluye el ensamblado puro `VisualAnalysis` y excluye la CLI offline `VisualEvaluation`, corpus/golden y directorios `tools`/`evaluation`; mantiene exactamente cinco capacidades. Beta 9 queda como evidencia histórica.

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

Última revisión de la documentación contra la fuente y la publicación: **2026-09-24**. Versión pública actual: `v0.2.0-beta.10`, secuencia 11; beta 9 queda como evidencia histórica. 7.2b requiere un consentimiento separado de un solo uso y contiene sondeo agregado WGC/D3D11 acotado, evidencia cifrada de cobertura/actividad y presentación fail-closed solo para `SystemOutput`; no modifica transcripción, `SpeakerName` ni exportaciones. Los perfiles Meet/Teams permanecen `Unvalidated`, se abstienen y muestran **No disponible**. La validación física audible, WGC/GPU/interfaz/accesibilidad/Meet/Teams/2 h/5 h, la firma, la ejecución productiva del Setup, la cancelación humana, otra máquina o cuenta y 7.2c–7.2e siguen pendientes.
