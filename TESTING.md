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
- Gobernadores adaptativos: RAM por bytes, prefetch, backpressure y trabajo CPU-bound.
- Telemetría: source read/hash, buffer wait, FAN-OUT/queue wait, write, flush, commit, recovery y verify.

## Baseline de rendimiento congelado

El hot path actual es el baseline técnico. No cambiar tamaños de bloque, profundidad de prefetch, presupuesto de RAM, prioridad de proceso, afinidad CPU, concurrencia de verificación ni políticas de flush basándose únicamente en benchmarks virtualizados o en el nombre/tipo comercial del dispositivo.

Cualquier nueva optimización de rendimiento debe cumplir simultáneamente:

1. Mantener todos los invariantes de integridad y recovery.
2. Pasar el gate obligatorio y las pruebas de estrés existentes.
3. Mostrar una mejora repetible en hardware Windows físico o eliminar un cuello demostrado por telemetría.
4. No degradar de forma material otro escenario representativo.
5. Mantener límites duros de memoria/backpressure y cancelación segura.

La telemetría permanente se obtiene mediante `CopyJob.DiagnosticsSnapshot()` y debe ser la fuente principal para localizar el cuello antes de modificar el motor.

## Protocolo de benchmark físico

Ejecutar en Windows real. Para cada combinación origen/destino disponible (USB 3.x, SATA HDD/SSD, NVMe y almacenamiento interno), probar al menos estos perfiles de datos:

- **Secuencial grande:** uno o varios archivos suficientemente grandes para superar ampliamente caché/prefetch y observar throughput sostenido.
- **Mixto:** archivos pequeños, medianos y grandes con subdirectorios y carpetas vacías.
- **Small-file:** miles de archivos pequeños para ejercer metadata, commit y recovery.
- **Verificación:** repetir con verificación física habilitada.
- **Contención:** cuando sea posible, destinos que compartan y que no compartan controlador/hub para distinguir límite del motor de límite del bus.

Para cada ejecución conservar:

- tiempo total y bytes copiados;
- throughput observado por destino y global;
- `CopyDiagnosticsSnapshot` completo;
- tipo de conexión física y si comparte controlador/hub;
- CPU y RAM del equipo;
- resultado final de verificación.

Antes de comparar dos cambios, usar el mismo dataset, origen, destinos, opciones y topología física. Repetir las mediciones para separar variación del sistema de una mejora real.

## Interpretación de cuellos

- `BufferWaitTime` alto con almacenamiento todavía ocioso: revisar presupuesto de RAM/prefetch.
- `FanoutWaitTime` o `QueueWaitTime` altos: uno o más consumidores o el bus están imponiendo backpressure; no aumentar RAM automáticamente.
- `SourceHashTime` dominante respecto a lectura: investigar CPU/BLAKE3 antes de tocar I/O.
- `WriteTime` dominante: límite de destino/controlador/filesystem; confirmar con throughput físico.
- `DurableFlushTime`, `CommitTime` o `RecoveryTime` dominantes en small-file: optimizar metadata/durabilidad solo si se preserva el contrato transaccional.
- `VerifyHashTime` dominante: revisar concurrencia CPU; `VerifyReadTime` dominante: el límite es lectura física de destinos.

## Pruebas de fallo físico antes de release

Además del benchmark, validar desconexión de un destino, cancelación durante lectura/escritura/commit, pausa/reanudación prolongada, falta de espacio, paths Unicode/UNC y recuperación después de una interrupción. Ninguna mejora de rendimiento puede reducir estas garantías.
