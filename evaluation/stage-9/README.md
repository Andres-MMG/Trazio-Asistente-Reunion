# Evaluación privada de transcripciones (etapa 9.2)

La herramienta compara **los mismos audios consentidos** entre configuraciones. No ejecuta modelos ni lee las reuniones guardadas por Trazio: recibe hipótesis y mediciones obtenidas por separado. Hoy no existe un corpus real del proyecto; los tests usan únicamente datos sintéticos.

## Uso

1. Guarda fuera del repositorio una prueba de consentimiento vigente y específico para `stage-9-offline-transcription-evaluation`, los WAV y un manifiesto con nombre `*.evaluation.private.json`. Obtén la aprobación humana del texto de referencia y de la etiqueta de significado de **cada** hipótesis.
2. Calcula SHA-256 de la prueba de consentimiento, cada WAV, modelo y configuración. Usa rutas relativas al directorio del manifiesto; no se aceptan enlaces, uniones de carpetas ni rutas externas, incluso en los directorios ancestros.
3. Ejecuta desde la raíz del repositorio:

   ```powershell
   dotnet run --project tools/Trazio.AsistenteReunion.TranscriptEvaluation -- --manifest "D:\corpus-privado\piloto.evaluation.private.json" --output "D:\corpus-privado\reporte.evaluation.private.json"
   ```

La salida debe ser una ruta nueva: la herramienta no sobrescribe archivos. Revisa [el ejemplo de esquema](manifest.example.json), que contiene solo nombres y hashes ficticios; no es un corpus utilizable.

## Qué se mide

| Campo | Interpretación |
|---|---|
| WER/CER | Distancia de edición agregada / número de palabras o caracteres de referencia. CER excluye espacios. Puede superar 1. |
| `meaningErrorCount` y `meaningErrorKinds` | Juicios humanos separados de WER/CER; incluye negaciones, cifras, fechas y nombres. Una frase más legible no se considera fiel por sí sola. |
| Latencia p50/p95 y memoria p50/p95 | Percentiles interpolados de **mediciones suministradas**, no mediciones realizadas por esta herramienta. Memoria = pico de working set en bytes. |

El vocabulario cerrado `stage9-meaning-errors-v1` admite exactamente `negation`, `number`, `date`, `name`, `omission`, `unsupported_fact` y `other`. No traduzcas ni cambies mayúsculas en `errorKinds`: el manifiesto identifica la versión y rechaza etiquetas desconocidas o duplicadas.

Para limitar el costo de la distancia de edición, cada referencia e hipótesis admite hasta 4096 caracteres UTF-16, cada par hasta 2 millones de celdas estimadas y el manifiesto completo hasta 50 millones. Si supera un límite, divide el corpus en lotes independientes, sin cambiar el conjunto de fragmentos entre configuraciones del mismo lote.

La normalización `es-cl-v1-lowercase-nfc-alphanumeric` convierte a minúsculas, aplica Unicode NFC, conserva letras (incluidos acentos) y dígitos, y separa signos por espacio. No elimina muletillas, negaciones ni cambia números. Se exige una matriz completa: cada configuración debe aportar exactamente una hipótesis por fragmento. Así se evita comparar configuraciones sobre subconjuntos distintos.

Registra en `tags` escenarios como `conversacion`, `ruido`, `tecnico`, `autocorreccion`, `nombre`, `cifra` y `negacion`; agrega variedad real solo con consentimiento. `transcriptionKind=acoustic` distingue Whisper/Qwen-ASR de `refined`, una reescritura textual: esta última **no** constituye una segunda escucha del audio.

## Privacidad y límites

- `*.evaluation.private.json`, `evaluation/stage-9/private/` y audio están ignorados por Git. Verifica además `git status` antes de publicar. No copies muestras, referencias, hipótesis, consentimiento ni reportes reales al repositorio.
- El programa valida vigencia, ámbito, identificador de consentimiento por fragmento y hash de su archivo de evidencia, pero **no puede verificar jurídicamente** que el documento pruebe consentimiento suficiente. Esa comprobación corresponde a una persona responsable.
- No hay llamadas de red, lectura automática de SQLite ni ejecución de ASR/Laya/Jev. Los números son útiles solo si los modelos se midieron en el mismo hardware, sobre los mismos fragmentos y con la misma política de medición. El hash de configuración y el identificador de hardware hacen visible esta procedencia, pero no la prueban por sí solos.
- El encabezado RIFF/WAVE y SHA-256 se validan; este arnés no certifica codec/duración ni detecta una modificación concurrente maliciosa del sistema de archivos. Mantén el corpus privado en una carpeta local controlada. Los errores del sistema de archivos se muestran sin rutas privadas en la consola.

## Estado

La base de evaluación está lista para recibir un corpus **consentido**. No hay resultados de calidad reales ni pruebas de reuniones de dos y cinco horas; esas pruebas siguen pendientes antes de declarar aptitud para producción.
