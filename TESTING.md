# Testing

El producto actual es exclusivamente C#/.NET/WinUI 3.

## Gate obligatorio

```powershell
dotnet restore dotnet/RepartoCopier.Core.Tests/RepartoCopier.Core.Tests.csproj
dotnet test dotnet/RepartoCopier.Core.Tests/RepartoCopier.Core.Tests.csproj -c Release --no-restore
dotnet restore dotnet/RepartoCopier.WinUI/RepartoCopier.WinUI.csproj -r win-x64
dotnet build dotnet/RepartoCopier.WinUI/RepartoCopier.WinUI.csproj -c Release -r win-x64 --no-restore
```

## Cobertura mínima de regresión

- Árbol de carpetas completo y carpetas vacías.
- Archivo individual sin copiar hermanos.
- FAN-OUT multibloque hacia varios destinos.
- Unicode y rutas Windows.
- Solapamientos origen/destinos y destino/destino.
- Conflictos archivo/carpeta y reparse points.
- Cálculo de espacio con reserva y granularidad de asignación.
- Recovery: journal, manifest, backup, corrupción, estado legacy y validación BLAKE3 física.
- Pausa, continuación, cancelación y fallo aislado por destino.

## Pruebas físicas antes de release

Ejecutar en Windows real con USB, SATA SSD y NVMe cuando estén disponibles; 1/2/4/8/16 destinos; archivos grandes y árboles de archivos pequeños; desconexión de destino; cancelación durante lectura/escritura/commit; paths Unicode y UNC.
