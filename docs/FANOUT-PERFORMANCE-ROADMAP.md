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
- `DestinationWriteCoordinator` emite cada payload FAN-OUT lógico como una sola escritura de offset explícito; la concurrencia física ocurre entre bloques independientes y ramas de destino, no fragmentando un bloque secuencial para fabricar QD.
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
- `FastCrc32C.Compute` usa CRC32C hardware mediante SSE4.2 en x86/x64 o instrucciones CRC de ARM cuando están disponibles; el fallback usa slicing-by-8 Castagnoli y debe ser bit-idéntico al hardware.
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
- StorageWritePolicy, FanoutPerformancePolicy y ExplicitOffsetWriter eliminados tras quedar huérfanos; `StorageIoProfile` clasifica el punto inicial y `DeviceScheduler` gobierna la ruta productiva adaptativa, mientras `DestinationWriteCoordinator` emite la escritura real.
- Infraestructura temporal de migraciones eliminada de `main`; solo queda el workflow permanente `windows-dotnet.yml`.

## Prioridad actual

### VALIDACIÓN FÍSICA — ramp-up de QD

`DeviceScheduler.RecordCompletionLocked` ya reevalúa usando una onda de concurrencia realmente observada (`_samplePeakObservedConcurrency`) y demanda efectiva, sin un piso fijo arbitrario de completions. El siguiente paso no es volver a cambiar la política por intuición: medir tiempo-hasta-QD-útil en hardware físico y modificarla solo si el benchmark demuestra una exploración insuficiente.

### VALIDACIÓN FÍSICA — slow-branch decoupling

Integrado replay por rama: cuando una rama supera su `BacklogTargetBytes` y existen múltiples destinos, el payload se deriva a un `BranchReplayStore` temporal append-only, se libera inmediatamente la referencia de esa rama al `SharedBlock` y el writer la reproduce después conservando CRC32C, offsets y una sola lectura física del source. El replay es best-effort: si el volumen temporal no puede aceptarlo se conserva la ruta shared normal. Telemetría expone bytes/tiempo/segmentos de replay. Pendiente únicamente medir en hardware real el punto de activación y el coste del volumen temporal.

### VALIDACIÓN FÍSICA — tamaño de transferencia / ventana de lectura

El `BlockSize` fijo y las bandas de `ReadBufferSizeFor` fueron eliminados. `AdaptiveTransferSizer` calcula el tamaño por archivo usando headroom actual de `AdaptiveByteBudget`, destinos activos, QD físico, límite de prefetch y, cuando ya existe feedback, throughput + latencia observados para estimar bytes por operación. `PipelineGovernor` actualiza su bytes-per-block con cada selección y la telemetría expone tamaño actual/mínimo/máximo. Pendiente únicamente validar en hardware real cómo converge frente a ExtremeCopy.


### VALIDACIÓN FÍSICA — alineación Direct I/O

El techo artificial de 64 KiB fue eliminado de source, destination y verify. La elegibilidad Direct I/O ahora acepta cualquier alineación de sector conocida >=512 que sea potencia de dos y compatible con el tamaño de transferencia. Los buffers FAN-OUT reciben la alineación máxima real de los dispositivos participantes; replay usa la alineación del destino y verification abre Direct con la alineación requerida por ese dispositivo. Existe contrato sintético de 128 KiB. Pendiente únicamente validación con hardware real que reporte alineaciones superiores a 64 KiB.

### VALIDACIÓN FÍSICA — CPU por byte / verificación

CRC32C interno ya dispone de ruta hardware y fallback software Castagnoli bit-idéntico. La telemetría usa exclusivamente `VerifyCrc32CBytes`, `VerifyCrc32CTime` y `VerifyCrc32CBytesPerSecond`; la nomenclatura histórica `VerifyHash*` fue eliminada para evitar dos contratos para la misma medición. `VerificationBottleneck` clasifica la tasa de servicio como `StorageRead`, `Crc32C`, `Balanced` (banda del 15%) o `None` sin muestras suficientes. El mensaje de mismatch productivo también identifica CRC32C. Pendiente únicamente validar en hardware real GiB/s, CPU y la clasificación frente al tiempo de pared.

## Benchmark físico contra ExtremeCopy

Usar el mismo origen, destinos, dataset y opciones. Registrar:

- throughput físico del source;
- throughput por destino;
- throughput agregado lógico;
- QD inicial/actual/máximo/mejor y tiempo hasta el QD útil;
- source idle / destination idle;
- CPU y RAM;
- fan-out wait, buffer wait, verify read GiB/s, CRC32C GiB/s y `VerificationBottleneck`;
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

### Cierre de auditoría — retry y replay por rama

- Buffered retry consulta `TransientIoErrorClassifier.IsTransient` desde `WriteBlockAtOffsetAsync`; errores permanentes y fallos de medio (incluidos Win32 23/1117) no consumen reintentos.
- Replay a disco solo se habilita cuando el volumen temporal tiene identidad física `Exact` y se demuestra distinto del origen y de todos los destinos activos. Sin esa prueba, replay queda deshabilitado de forma conservadora.
- La entrada/salida de replay usa histéresis temporal por rama: backlog alto sostenido para entrar y backlog por debajo del 50% sostenido para salir. Un pico aislado no activa spool.
- Contratos de arquitectura verifican el consumidor productivo del clasificador, la colocación física y la histéresis; `IOException` de spill conserva el fallback correcto al `SharedBlock` en memoria.