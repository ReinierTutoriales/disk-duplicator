# FAN-OUT Performance Audit & Roadmap

## Objetivo

Igualar o superar ExtremeCopy en FAN-OUT sobre hardware Windows real. El criterio rector es simple: una lectura física del origen debe alimentar a todos los destinos capaces sin dividir artificialmente el throughput del source entre N ramas. Si el source sostiene ~150 MB/s y varios destinos pueden sostener al menos esa tasa, cada destino debe intentar recibir ~150 MB/s. El rendimiento solo se considera demostrado mediante A/B reproducible en hardware físico.

## Principios obligatorios

- Una optimización no se cierra porque el código nuevo exista: debe sustituir la ruta productiva anterior, migrar consumidores, eliminar código/rutas huérfanas, añadir contrato de arquitectura y pasar Core + WinUI Release.
- Ningún cap estático destinado solo a "ser prudente". La regulación de rendimiento debe basarse en hardware, throughput, latencia o presión real de recursos.
- Las invariantes que evitan OOM, corrupción, uso incorrecto de topología o commit no durable se conservan, pero deben desaparecer prácticamente del fast path cuando el riesgo no existe.
- Después de cada migración se elimina la infraestructura temporal. `main` debe conservar una sola línea productiva limpia.

## Arquitectura vigente en `main`

- FAN-OUT único: un bloque leído del origen se comparte mediante `SharedBlock` entre todos los destinos activos.
- Source pipeline con canales estructuralmente no acotados, pero payload gobernado por `PipelineGovernor` + `AdaptiveByteBudget`.
- `AdaptiveByteBudget` obtiene capacidad desde `GC.GetGCMemoryInfo()` y `MemoryPressureCapacity`; no usa el antiguo máximo fijo de 4 GiB ni un porcentaje fijo de RAM instalada.
- `PipelineGovernor` puede ampliar/reducir multiplicativamente la ventana de prefetch según starvation, presión de entrega y presión de memoria.
- Lectura del origen Direct I/O overlapped cuando el volumen local tiene probe/alineación válidos; HDD no está excluido por clase de medio.
- El payload FAN-OUT se renta alineado para que destinos Direct I/O puedan reutilizar el mismo bloque sin staging completo por rama.
- Escritura Direct I/O con `NO_BUFFERING | SEQUENTIAL_SCAN | OVERLAPPED` cuando es elegible; buffered permanece como fallback de compatibilidad.
- `DestinationWriteCoordinator` divide cada payload en operaciones de offset explícito y cada sub-I/O adquiere un lease del `DeviceScheduler` físico.
- **Multi-block in-flight por destino**: el writer ya no espera a completar un `DataMessage` antes de programar el siguiente. Cada bloque reserva un offset monotónico (`ScheduledBytes`) y se mantiene como tarea pendiente independiente.
- `EndMessage` es barrera de archivo: no se hace flush/finalización/commit hasta drenar todas las escrituras pendientes.
- `Copied` representa únicamente bytes realmente completados y solo avanza mediante `RecordCompletedWrite`; `FinishFile` exige simultáneamente `ScheduledBytes == Entry.Size` y `Copied == Entry.Size`.
- Direct I/O fallback durante multi-block no cierra un handle mientras haya operaciones direct pendientes: se solicita fallback, se drena la generación activa, se cambia una sola vez a buffered y se reintentan los bloques afectados por offset explícito.
- La cantidad de bloques pendientes no usa `MaxBlocksInFlight` fijo: queda limitada por la memoria FAN-OUT adaptativa y el scheduler físico.
- `DeviceScheduler` usa QD mutable por dispositivo, waiters propios y exploración multiplicativa sin máximo por clase de hardware.
- USB/SATA/NVMe conservan únicamente profundidades iniciales; no son techos permanentes.
- Source y destino con el mismo `PhysicalDeviceNumber` comparten scheduler. Si comparten disco, el arranque QD1 es solo punto inicial, no cap permanente.
- Si `IOCTL_STORAGE_GET_DEVICE_NUMBER` falla, `StorageTopology` intenta `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`; un extent único recupera la identidad física real sin degradar QD ni apagar Direct I/O.
- Volúmenes multi-disk no se fingen como una sola identidad física.
- `DataMessage` mantiene un fast path de enqueue por rama; `ReserveBacklog` es contabilidad/telemetría, no admission gate.
- El control plane (`BeginMessage`/`EndMessage`) usa `AdaptiveControlByteBudget`, derivado de presión real de memoria. El antiguo `GlobalControlBacklogBudget` y su cap fijo permanecen eliminados.
- `WriteThrough` fue eliminado del hot path. La durabilidad se conserva mediante flush explícito antes de `AtomicFileCommit`.
- `AtomicFileCommit` es la única primitiva de reemplazo productiva.
- Verificación post-copia usa CRC32C/Castagnoli por bloque y lectura Direct/buffered async con `VerificationReadBudget` dinámico.
- `FastCrc32.Compute` usa CRC32C hardware mediante SSE4.2 en x86/x64 o instrucciones CRC de ARM cuando están disponibles; el fallback usa slicing-by-8 Castagnoli y debe ser bit-idéntico al hardware.
- `PendingRead` en verificación es un `readonly record struct`, evitando una asignación de objeto por descriptor de lectura pendiente sin cambiar QD ni presupuesto de memoria.
- Los CRC32C de `VerificationBlock` son internos a la ejecución de copy/verify; recovery continúa usando el hash BLAKE3 persistente y no depende del checksum de bloque.
- Estado interno permanece fuera del árbol copiado en `.disk-duplicator-state/<state_id>`.

## Cerrado e integrado

- Direct I/O source overlapped, incluidos HDD locales elegibles y EOF lógico no alineado.
- Direct I/O destination con mismo `SharedBlock`, tail-only scratch, EOF exacto y fallback de compatibilidad.
- QD2 fijo eliminado de copy y verify.
- Caps fijos de source prefetch/hash y máximo FAN-OUT de 4 GiB eliminados.
- Backlog duro por dispositivo eliminado; queda contabilidad de presión.
- QD físico adaptativo integrado; QD4/8/16 son solo puntos de arranque.
- FAN-OUT delivery hot path separado del control plane.
- Commit atómico unificado.
- Memoria FAN-OUT y verification budget adaptados a presión real de memoria.
- Thresholds fijos de Direct destination, source prefetch, direct verify y preallocation eliminados.
- `WriteThrough` y su telemetría/política antigua eliminados.
- Identidad física ampliada mediante `VOLUME_DISK_EXTENTS` cuando el probe primario falla.
- Control-plane fijo eliminado y reemplazado por `AdaptiveControlByteBudget`.
- **Multi-block in-flight integrado**: eliminada la ruta secuencial `WriteWithRetryAsync`; los bloques se programan por offset explícito, el commit espera al drenaje total y el fallback Direct se resuelve solo después de drenar I/O pendiente.
- CRC32 IEEE interno de verificación sustituido por CRC32C/Castagnoli coherente en hardware y software, con vector estándar y contrato hardware/software.
- Descriptor `PendingRead` de verificación convertido a valor readonly para eliminar la asignación de heap por entrada pendiente.
- Infraestructura temporal de migraciones eliminada de `main`; solo queda el workflow permanente `windows-dotnet.yml`.

## Prioridad actual

### P1 — ramp-up de QD más rápido y basado en evidencia

`DeviceScheduler.RecordCompletionLocked` todavía espera una cantidad de completions dependiente del QD antes de reevaluar. No existe hard max, pero una copia corta puede terminar antes de explorar suficiente profundidad. Auditar tiempo-hasta-QD-óptimo y sustituir cualquier lentitud innecesaria por exploración basada en demanda, throughput, latencia y tiempo observado; no por otro número fijo arbitrario.

### P1 — slow-branch decoupling

Una rama permanentemente más lenta conserva referencias a `SharedBlock` durante más tiempo. Mientras exista headroom de RAM esto no afecta a las ramas rápidas; bajo presión sostenida puede terminar frenando al productor. Diseñar desacoplamiento por rama que preserve una sola lectura física del source: ventana dinámica por destino, batching/deferred write y, si el benchmark lo justifica, spill/replay para la rama atrasada. No resolverlo limitando todas las ramas a la velocidad del destino lento.

### P1 — BlockSize / ventana de lectura adaptativos

`BlockSize` continúa fijo en 32 MiB y `ReadBufferSizeFor` usa bandas 64 KiB / 1 MiB / 4 MiB / 32 MiB. Son heurísticas pendientes de demostrar. La siguiente evolución debe explorar tamaño de bloque/ventana según throughput, latencia, QD, número de destinos y presión de memoria. No sustituir 32 MiB por otro número fijo.

### P1 — eliminar QD1 incondicional de network

`StorageWritePolicy` todavía fuerza network a QD1. Direct I/O remoto puede seguir deshabilitado por compatibilidad, pero el buffered explicit-offset path no debe asumir que NAS/SMB solo soporta una operación concurrente. Convertirlo en exploración adaptativa.

### P2 — alineación máxima Direct I/O

El soporte de payload alineado mantiene `MaximumSupportedAlignment = 64 KiB`. Auditar si puede derivarse completamente del dispositivo sin máximo de implementación fijo.

### P2 — CPU por byte / verificación

CRC32C interno ya dispone de ruta hardware y fallback software Castagnoli bit-idéntico. El siguiente paso no es cambiar otra vez de algoritmo: medir GiB/s de lectura, tiempo de checksum y CPU en hardware real para comprobar cuánto aporta la aceleración y si la verificación está limitada por I/O o CPU.

## Benchmark físico contra ExtremeCopy

Usar el mismo origen, destinos, dataset y opciones. Registrar:

- throughput físico del source;
- throughput por destino;
- throughput agregado lógico;
- QD inicial/actual/máximo/mejor y tiempo hasta el QD útil;
- source idle / destination idle;
- CPU y RAM;
- fan-out wait, buffer wait y hash time;
- Direct I/O fallbacks;
- efecto de una rama lenta sobre las rápidas.

Escenario mínimo objetivo:

```text
SOURCE HDD:      ~150 MB/s
DEST A capaz:    ~150 MB/s
DEST B capaz:    ~150 MB/s
DEST C capaz:    ~150 MB/s
DEST D capaz:    ~150 MB/s
Aggregate write: ~600 MB/s
Physical source: ~150 MB/s
```

La meta mínima es paridad reproducible. Superar ExtremeCopy requiere evidencia física A/B; CI y arquitectura superior no son por sí solos prueba de rendimiento.

## Regla permanente de unificación

Una optimización se cierra únicamente cuando:

```text
[ ] La ruta productiva nueva reemplaza realmente a la anterior
[ ] Grep completo de símbolos/rutas antiguas en dotnet/
[ ] Consumidores reales del símbolo nuevo verificados
[ ] Código/telemetría/parámetros huérfanos eliminados
[ ] Infraestructura temporal de migración eliminada
[ ] Test de contrato de arquitectura añadido
[ ] Suite Core Release verde
[ ] WinUI Release x64 verde
[ ] Source gate + publish + artifact verdes
[ ] El SHA final de main contiene código + tests + documentación limpia
```

Si alguno falla, el ítem permanece PARCIAL.
