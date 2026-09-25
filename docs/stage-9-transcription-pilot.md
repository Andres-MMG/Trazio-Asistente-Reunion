# Piloto de transcripción mejorada — etapa 9

**Estado:** implementación experimental en el código fuente, no incluida en la versión pública `v0.2.0-beta.15`. La beta publicada solo incluye la configuración 9.1; sus garantías y resultados de prueba no se extienden a este piloto. No hay todavía un corpus consentido ni resultados reales de precisión, inferencia física de Laya/Qwen/Jev, prueba de instalación del piloto o reuniones verificadas de dos y cinco horas.

## Preparación reproducible desde el código fuente

Esta preparación **no corresponde al ZIP/Setup publicado** y no se ejecuta automáticamente. Revisa el origen de cada ejecutable y usa solo reuniones de prueba consentidas.

1. **Laya:** instala [Node.js](https://nodejs.org/en/download) **20 o superior**. Comprueba que la revisión de código que pruebas contiene el archivo tools/Trazio.Laya.Sidecar/package-lock.json; desde tools/Trazio.Laya.Sidecar ejecuta manualmente `npm ci` para instalar las dependencias fijadas por ese lockfile. Esta instalación puede usar la red; Trazio no la realiza. El directorio elegido debe contener server.mjs, package.json, package-lock.json y node_modules con @receptron/laya y @huggingface/tokenizers.
2. Obtén por separado un **paquete ONNX completo** desde [receptron/laya-onnx](https://huggingface.co/receptron/laya-onnx/tree/main), vinculado por la [documentación de @receptron/laya](https://github.com/receptron/laya#options), o sigue su procedimiento oficial de exportación. La carpeta del modelo que valida Trazio debe contener al menos estas cinco rutas no vacías: `laya.onnx`, `laya.onnx.data`, `laya_config.json`, `tokenizer/tokenizer.json` y `tokenizer/tokenizer_config.json`. El repositorio original [convaiinnovations/laya](https://huggingface.co/convaiinnovations/laya/tree/main) contiene el checkpoint fuente: no confundas sus archivos con el paquete ONNX que espera el sidecar. Fija y anota la revisión exacta que descargaste.
3. En **Inteligencia → Evaluación Laya local**, indica rutas locales absolutas de node.exe, carpeta tools/Trazio.Laya.Sidecar y carpeta ONNX, además de la versión/revisión exacta del modelo; pulsa **Guardar Laya local**. No se admiten rutas de red ni enlaces simbólicos. El paquete ONNX ocupa aproximadamente **1,7 GB** y la documentación del adaptador estima alrededor de **2 GB de RAM** para el modelo cargado, más memoria adicional por lote; comprueba capacidad y espacio antes de probar.
4. **Qwen3-ASR:** aporta un llama-server.exe de [llama.cpp](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md), un modelo Qwen3-ASR GGUF y su proyector multimodal compatible `mmproj*.gguf`. En **Inteligencia → Qwen3-ASR local**, selecciona sus tres rutas locales absolutas y guarda la configuración. La aplicación exige esos archivos, pero su mera extensión o hash **no demuestra compatibilidad ni autenticidad**; todavía no se ha verificado un par concreto de pesos/ejecutable ni su rendimiento en este piloto. Antes de cada ejecución, revisa la autorización que muestra las rutas y huellas de los binarios/modelos.

No se distribuyen Node, dependencias, pesos Laya/Qwen ni llama-server con beta 15. Los costos de disco/memoria de Qwen dependen de los archivos aportados y del equipo; aquí no existe una medida validada. Preparar estos componentes no constituye prueba de inferencia real ni de estabilidad de reuniones de dos/cinco horas.

## Flujo manual

1. Conserva la transcripción original de Whisper y el audio cifrado. En **Historial**, selecciona una reunión y un segmento original con audio disponible. El micrófono y el audio del equipo siguen siendo fuentes distintas.
2. En **Inteligencia**, configura un generador compatible con chat completions. Puede ser un servicio local o una API externa; no depende de una marca de modelo. **Generar propuestas** crea hasta dos textos conservadores, o ninguno. La segunda propuesta solo se solicita ante ambigüedad. El contexto cercano y el diccionario aprobado tienen límites; una propuesta es una interpretación textual, no una segunda escucha.
3. Si el destino es remoto, revisa la URL, modelo y **JSON exacto** mostrado antes de autorizar ese envío. Cancelar no envía la solicitud. La grabación no depende de esta operación.
4. Opcionalmente, solicita Qwen3-ASR sobre el **mismo intervalo** del audio original. Esta es una hipótesis acústica independiente con procedencia de modelo/configuración; no reemplaza Whisper ni la corrección humana. El proceso local `llama-server` y sus modelos se instalan aparte y requieren autorización específica antes de descifrar el audio para esa operación.
5. Opcionalmente, evalúa con Laya instalado localmente. La comparación conjunta puede considerar Whisper, una revisión Qwen elegida y propuestas del mismo segmento. Muestra también **mantener original** y **revisión humana**. Los juicios y probabilidades son señales sobre textos, no una verificación acústica. Un fallo o baja confianza no acepta texto automáticamente.
6. Si Laya no está instalado o una comprobación **nueva** confirma indisponibilidad/capacidad insuficiente, se puede pedir manualmente Jev con una clave separada. Antes de cada petición remota se muestra su JSON exacto, destino y modelo para consentimiento. La incertidumbre, contexto excesivo o respuesta inválida de Laya no activan este respaldo; Jev no evalúa la comparación conjunta con Qwen en esta primera entrega.
7. Revisa **Original / Propuesta / Cambios**, escucha el fragmento y acepta, edita mediante la corrección humana existente o rechaza. Las alertas de posibles nombres, cifras, fechas y negaciones son heurísticas: no sustituyen escuchar el audio. Ninguna recomendación cambia el texto aprobado sin una acción humana; original, audio y versiones anteriores se conservan.

Las propuestas, decisiones y evaluaciones se vinculan al segmento y se cifran de forma aditiva en SQLite. La procedencia distingue una observación acústica de una reescritura textual. La cancelación o fallo del análisis no debe detener la captura; ante ausencia de evaluador autorizado las propuestas quedan sin evaluar.

## Preparar una evaluación privada (9.2)

El [arnés offline](../evaluation/stage-9/README.md) recibe **hipótesis y mediciones ya obtenidas**: no ejecuta modelos, no abre automáticamente la base de reuniones y no extrae audio. Conserva fuera del repositorio consentimiento vigente, WAV y referencia aprobada por una persona. Usa exactamente los mismos fragmentos para Whisper, Whisper refinado y Whisper + Qwen, y mide WER/CER, latencia y memoria por configuración. Los errores de significado, en particular negaciones, nombres, fechas, cifras y hechos no respaldados, requieren etiquetas humanas separadas: una redacción más elegante no cuenta como mayor fidelidad.

Desde la raíz del repositorio, con un manifiesto privado válido:

```powershell
dotnet run --project tools/Trazio.AsistenteReunion.TranscriptEvaluation -- --manifest "D:\corpus-privado\piloto.evaluation.private.json" --output "D:\corpus-privado\reporte.evaluation.private.json"
```

El [ejemplo de manifiesto](../evaluation/stage-9/manifest.example.json) es sintético y no acredita consentimiento ni calidad. No publiques audios, referencias, hipótesis ni informes privados. Antes de compartir código, revisa `git status` y la [guía de seguridad](security.md#piloto-experimental-de-la-etapa-9).

## Límites para decidir una publicación

- El piloto es **manual y experimental**. No contiene un tercer transcriptor ni aceptación automática. Resúmenes, compromisos, traducción y temas se retoman después de evaluar la fidelidad, siempre vinculados a la versión elegida y su evidencia.
- El control de loopback, token, PID y huella de archivos protege el canal de la aplicación hacia procesos locales; **no garantiza** que un `node.exe`, `server.mjs`, `llama-server.exe`, dependencia o modelo aportado por el usuario sea confiable o no acceda a la red. El sidecar Node y llama.cpp no están incluidos ni certificados en el instalador publicado.
- La continuidad de audio nueva usa muestras y épocas de captura y una tolerancia provisional de reloj de **40 ms**; no certifica que el hardware no pierda muestras. Los archivos anteriores sin metadatos de continuidad mantienen un rechazo estricto cuando el intervalo cruza límites ambiguos.
- Antes de liberar esta etapa faltan pruebas de inferencia real con modelos instalados, revisión audible de segmentos y diferencias, validación de consentimiento/privacidad en hardware, métricas sobre corpus consentido, estabilidad de dos y cinco horas, paquete/Setup y accesibilidad física. Un build o prueba sintética no sustituye estos resultados.

La [hoja de ruta](../ROADMAP.md#etapa-9--inteligencia-de-reuniones-opcional) mantiene después la etapa 10 (integración organizacional), 11 (calendarios) y 12 (entrenamiento).
