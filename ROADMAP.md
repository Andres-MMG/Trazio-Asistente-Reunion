# Trazio Asistente Reunión — Hoja de ruta del producto

- Fecha del estado: 2026-09-22
- Madurez actual: MVP funcional avanzado / versión preliminar pública
- Versión actual: [`0.1.1-mvp`](https://github.com/Andres-MMG/Trazio-Asistente-Reunion/releases/tag/v0.1.1-mvp)

Este es el plan canónico de etapas. **La etapa 6 es la línea funcional activa; el fortalecimiento de la distribución de la etapa 5 sigue pendiente.** La identidad local (5.5) está implementada, todavía sin validación física. Consulta la [documentación de ingeniería](docs/README.md) y la [evidencia de validación](docs/validation.md). El alcance futuro indicado a continuación es un objetivo, no una afirmación de que ya se distribuya.

## Principios del producto

- Captura y transcripción con prioridad local.
- Iniciar una grabación requiere una acción explícita del usuario o una regla de automatización habilitada explícitamente. Cada nueva grabación conserva audio cifrado y muestra un indicador visible persistente.
- Las transcripciones, el audio, las capturas de pantalla, las identidades y los metadatos de calendario son datos sensibles.
- La automatización debe ser configurable por cuenta/calendario y siempre debe ofrecer controles de Pausar y Detener.
- La automatización de calendarios, la selección de fuente de reunión, la atribución de hablantes y la identificación biométrica son capacidades independientes y no deben presentarse como una sola función.

## Situación actual

| Etapa | Resultado | Estado |
|---|---|---|
| 1 | Base técnica y arquitectura local | Implementada — validación de producción pendiente |
| 2 | Captura de audio de dos fuentes y transcripción local | Implementada — validación de producción pendiente |
| 3 | Transcripción cifrada e historial de audio cifrado obligatorio | Implementada — validación de producción pendiente |
| 4 | Historial utilizable y almacenamiento configurable | Implementada — validación de producción pendiente |
| 5 | Beta distribuible y mantenible | En curso |
| 5.5 | Identidad del usuario local y atribución del micrófono | Implementada — validación física de interfaz pendiente |
| 6 | Precisión, retranscripción y comparación de modelos | En curso |
| 7 | Fuente de reunión y atribución de hablantes | Planificada |
| 8 | Búsqueda, revisión por lotes, glosario global y productividad | Planificada — edición y navegación de audio básicas ya entregadas en la etapa 6 |
| 9 | Inteligencia de reuniones opcional | Planificada |
| 10 | Integración organizacional/con plataforma opcional | Futura |
| 11 | Cuentas conectadas, calendarios y automatización de reuniones | Futura |
| 12 | Entrenamiento opcional de modelos con correcciones aprobadas | Futura — etapa final |

Las pruebas físicas prolongadas de dos y cinco horas, aplazadas, siguen siendo requisitos para publicar en producción. No bloquean el desarrollo funcional de la beta interna, pero no se debe declarar el producto listo para producción sin ellas.

## Etapa 5 — Beta distribuible y mantenible

### Objetivo

Permitir instalar, actualizar, diagnosticar y recuperar la aplicación existente de forma segura en otros equipos.

### Base implementada

- Código público en GitHub, versión `0.1.1-mvp`, ZIP completo para Windows y suma de comprobación.
- Script de publicación combinada de aplicación/proceso auxiliar con comprobaciones de paquete y prueba básica de salud por canal con nombre.
- Existe la definición de Inno Setup por usuario; la validación de instalación/actualización/reversión sigue pendiente.

### Alcance pendiente

- Pasar a una versión beta como `0.2.0-beta.1`.
- Producir un instalador por usuario que detecte versiones anteriores y preserve los datos del usuario.
- Implementar actualizaciones de la aplicación completa con manifiesto firmado, verificación SHA-256, cierre controlado, reemplazo atómico y reversión.
- Actualizar la aplicación y el modelo Whisper de forma independiente cuando el modelo no haya cambiado.
- Firmar el ejecutable y el instalador.
- Exportar diagnósticos que protejan la privacidad, sin transcripciones, audio conservado, capturas, secretos ni claves de cifrado.
- Eliminar los bloqueos intermitentes de SQLite en las pruebas antes de considerar que el conjunto de pruebas es completamente determinista.

### Criterios de salida

- Una persona de pruebas puede instalar y actualizar Trazio sin copiar archivos manualmente.
- Las reuniones y configuraciones existentes sobreviven a actualizaciones, reparaciones y reversiones.
- Una actualización fallida restaura la versión funcional anterior.
- La exportación de diagnósticos no contiene contenido de reuniones ni secretos.

## Etapa 5.5 — Identidad del usuario local y atribución del micrófono

### Objetivo

Dar a cada grabación local una identidad de propietario y atribuir los segmentos transcritos del micrófono al usuario local confirmado.

### Alcance

- Agregar un perfil local con nombre visible obligatorio y organización opcional.
- Completar previamente el nombre visible desde la cuenta de Windows cuando esté disponible, pero exigir que el usuario lo confirme o cambie.
- Atribuir por defecto los segmentos transcritos del micrófono a este perfil local confirmado.
- Permitir un nombre visible específico para la reunión sin cambiar el perfil global.
- Guardar los datos del perfil cifrados con el modelo de privacidad local existente.

Esto identifica a la persona que usa el micrófono configurado. No demuestra quién está hablando físicamente por un micrófono compartido.

### Criterios de salida

- Los segmentos del micrófono muestran el nombre del usuario local confirmado.
- Un nombre específico para una reunión no modifica el perfil global.
- Los datos del perfil permanecen cifrados y sobreviven al reinicio.
> Estado de implementación de la etapa 5.5: perfil local, nombre por reunión, persistencia cifrada y atribución del micrófono implementados. Las pruebas automatizadas de identidad pasan; la validación física de interfaz y captura sigue pendiente.

## Etapa 6 — Precisión, espacio de corrección, glosario y retranscripción

**La etapa 6 sigue en curso.** El ciclo de revisión funcional está implementado en gran parte; la aplicación del glosario y la validación medible de calidad no.

| Línea de trabajo | Implementado ahora | Todavía pendiente |
|---|---|---|
| 6A — Revisión | Forma de onda/línea de tiempo por fuente, reproducción/pausa y saltos de 10 segundos, audio por segmento, correcciones/deshacer por anexado, exportación TXT del original efectivo | Control de velocidad, controles específicos de segmento anterior/siguiente, resaltado completo según reproducción |
| 6A — Glosario | Sugerencias por término después de ediciones humanas, guardado explícito y procedencia de corrección cifrada | Aplicar términos a las instrucciones de Whisper, reemplazo determinista con vista previa, gestión ampliada del glosario |
| 6B — Retranscripción | Audio cifrado conservado por fuente → revisión cifrada independiente del modelo; estado auditable de cancelación/fallo y hash del modelo | Perfiles de calidad y evaluación lingüística representativa |
| 6B — Comparación | Original revisado frente a revisión exitosa del modelo, o dos revisiones exitosas de la misma fuente; intervalos de 15 segundos y accesos al audio | Métricas reproducibles de precisión; las diferencias visuales no son una puntuación de precisión |

### Objetivo

Convertir cada conversación guardada en un espacio de revisión donde el usuario pueda escuchar, inspeccionar, corregir y reutilizar terminología para mejorar futuras transcripciones.

### Ruta rápida

1. Abrir cualquier conversación guardada desde Historial.
2. Ver su transcripción con la misma presentación por segmentos utilizada en Sesión en vivo.
3. Seleccionar un segmento para navegar y reproducir el audio conservado correspondiente.
4. Corregir el texto en el mismo lugar y preservar tanto la versión original como la corregida.
5. Agregar al glosario un nombre o término técnico corregido cuando deba aplicarse a conversaciones futuras.
6. Retranscribir el audio conservado en una nueva revisión y comparar resultados sin sobrescribir el trabajo anterior.

### Espacio de revisión de sesiones objetivo (parcialmente implementado)

- Abrir cada conversación guardada en una vista completa de transcripción, en vez de un bloque de texto de solo lectura.
- Mantener visibles por segmento las marcas de tiempo, la fuente, la identidad local y, posteriormente, la atribución del hablante remoto.
- Seleccionar un segmento para navegar a su posición de audio y reproducir desde ese punto.
- Proporcionar controles de anterior, reproducir/pausar, siguiente, velocidad y repetición breve para el trabajo de corrección.
- Resaltar el segmento que se está reproduciendo.
- Admitir corrección en el mismo lugar con Guardar y Deshacer.
- Preservar texto original inmutable, texto corregido, identidad del editor, marca de tiempo e historial de revisiones.
- Permitir corregir solo el texto cuando no exista audio conservado.
- Explicar claramente que la reproducción y retranscripción no están disponibles para sesiones antiguas sin audio cifrado o cuyo audio conservado fue eliminado por retención.

### Glosario y memoria de correcciones objetivo (parcialmente implementados)

El glosario es una ayuda terminológica controlada, no entrenamiento automático del modelo.

Cada entrada puede contener:

- Escritura preferida, por ejemplo Trazio.
- Formas incorrectas frecuentes o alias.
- Categoría como persona, organización, producto, sigla, término médico o término técnico.
- Idioma y alcance de reunión opcionales.
- Estado activo/inactivo y cantidad de usos.
- Enlace a la corrección que creó la entrada.

Desde un segmento corregido, el usuario puede elegir:

- Corregir solo este segmento.
- Reemplazar el mismo error en esta reunión después de previsualizar cada coincidencia.
- Agregar al glosario el término preferido y la forma incorrecta para futuras reuniones.

Aplicar el conocimiento del glosario de dos formas acotadas:

1. Incluir una lista de tamaño limitado de nombres propios y términos técnicos relevantes en las instrucciones iniciales de Whisper.
2. Ejecutar posprocesamiento determinista solo para reglas explícitas de alias a forma preferida, con límites de palabra, vista previa y posibilidad de deshacer.

Nunca reemplazar silenciosamente palabras comunes ambiguas. La salida original del modelo debe seguir siendo recuperable.

### Límite del aprendizaje

Guardar una corrección no reentrena ni modifica permanentemente el modelo Whisper. La mejora operativa planificada consiste en reutilizar términos, alias y correcciones confirmados del glosario. La versión actual guarda esa evidencia, pero todavía no la aplica a la inferencia. El aprendizaje real del modelo requiere un conjunto de audio/texto preparado por separado, ajuste fino del modelo, evaluación y una nueva versión del modelo. Se aplaza intencionalmente hasta la etapa 12, después de la automatización de calendarios.

### Comparación de modelos y retranscripción

- Retranscribir una reunión guardada a partir del audio cifrado conservado.
- Preservar la transcripción original y cada corrección humana.
- Crear una nueva revisión de salida del modelo en vez de reemplazar la transcripción existente.
- Comparar modelos Whisper usando exactamente la misma muestra de audio en español.
- Proporcionar perfiles de calidad sencillos: Rápido, Equilibrado y Preciso.
- Mostrar modelo, idioma, versión del glosario, tiempo de procesamiento y revisión de transcripción en Historial.
- Definir un conjunto repetible de evaluación de precisión con texto esperado y audio representativo del español de Chile.
- Calcular métricas útiles de corrección, como segmentos corregidos, sugerencias de glosario aceptadas y comparación de errores de palabras sobre el conjunto de evaluación.

### Criterios de salida

- Cualquier conversación guardada se abre en el espacio de revisión por segmentos.
- Las sesiones con audio conservado permiten reproducción sincronizada, navegación, corrección y retranscripción.
- Las sesiones sin audio conservado siguen permitiendo corrección de texto y actualización del glosario sin fingir que se puede recuperar el audio.
- Las correcciones preservan el texto original y se pueden deshacer completamente.
- Las reglas del glosario pueden mejorar una transcripción futura registrando qué regla se aplicó.
- La misma reunión se puede retranscribir sin volver a capturar audio ni sobrescribir revisiones anteriores.
- Las comparaciones de modelos son reproducibles y usan el mismo audio de origen.
## Etapa 7 — Fuente de reunión y atribución de hablantes

### Objetivo

Asociar una sesión con la superficie seleccionada de Meet o Teams y atribuir el habla remota cuando exista evidencia fiable.

Esta etapa se divide intencionalmente en niveles de confianza separados. Trazio nunca debe afirmar un nombre de hablante cuando la evidencia disponible solo respalde una etiqueta anónima.

### 7.1 Fuente de reunión seleccionada por el usuario

- Pedir al usuario que seleccione la pestaña del navegador o ventana de la aplicación de reunión.
- Detectar Google Meet o Microsoft Teams desde la URL de la pestaña/los metadatos de la ventana seleccionada.
- Vincular la superficie seleccionada con la sesión activa de Trazio.
- Mantener la captura de salida del sistema existente como alternativa cuando no se seleccione una superficie.

Los controles de privacidad del navegador y del sistema operativo pueden exigir que el usuario inicie el selector de fuente. La automatización de calendarios no puede omitir silenciosamente ese permiso.

### 7.2 Adaptadores de proveedores

- Construir adaptadores separados y versionados para Google Meet y Microsoft Teams.
- Una extensión complementaria del navegador puede leer señales de accesibilidad/DOM, subtítulos, etiquetas de participantes y estado del hablante activo específicos del proveedor después de un permiso explícito.
- La aplicación de escritorio sigue siendo la autoridad de grabación y almacenamiento cifrado.
- Si un adaptador deja de funcionar después de un cambio de interfaz del proveedor, continuar la transcripción con `Hablante remoto` en vez de adivinar un nombre.

### 7.3 Niveles de evidencia del hablante

Guardar confianza de atribución y tipo de evidencia para cada segmento con nombre:

1. `Perfil local`: fuente de micrófono asociada al usuario local confirmado.
2. `Metadatos del proveedor`: etiqueta de hablante activo/subtítulo obtenida desde la superficie seleccionada de Meet/Teams.
3. `Corrección del usuario`: una persona asignó o corrigió el hablante.
4. `Hablante diarizado`: la agrupación de audio produjo `Hablante 1`, `Hablante 2`, etc., sin un nombre verificado.

La diarización de audio separa voces, pero no revela nombres reales. Asociar una voz con una persona requeriría un registro explícito de voz e introduce obligaciones legales y de privacidad biométrica; queda fuera del alcance inicial de la etapa 7.

### 7.4 Capturas opcionales al cambiar de hablante

- No usar capturas de pantalla como mecanismo principal de identificación de hablantes.
- Si se habilitan, capturar solo al detectar una transición de hablante, no continuamente.
- Preferir recortar el recuadro del participante activo y la etiqueta de nombre en vez de almacenar toda la pantalla de la reunión.
- Cifrar las capturas, aplicar una retención breve y proporcionar controles independientes de eliminación.
- Obtener consentimiento explícito porque las capturas pueden contener rostros, chat, documentos compartidos u otro contenido sensible.
- Tratar la detección visual solo como evidencia complementaria; un resaltado de interfaz puede estar retrasado, ser ambiguo o incorrecto.

### Criterios de salida

- El usuario puede seleccionar una pestaña/ventana de Meet o Teams y ver el proveedor detectado.
- Trazio continúa de forma segura cuando los metadatos del proveedor no están disponibles o falla un adaptador.
- Las etiquetas de hablantes remotos con nombre incluyen evidencia y confianza; los segmentos inciertos permanecen anónimos.
- Las capturas opcionales están cifradas, acotadas y se pueden eliminar independientemente.

## Etapa 8 — Historial y productividad

- Búsqueda de texto completo entre reuniones.
- Revisión y aprobación avanzadas por lotes de revisiones de transcripción y etiquetas de hablantes.
- Gestión global del glosario, detección de duplicados, importación y exportación.
- Marcadores, notas, etiquetas e indicadores de seguimiento.
- Formatos de exportación estructurados además del texto plano.
- Evaluar Opus para archivos de audio cifrados más pequeños, preservando navegación y exportación fiables.
## Etapa 9 — Inteligencia de reuniones opcional

- Resúmenes opcionales de reuniones mediante una API LLM externa inicialmente, con dirección del proveedor y clave API configurables; después podría reemplazarse por un servicio compatible autoalojado.
- Explorar un modelo generativo que proponga preguntas candidatas sustentadas en evidencia y Jev que devuelva juicios tipados; validar y acotar esas preguntas en el código de la aplicación. Ninguna de las dos integraciones está implementada actualmente.
- Exigir activación explícita antes de que el contenido de la transcripción salga del equipo; proteger credenciales y registrar la procedencia del proveedor/modelo. La grabación y revisión locales deben continuar sin esta integración.
- Decisiones, compromisos y acciones pendientes.
- Traducción opcional.
- Segmentación por temas.
- Todo contenido generado debe enlazar a la evidencia de la transcripción y permanecer claramente marcado como generado por máquina.

## Etapa 10 — Integración organizacional/con plataforma opcional

- Sincronización con Trazio Platforms mediante activación explícita.
- Políticas organizacionales, controles de retención y despliegue administrativo.
- Plantillas compartidas y vocabulario controlado.
- Esta etapa cambia el límite de privacidad exclusivamente local y, por tanto, requiere un diseño separado de seguridad, aspectos legales, aislamiento entre organizaciones y consentimiento.

## Etapa 11 — Cuentas conectadas, calendarios y automatización de reuniones

### Objetivo

Una vez que el resto del producto local sea estable, permitir que el usuario conecte uno o varios calendarios capaces de preparar, iniciar y detener la captura de reuniones automáticamente.

### Cuentas de correo/calendario conectadas

- Admitir varias cuentas conectadas, inicialmente Google Calendar y Microsoft 365/Outlook Calendar.
- Usar autorización OAuth; nunca almacenar contraseñas de cuentas.
- Mantener separadas la identidad de cuenta, la selección de calendarios y la política de grabación.
- Permitir que el usuario seleccione qué calendarios se supervisan y elija una identidad/correo principal.
- Leer solo los campos mínimos necesarios del evento: título, inicio/fin, organizador, asistentes, proveedor y URL de acceso.
- Permitir desconectar una cuenta y eliminar sus metadatos en caché.

### Reglas de automatización de reuniones

Cada calendario conectado puede tener una regla explícita:

- Ignorar eventos.
- Solo notificar.
- Preparar Trazio y esperar confirmación.
- Iniciar captura local automáticamente a la hora programada.

La captura automática requiere activación por calendario o regla y debe mostrar un indicador persistente de grabación. Nunca debe habilitar silenciosamente la grabación solo porque se conectó una cuenta.

### Ciclo de vida automático

- Activar o iniciar Trazio antes del evento.
- Crear la sesión usando el título del evento y los metadatos del proveedor.
- Abrir opcionalmente la URL de acceso a la reunión; Trazio no suplanta al usuario ni omite una sala de espera.
- Iniciar con el desfase configurado cuando la regla de automatización lo permita.
- Detener al finalizar la hora programada más un período de gracia configurable.
- Usar el silencio sostenido solo como señal secundaria de detención, nunca como única fuente de verdad.
- Si el evento se extiende o el usuario sigue activo, ofrecer continuar.
- Finalizar el cifrado y la persistencia, luego minimizar o cerrar según la preferencia del usuario.
- Registrar por qué comenzó y terminó la sesión: acción manual, regla de calendario, fin programado, alternativa por silencio o recuperación de error.

### Criterios de salida

- Dos o más cuentas/calendarios pueden coexistir sin sesiones duplicadas.
- Un evento de calendario autorizado puede abrir, iniciar, detener y finalizar una sesión sin perder datos.
- El usuario siempre puede pausar o detener la automatización inmediatamente.
- Desconectar una cuenta elimina sus tokens y metadatos en caché sin borrar reuniones locales.
## Etapa 12 — Entrenamiento opcional de modelos con correcciones aprobadas

### Objetivo

Después de completar la automatización de calendarios, evaluar si las correcciones confirmadas de transcripciones y el audio conservado justifican entrenar un nuevo modelo de transcripción de Trazio.

### Requisitos de entrada

- Las etapas 1–11 están completas.
- Los usuarios han consentido explícitamente incluir audio y correcciones seleccionados en un conjunto de entrenamiento.
- Las correcciones tienen historial de revisiones y estado de aprobación fiables.
- El audio, los segmentos transcritos, las marcas de tiempo, el idioma, la fuente y la evidencia de hablantes se pueden exportar sin exponer contenido ajeno de reuniones.
- Existe un conjunto representativo de evaluación de español y español de Chile antes de comenzar el entrenamiento.

### Flujo de trabajo del conjunto de datos

1. Seleccionar explícitamente reuniones elegibles y segmentos corregidos.
2. Excluir muestras privadas, no aprobadas, de baja calidad o ambiguas.
3. Emparejar cada intervalo de audio con su transcripción final aprobada por una persona.
4. Eliminar ejemplos duplicados y contradictorios.
5. Dividir los datos en conjuntos de entrenamiento, validación y prueba sin intervenir.
6. Cifrar los conjuntos de datos en reposo y registrar consentimiento, procedencia y obligaciones de eliminación.
7. Entrenar un modelo candidato independiente; nunca modificar directamente el modelo de producción instalado.
8. Comparar el candidato con el modelo actual sobre el conjunto de prueba sin intervenir.
9. Publicar una nueva versión firmada del modelo solo cuando mejore la precisión sin regresiones inaceptables.
10. Conservar la posibilidad de volver al modelo anterior.

### Límites de seguridad y privacidad

- Las correcciones no entran automáticamente al entrenamiento.
- Las entradas de diccionario y las ediciones habituales de transcripción permanecen locales salvo selección y aprobación explícitas.
- El usuario puede revocar muestras no procesadas antes de publicar el conjunto de datos.
- Los datos de entrenamiento no deben contener tokens de calendario, credenciales, capturas ni metadatos ajenos de reuniones.
- Una versión del modelo debe registrar su política de datos, resultados de evaluación, cobertura lingüística y limitaciones conocidas.

### Criterios de salida

- El entrenamiento es reproducible desde un conjunto de datos versionado y consentido.
- El candidato mejora de forma demostrable las métricas de precisión acordadas.
- El nuevo modelo se versiona independientemente, está firmado, se puede instalar y revertir.
- Si no mejora la precisión, el modelo actual permanece sin cambios.
## Validación de producción aplazada

Antes de publicar en producción:

- Ejecutar la prueba controlada de solo micrófono, solo sistema y ambas fuentes.
- Ejecutar una prueba de reunión de dos horas.
- Ejecutar una prueba de reunión de cinco horas.
- Verificar CPU, memoria, crecimiento de almacenamiento, pausa/reanudación, cambios de dispositivos, recuperación de fallos, cifrado, reproducción, exportación e integridad de segmentos.
- Probar actualización y reversión del instalador en un equipo Windows limpio y en uno con reuniones existentes.

## Orden de ejecución recomendado

1. Continuar fortaleciendo la distribución de la etapa 5 desde la base existente de código público/ZIP; mantener pendientes los requisitos de producción.
2. Validar físicamente la identidad local y la atribución del micrófono implementadas en la etapa 5.5.
3. Completar aplicación del glosario, perfiles de calidad y evaluación medible de precisión de la etapa 6; conservar el flujo entregado de revisión/retranscripción/comparación.
4. Construir la selección de fuente de la etapa 7 antes de intentar atribuir nombres a hablantes remotos.
5. Agregar adaptadores de proveedores y evidencia visual opcional solo cuando el flujo de fuente seleccionada sea estable.
6. Completar las etapas 8–10 y la validación prolongada aplazada antes de publicar en producción.
7. Implementar las cuentas conectadas, calendarios y automatización de reuniones con activación voluntaria de la etapa 11.
8. Evaluar el entrenamiento opcional de modelos de la etapa 12 solo después de completar la automatización de calendarios.
