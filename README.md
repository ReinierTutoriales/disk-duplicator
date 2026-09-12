# RepartoCopier

RepartoCopier es una aplicación de escritorio para Windows que copia un origen hacia múltiples destinos simultáneamente mediante FAN-OUT, preservando la estructura completa de carpetas y priorizando integridad, recuperación y comportamiento nativo del sistema operativo.

## Plataforma

- C# y .NET 10
- WinUI 3 + XAML
- Windows App SDK 2.4
- Windows 10 2004 (19041) o posterior
- x64

## Arquitectura

`RepartoCopier.WinUI` contiene exclusivamente la interfaz Windows. Usa controles WinUI, recursos de tema/acento, escalado DPI del sistema, focus/teclado y pickers nativos. No se implementan sustitutos visuales manuales cuando Windows ya ofrece el comportamiento.

`RepartoCopier.Core` contiene planificación, preflight, FAN-OUT, verificación BLAKE3, recuperación transaccional y protección de rutas. Las llamadas Win32 se mantienen aisladas y solo se usan cuando .NET o Windows App SDK no exponen la semántica requerida.

## Invariantes de copia

- FAN-OUT es el único modo de copia.
- Una carpeta seleccionada se replica incluyendo su carpeta raíz.
- Se conserva exactamente la estructura de directorios, incluidas carpetas vacías.
- Un archivo seleccionado copia únicamente ese archivo.
- Los archivos adicionales del destino no se eliminan.
- El estado interno vive fuera del árbol copiado en `.disk-duplicator-state`.
- Los destinos se verifican físicamente antes de confiar en recovery/skip.
- Symlinks, junctions, reparse points y solapamientos peligrosos se rechazan de forma fail-closed.

## Compilar

```powershell
dotnet restore RepartoCopier.sln
dotnet test dotnet/RepartoCopier.Core.Tests/RepartoCopier.Core.Tests.csproj -c Release
dotnet build dotnet/RepartoCopier.WinUI/RepartoCopier.WinUI.csproj -c Release -r win-x64
```

La versión estable histórica v1.4.3 permanece publicada sin modificaciones. El corte C#/WinUI corresponde a una versión futura.
