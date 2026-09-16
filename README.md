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

El hot path usa FAN-OUT con una sola lectura del origen y `SharedBlock` para las ramas que mantienen el ritmo. Cada destino dispone de staging propio: cuando una rama excede su ventana de retención derivada del QD físico, se desacopla del `SharedBlock`; si existe un dispositivo temporal físicamente independiente demostrado, el spill de replay ocurre dentro de la etapa de esa rama, nunca inline en el productor. Si replay no está disponible, la rama recibe un buffer propio. Así, una cola individual no actúa como backpressure directo del productor; la admisión global queda gobernada por memoria, cancelación y fallos reales.

La cantidad de escrituras pendientes por destino también está acotada por una ventana adaptativa ligada al `DeviceScheduler`, y el backlog físico se mantiene contabilizado hasta que el payload de esa rama termina realmente. El tamaño de transferencia del origen ya no se reduce por el QD máximo de una única rama lenta. Source, destinos y verificación usan Direct I/O `NO_BUFFERING + SEQUENTIAL_SCAN + OVERLAPPED` cuando la topología/alineación lo permiten, con fallback buffered seguro.

La telemetría de `CopyJob.DiagnosticsSnapshot()` expone lectura del origen, presión del pipeline, schedulers físicos, recuperación de I/O y flujo por rama (`BranchFlows`) para localizar el cuello real antes de modificar parámetros. La hoja de ruta vigente está en `docs/FANOUT-PERFORMANCE-ROADMAP.md`.

## Compilar

```powershell
dotnet restore RepartoCopier.sln
dotnet test dotnet/RepartoCopier.Core.Tests/RepartoCopier.Core.Tests.csproj -c Release
dotnet build dotnet/RepartoCopier.WinUI/RepartoCopier.WinUI.csproj -c Release -r win-x64
```

El protocolo de validación y benchmark físico se mantiene en `TESTING.md`.
