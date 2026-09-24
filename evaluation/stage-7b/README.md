# Evaluación visual sintética de la etapa 7.2b

Este directorio contiene un corpus reproducible de regresión para el detector de actividad visual anónima y su correlación temporal. Su objetivo es detectar cambios involuntarios en la lógica determinista sin activar captura visual real ni modificar los perfiles de producción.

## Archivos

- [`synthetic-corpus-v1.json`](synthetic-corpus-v1.json): entradas sintéticas agregadas con esquema de corpus versión 1.
- [`synthetic-corpus-v1.golden.json`](synthetic-corpus-v1.golden.json): informe canónico esperado con esquema de informe independiente versión 1.

Los valores de política del candidato son **entradas sintéticas de regresión tomadas de pruebas existentes**. No son umbrales recomendados, calibrados ni habilitados para producción. El evaluador no selecciona un mejor candidato, no ordena alternativas, no acepta políticas y no promueve configuración alguna.

## Contrato de privacidad

El corpus admite exclusivamente vectores agregados por observación:

- coincidencia de realce en partes por millón;
- proporción no negra en partes por millón;
- luminancia media en partes por millón.

No contiene píxeles, coordenadas de parches, canales RGB/BGRA, imágenes, capturas de pantalla, OCR, nombres, texto transcrito, URL, identificadores de ventana o proceso, títulos, datos DOM ni descripciones libres. Los identificadores son tokens numéricos opacos. El informe no reproduce los vectores de entrada ni incorpora rutas, marcas de tiempo de ejecución, datos de máquina, GUID o identificadores internos de sesión.

## Ejecución

Desde la raíz del repositorio:

```powershell
dotnet run --project tools/Trazio.AsistenteReunion.VisualEvaluation -c Release -- evaluate --corpus evaluation/stage-7b/synthetic-corpus-v1.json
```

Para verificar que el resultado sigue siendo exactamente el aprobado:

```powershell
dotnet run --project tools/Trazio.AsistenteReunion.VisualEvaluation -c Release -- verify --corpus evaluation/stage-7b/synthetic-corpus-v1.json --golden evaluation/stage-7b/synthetic-corpus-v1.golden.json
```

La verificación compara bytes canónicos por bloques y rechaza de inmediato cualquier longitud distinta, sin cargar el golden completo en memoria. Devuelve `VE000 verified` cuando coinciden y `VE200 golden-mismatch` cuando existe una diferencia. Nunca reescribe el golden ni muestra rutas o contenido en el error. El golden debe ser UTF-8 sin BOM y usar únicamente saltos LF; una regla específica de `.gitattributes` conserva ese formato también en checkouts de Windows.

## Escenarios cubiertos

| ID | Regresión sintética |
| --- | --- |
| `scenario-001` | Actividad estable con esperas de entrada y salida, tramo inactivo y exclusión de micrófono. |
| `scenario-002` | Disponibilidad completa sin actividad. |
| `scenario-003` | Ruido dentro de la banda de histéresis. |
| `scenario-004` | Características coherentes contradictorias. |
| `scenario-005` | Cantidad insuficiente de características coherentes. |
| `scenario-006` | Fuente explícitamente no disponible. |
| `scenario-007` | Brecha de observaciones superior al máximo. |
| `scenario-008` | Cambio de revisión de superficie. |
| `scenario-009` | Límite exacto de espera de activación. |
| `scenario-010` | Instante inmediatamente anterior al límite de activación. |
| `scenario-011` | Límite exacto de espera de liberación. |
| `scenario-012` | Instante inmediatamente anterior al límite de liberación; falso positivo deliberado. |
| `scenario-013` | Cobertura parcial exactamente en el mínimo. |
| `scenario-014` | Cobertura parcial inmediatamente bajo el mínimo. |
| `scenario-015` | Solapamiento de actividad exactamente en el mínimo y apenas por debajo. |
| `scenario-016` | Cierre diferido de intervalos y latencia de primera coincidencia medida por checkpoints. |

## Significado de las métricas

- `match`: actividad remota esperada que obtiene coincidencia.
- `miss`: actividad remota esperada que queda con evidencia insuficiente.
- `falseMatch`: coincidencia durante una verdad inactiva o no disponible.
- `abstain`: correlación que se abstiene por evidencia contradictoria, incompleta o incompatible.
- `unavailable`: ausencia explícita de cobertura visual disponible.
- `insufficient`: evidencia disponible que no alcanza la actividad mínima para una verdad no activa.
- `microphoneExcluded`: segmento de micrófono excluido por contrato.

Las tasas se expresan en partes por millón y usan denominadores explícitos. Una razón `0/0` se representa como `null`. La latencia `p50`, `p95` y máxima se calcula mediante rango más cercano sobre checkpoints del corpus, nunca con reloj de pared. Los contadores de recursos registran llamadas, observaciones, celdas agregadas e intervalos retenidos.

## Revisión del golden

1. Ejecutar `verify` antes de cambiar el corpus o el evaluador.
2. Si hay una diferencia, inspeccionar el informe generado y confirmar que el cambio corresponde a una modificación intencional de contrato o lógica.
3. Revisar propiedades, métricas, latencias, contadores y contrato de privacidad.
4. Regenerar el golden solo como una modificación explícita y revisable; `verify` nunca lo actualiza automáticamente.
5. Volver a ejecutar las pruebas enfocadas y `verify` en al menos dos configuraciones culturales.

## Límites

El cargador limita el corpus a 2 MiB, profundidad JSON 24, 8 suites, 16 candidatos por suite, 128 escenarios por suite, 4.096 observaciones, 512 segmentos, 16 características por observación y 8.192 intervalos generados por escenario. Además, aplica presupuestos deterministas por escenario de 16.384 unidades de trabajo de características y 8.192 unidades de correlación antes de las operaciones costosas.

Esta evidencia es exclusivamente sintética. **No constituye calibración física, aceptación de producción ni validación de Meet o Teams reales.** Continúan pendientes las pruebas físicas de WGC/GPU/accesibilidad, los recorridos reales en ambas plataformas y las sesiones sostenidas de 2 y 5 horas descritas en el [plan visual](../../docs/stage-7-visual-speaker-plan.md) y en la [evidencia de validación](../../docs/validation.md).
