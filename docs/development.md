# Desarrollar y empaquetar Trazio

**Usa el script de publicación combinada para obtener una aplicación ejecutable.** Compilar o publicar solo el proyecto WPF no coloca el proceso de inferencia junto a él.

## Requisitos y estructura

- Windows 11 x64 y un SDK .NET 10; C# 14 está configurado en los proyectos fuente.
- Una CPU x64 compatible con el entorno de ejecución CPU de Whisper incluido.
- PowerShell. Opcional: Inno Setup 6 para la definición del instalador.
- Un modelo GGML confiable y WAV de prueba autorizado solo para comprobaciones reales de inferencia.

```text
Directory.Build.props             metadatos de versión compartidos de aplicación/proceso auxiliar
src/
  Trazio.AsistenteReunion.App/     escritorio WPF, captura, revisión y reproducción
  Trazio.AsistenteReunion.Core/    contratos, almacenes, protección y recuperación
  Trazio.AsistenteReunion.Worker/  proceso Whisper local
tests/                           comprobaciones automatizadas
installer/                       publicación, pruebas básicas de proceso/inferencia, Inno Setup
docs/                            documentación de ingeniería
artifacts/publish/                aplicación combinada generada; ignorada por Git
```

El repositorio actual no fija un parche de SDK en `global.json`, no incluye archivo de bloqueo de dependencias versionado, flujo de CI ni actualizador automático. No describas evidencia de pruebas locales como evidencia de CI.

## Compilar

Ejecuta desde la raíz del repositorio:

```powershell
dotnet restore .\Trazio.AsistenteReunion.slnx
dotnet build .\Trazio.AsistenteReunion.slnx -c Release --no-restore
```

Sigue la [guía de validación](validation.md) para las pruebas, incluido el problema conocido de limpieza paralela de SQLite. Cierra Trazio normalmente antes de probar el comportamiento de instancia única.

## Publicar una carpeta completa para Windows

Cierra primero la aplicación; no finalices la grabación activa de otra persona.

```powershell
.\installer\publish.ps1
```

[El script](../installer/publish.ps1):

1. Reemplaza únicamente `artifacts\publish` después de comprobar que esté dentro de `artifacts`.
2. Publica App y Worker como `win-x64`, autocontenidos, **no como archivo único**, en la misma carpeta.
3. Copia el README y los avisos de terceros.
4. Comprueba ejecutables/dependencias necesarios, coincidencia de versiones `0.2.0-beta.4` y el manifiesto de capacidades del paquete. El manifiesto `trazio-capabilities.json`, versionado junto al proyecto WPF y copiado al publicar, sustituye el escaneo frágil de cadenas dentro de la DLL; la publicación exige **exactamente las cinco capacidades existentes** de historial, audio cifrado, exportación Obsidian, asociación de proveedor por ventana y captura efímera consentida. La validación rechaza identificadores duplicados o versiones inválidas. La infraestructura fuente 7.2b no agrega una capacidad empaquetada de actividad/correlación anónima ni de identificación de hablantes: sus perfiles de producción permanecen `Unvalidated` y fallan de forma segura.
5. Rechaza tipos y metadatos no incluidos en la lista permitida del layout, incluidos datos de usuario, bases SQLite, audio, modelos GGML/GGUF, imágenes, video, volcados, registros y material de claves. Los recursos visuales legítimos futuros requieren una autorización explícita en el empaquetado; no se aceptan por extensión de manera silenciosa. El ZIP final también debe inspeccionarse antes del SHA-256 y la subida.
6. Ejecuta la comprobación de salud del proceso auxiliar mediante canal con nombre.

Ejecuta `artifacts\publish\Trazio.AsistenteReunion.exe`. Una comprobación de salud demuestra inicio/respuesta del proceso auxiliar, **no** carga de modelo, captura ni reconocimiento; usa la [prueba básica de inferencia](validation.md#pruebas-básicas-de-paquete-e-inferencia-real) para ese límite independiente.

El script actual de empaquetado copia el README y los avisos, no `docs/`. El README incluye un índice de documentación en línea para quienes leen desde el ZIP.

### Instalador opcional

Después de publicar, con Inno Setup 6 instalado:

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" .\installer\Trazio.AsistenteReunion.iss
```

[La definición](../installer/Trazio.AsistenteReunion.iss) instala por usuario sin elevación y produce `artifacts\installer\Trazio-Asistente-Reunion-Setup.exe`. Su presencia no demuestra validación de actualización/reversión. La base pública ofrece un ZIP; la firma de código de producción no está configurada.

## Disciplina de versiones y publicación

Versión fuente: **0.2.0-beta.4** (`VersionPrefix` 0.2.0 + `VersionSuffix` beta.4); versión de ensamblado/archivo: **0.2.0.0**. [Directory.Build.props](../Directory.Build.props) es la autoridad compartida de versión. El script de publicación, la definición del instalador y las [pruebas de versión](../tests/Trazio.AsistenteReunion.Tests/VersionMetadataTests.cs) también contienen comprobaciones; actualízalos juntos para una nueva versión.

La beta 4 publicada completó `VersionMetadataTests` (4/4), `Area=VisualCapture` (132/132), el conjunto Release serial (371/371), el conjunto Release paralelo predeterminado (371/371) y la compilación (0 advertencias, 0 errores). También aprobaron el contrato de publicación, la prueba básica por canal con nombre y la comparación del layout (494/494 archivos byte a byte, 0 hallazgos prohibidos, 0 rutas fuente locales y 0 referencias CodeView). El tag `v0.2.0-beta.4` corresponde al commit `f871f20c3bf9e77b0cf9ad51134febb83c673de7`. El recurso remoto `Trazio-Asistente-Reunion-v0.2.0-beta.4-win-x64.zip` mide 86,823,005 bytes y su digest coincide exactamente con el ZIP local y el archivo lateral publicado: SHA-256 `c08d6d6df3d986d19773c6a0d3723c587c7449a37b3d1d29ef601a936768c0d6`.

- [ ] Registrar commit, versión, evidencia de pruebas y límites de validación pendientes.
- [ ] Publicar ambos ejecutables; verificar inferencia real antes de afirmar que un modelo funciona.
- [ ] Empaquetar **toda** la salida, incluidos subdirectorios nativos del entorno de ejecución/avisos.
- [ ] Excluir símbolos de depuración innecesarios y todos los datos de reuniones, claves, credenciales, registros y evidencia específica del desarrollador.
- [ ] Generar SHA-256; verificar tamaño/hash del recurso subido contra el ZIP local.
- [ ] Mantener una versión preliminar hasta cumplir la [aceptación de producción](validation.md#aceptación-manual-de-versiones).
- [ ] No inventar una licencia de aplicación ni distribuir modelos sin revisar sus condiciones.

Ejemplo de suma de comprobación para un ZIP preparado:

```powershell
Get-FileHash -Algorithm SHA256 .\artifacts\Trazio-Asistente-Reunion-v0.2.0-beta.4-win-x64.zip
```

El recurso publicado es `Trazio-Asistente-Reunion-v0.2.0-beta.4-win-x64.zip` junto a `Trazio-Asistente-Reunion-v0.2.0-beta.4-win-x64.zip.sha256`. El ZIP mide 86,823,005 bytes y su SHA-256 es `c08d6d6df3d986d19773c6a0d3723c587c7449a37b3d1d29ef601a936768c0d6`; el tamaño y digest del recurso remoto coinciden exactamente. Este comando solo permite volver a comprobar un archivo local: no crea un ZIP ni una versión publicada. Publicar, firmar y enviar cambios requieren autorización explícita del mantenedor.

## Límites de contribución

- Los textos de la interfaz y la documentación pública se mantienen en español; los identificadores técnicos se mantienen en inglés.
- Prefiere cambios de comportamiento acotados, pruebas asociadas y un mapa claro de evidencia.
- Preserva datos del usuario y trabajo ajeno; no uses limpieza destructiva como reparación.
- Nunca incluyas en commits transcripciones, WAV, archivos de audio, bases de datos, modelos, credenciales, claves ni capturas privadas. El cifrado no vuelve aptos los datos privados para un repositorio público.
- Preserva texto original, procedencia de correcciones y revisiones del modelo. No reescribas evidencia silenciosamente ni sugieras que un glosario entrena Whisper.
- Usa commits convencionales; sin atribución de IA ni líneas `Co-Authored-By`.
- Indica si la evidencia es automatizada, solo de código fuente, de dispositivos físicos o de larga duración.

La licencia de la aplicación sigue sin decidirse. Consulta al mantenedor antes de reutilizar/redistribuir el código como si tuviera licencia MIT. Consulta los [avisos de terceros](../THIRD-PARTY-NOTICES.md) para las condiciones de dependencias.
