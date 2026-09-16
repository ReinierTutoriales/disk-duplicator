# RepartoCopier

RepartoCopier es una aplicación de escritorio para Windows que copia un origen hacia múltiples destinos simultáneamente mediante FAN-OUT, preservando la estructura completa de carpetas y priorizando integridad, recuperación y comportamiento nativo del sistema operativo.

## Versión actual

**RepartoCopier v2.1.0** es la versión estable publicada del motor C#/.NET/WinUI. `main` contiene además las correcciones posteriores de recuperación de I/O, verificación configurable y aislamiento de ramas lentas del FAN-OUT. La versión histórica v1.4.3 permanece publicada sin modificaciones.

## Plataforma

- C# y .NET 10
- WinUI 3 + XAML
- Windows App SDK 2.4
- Windows 10 2004 (19041) o posterior
- x64

## Arquitectura

`RepartoCopier.WinUI` contiene exclusivamente la interfaz Windows. Usa controles WinUI, recursos de tema/acento, escalado DPI del sistema, focus/teclado y pickers nativos.

`RepartoCopier.Core` contiene planificación, preflight, FAN-OUT, recuperación transaccional, telemetría, BLAKE3 para SkipSame/recovery y verificación post-copia mediante CRC32C/Castagnoli por bloques. Las llamadas Win32 se mantienen aisladas en las rutas que requieren semántica de almacenamiento no expuesta directamente por las APIs de alto nivel.

## Invariantes de copia

- FAN-OUT es el único modo de copia.
- Una carpeta seleccionada se replica incluyendo su carpeta raíz.
- Se conserva exactamente la estructura de directorios, incluidas carpetas vacías.
- Un archivo seleccionado copia únicamente ese archivo.
- Los archivos adicionales del destino no se eliminan.
- El estado interno vive fuera del árbol copiado en `.disk-duplicator-state` por compatibilidad con recovery existente.
- La verificación CRC32C posterior a la copia es configurable desde la UI y está activada por defecto.
- Recovery y SkipSame conservan sus pruebas BLAKE3 independientes.
- Symlinks, junctions, reparse points y solapamientos peligrosos se rechazan de forma fail-closed.

## Rendimiento

El hot path FAN-OUT sigue un modelo shared-buffer deliberadamente simple: una lectura física del origen entra en un pool acotado de bloques alineados y el mismo bloque, con conteo de referencias, se entrega a una cola ligera por destino. Cada writer libera su referencia al completar la escritura; cuando el pool se llena, el lector espera espacio. No existen replay/spool, staging por rama ni copias privadas del payload en el camino normal.

La verificación opcional reutiliza los CRC32C calculados mientras el origen ya estaba en memoria. Después de copiar, todos los destinos avanzan coordinadamente bloque a bloque: una lectura outstanding por destino, CRC32C inmediato, comparación y reutilización del buffer. El origen no se vuelve a leer durante Verify.

La telemetría mantiene lectura física del origen, escrituras, recuperación de I/O, Direct I/O y tiempos de copy/verify para que el cuello de botella sea medible.

## Compilar

```powershell
dotnet restore RepartoCopier.sln
dotnet test dotnet/RepartoCopier.Core.Tests/RepartoCopier.Core.Tests.csproj -c Release
dotnet build dotnet/RepartoCopier.WinUI/RepartoCopier.WinUI.csproj -c Release -r win-x64
```

El protocolo de validación y benchmark físico se mantiene en `TESTING.md`.
