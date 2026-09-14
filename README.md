# RepartoCopier

RepartoCopier es una aplicación de escritorio para Windows que copia un origen hacia múltiples destinos simultáneamente mediante FAN-OUT, preservando la estructura completa de carpetas y priorizando integridad, recuperación y comportamiento nativo del sistema operativo.

## Versión actual

**RepartoCopier v2.0.0** es el baseline C#/.NET/WinUI del proyecto. `main` contiene además la evolución de rendimiento posterior a v2.0.0. La versión histórica v1.4.3 permanece publicada sin modificaciones.

## Plataforma

- C# y .NET 10
- WinUI 3 + XAML
- Windows App SDK 2.4
- Windows 10 2004 (19041) o posterior
- x64

## Arquitectura

`RepartoCopier.WinUI` contiene exclusivamente la interfaz Windows. Usa controles WinUI, recursos de tema/acento, escalado DPI del sistema, focus/teclado y pickers nativos.

`RepartoCopier.Core` contiene planificación, preflight, FAN-OUT, recuperación transaccional, telemetría, BLAKE3 para SkipSame/recovery y verificación post-copia automática mediante CRC32 por bloques. Las llamadas Win32 se mantienen aisladas en las rutas que requieren semántica de almacenamiento no expuesta directamente por las APIs de alto nivel.

## Invariantes de copia

- FAN-OUT es el único modo de copia.
- Una carpeta seleccionada se replica incluyendo su carpeta raíz.
- Se conserva exactamente la estructura de directorios, incluidas carpetas vacías.
- Un archivo seleccionado copia únicamente ese archivo.
- Los archivos adicionales del destino no se eliminan.
- El estado interno vive fuera del árbol copiado en `.disk-duplicator-state`.
- Los destinos recién escritos se verifican automáticamente después de la copia.
- Recovery y SkipSame conservan sus pruebas BLAKE3 independientes.
- Symlinks, junctions, reparse points y solapamientos peligrosos se rechazan de forma fail-closed.

## Rendimiento

El hot path actual usa bloques compartidos de 32 MiB, prefetch acotado, presupuesto global de RAM, backpressure y scheduler por dispositivo físico. En orígenes SSD locales con identidad exacta y alineación conocida existe una ruta Direct I/O `NO_BUFFERING + SEQUENTIAL_SCAN + OVERLAPPED`; la verificación usa el mismo principio cuando es elegible. Las escrituras de destino siguen siendo buffered con offsets explícitos y QD2 selectivo para SSD calificados; Direct I/O de escritura todavía no está implementado.

La telemetría de `CopyJob.DiagnosticsSnapshot()` permite identificar el cuello real antes de modificar parámetros. La hoja de ruta vigente está en `docs/FANOUT-PERFORMANCE-ROADMAP.md`.

## Compilar

```powershell
dotnet restore RepartoCopier.sln
dotnet test dotnet/RepartoCopier.Core.Tests/RepartoCopier.Core.Tests.csproj -c Release
dotnet build dotnet/RepartoCopier.WinUI/RepartoCopier.WinUI.csproj -c Release -r win-x64
```

El protocolo de validación y benchmark físico se mantiene en `TESTING.md`.
