# FAN-OUT Performance Audit & Roadmap

## Objetivo

Igualar o superar el comportamiento de ExtremeCopy en FAN-OUT sobre destinos físicos independientes sin debilitar integridad, recovery, cancelación, memoria acotada ni seguridad de topología. El rendimiento solo se considera demostrado mediante A/B reproducible en hardware Windows físico.

## Arquitectura vigente en `main`

- FAN-OUT único: una lectura física del origen alimenta a todos los destinos activos mediante un mismo `SharedBlock` con conteo de referencias.
- Bloque grande: **32 MiB**.
- El source pipeline ya no tiene caps fijos de 8 bloques de prefetch ni 4 bloques de hash. Los canales source/hash son no acotados estructuralmente, pero cada bloque queda gobernado por `PipelineGovernor` + `AdaptiveByteBudget`.
- `PipelineGovernor` parte de una ventana pequeña y puede crecer multiplicativamente **4 → 8 → 16 → 32 → 64...** mientras la memoria y la telemetría lo permitan; reduce agresivamente cuando aparece presión.
- `AdaptiveByteBudget` ya no tiene máximo fijo de 4 GiB. Su capacidad segura se recalcula desde memoria física disponible, manteniendo reserva de seguridad y un piso de un bloque para garantizar progreso.
- BLAKE3 del origen/SkipSame/recovery permanece separado de la verificación post-copia.
- Lectura principal Direct I/O ya no está limitada a SSD: un HDD local con identidad física exacta y alineación conocida también puede usar `NO_BUFFERING | SEQUENTIAL_SCAN | OVERLAPPED`.
- Los buffers Direct I/O grandes del origen se sobre-alinean a **64 KiB** para que el mismo payload FAN-OUT pueda ser consumido directamente por destinos con sectores/alineaciones distintas dentro del rango soportado.
- El lector Direct I/O reconoce EOF exacto aunque el tamaño lógico final no sea múltiplo del sector.
- Verificación post-copia: CRC32 por bloque generado una sola vez durante FAN-OUT; read-back Direct I/O overlapped cuando es elegible y fallback buffered async cuando no lo es.
- Verificación ya no tiene clamp QD2. Todos los destinos comparten un `VerificationReadBudget` por bytes derivado de memoria disponible, y la profundidad puede crecer hasta la capacidad física útil del scheduler.
- `FastCrc32.Compute` usa slicing-by-8 como única implementación de producción.
- Escritura: **Direct I/O selectivo por archivo** con `NO_BUFFERING | SEQUENTIAL_SCAN | OVERLAPPED` cuando topología/alineación lo permiten; buffered es únicamente fallback documentado.
- Direct Write reutiliza el mismo `SharedBlock` alineado para N destinos. No existe staging completo de 32 MiB por destino. Solo un tail final menor que un sector se copia a un scratch alineado pequeño y después se fija EOF exacto.
- Direct Write tiene fallback sticky por archivo: tras una incompatibilidad admitida no oscila de vuelta a Direct I/O durante reintentos.
- Telemetría productiva expone archivos Direct Write, bytes, operaciones y fallbacks.
- Escritura Direct o buffered usa **profundidad variable real** mediante `DestinationWriteCoordinator`; cada subescritura adquiere un único slot físico justo antes del I/O.
- Capacidades actuales del scheduler: USB SSD exacto **QD4**, SATA SSD **QD8**, NVMe **QD16**. En la implementación actual todavía son hard caps físicos; deben convertirse en una ventana dinámica y no permanecer como límites finales.
- Ya no existen `WriteQueueDepthTwoAsync`, `WriteTwoAsync`, `AcquireIoPairAsync`, `IoPairLease` ni `BufferedLargeWriteQueueDepth`.
- El backlog por rama es un **soft watermark** y no bloquea al productor mientras exista memoria FAN-OUT global disponible.
- `DataMessage` ya no consume `GlobalControlBacklogBudget`; el payload está gobernado por bytes + backlog/QD físico. `BeginMessage` y `EndMessage` siguen usando el control budget para proteger árboles con millones de archivos pequeños.
- Estado interno fuera del árbol copiado en `.disk-duplicator-state/<state_id>`.

## Cerrado e integrado

- Topología física e identidad con política conservadora cuando no puede demostrarse el disco real.
- Buffers de origen alineados y Direct I/O overlapped para SSD y HDD locales elegibles.
- Fix Direct I/O EOF para tails lógicos no alineados.
- H-04 cerrado mediante eliminación de reservas por pares: cada operación física adquiere/libera un único slot y la profundidad N surge de operaciones independientes concurrentes.
- Writer variable-depth integrado en producción.
- **P1 Direct I/O de escritura cerrado**: ruta real de `CopyEngine`, una estrategia activa por archivo, mismo `SharedBlock` para N destinos, tail-only staging, EOF exacto, fallback cerrado/sticky, telemetría y gate end-to-end. Cierre: Windows .NET CI **#173 / 34846562738** verde.
- **P1 Verify QD2 cerrado**: eliminado el clamp QD2; budget global de lectura por bytes, drenaje seguro de I/O pendiente y telemetría. Cierre: Windows .NET CI **#181 / 34848005638** verde.
- **P1 source pipeline fixed ceilings cerrado**: eliminados prefetch=8, hash=4 y máximo FAN-OUT=4 GiB; crecimiento multiplicativo gobernado por memoria/telemetría. Gates prueban >8 bloques y >4 GiB contables. Cierre: Windows .NET CI **#185 / 34850301010** verde.
- **P1 payload/control backlog desacoplado**: `DataMessage` no consume el budget fijo de control; `Begin/End` sí. Gate de arquitectura bloquea el regreso de `countsData` y de la herencia incorrecta. Cierre: Windows .NET CI **#186 / 34851256674** verde.
- H-10: lectura Direct I/O del origen con `OVERLAPPED`/async real.
- H-11/H-12/H-13: rutas antiguas de verificación y APIs obsoletas eliminadas.
- P0 backlog por dispositivo convertido en soft watermark.
- P1 CRC32 slicing-by-8 dentro de la única API `FastCrc32.Compute`.

## Prioridad actual

### P1 — QD físico adaptativo sin hard caps 4/8/16

`DeviceScheduler` todavía usa un `SemaphoreSlim` con máximo inmutable y `StorageIoProfile.RecommendedQueueDepth` funciona de hecho como hard cap. Migrar a una ventana de admisión mutable por dispositivo: los valores por clase de hardware serán puntos iniciales/probe, no máximos. Medir throughput, latencia, ocupación y presión para subir/bajar dinámicamente. NVMe debe poder explorar QD32/QD64 o superior cuando hardware/dataset muestran ganancia; SATA/USB también deben subir si la evidencia lo justifica.

Requisitos:
- una operación física = un lease, sin reservas parciales por pares/batches;
- ventana QD mutable con waiters propios, no `SemaphoreSlim` de máximo fijo;
- telemetría current/min/max QD, upshifts/downshifts y razón de decisión;
- backoff rápido ante degradación y crecimiento agresivo ante starvation/ganancia;
- gate que pruebe crecimiento por encima de los valores iniciales sin reintroducir QD2 fijo.

### P1 — mismo dispositivo físico origen/destino

Hoy compartir dispositivo físico fuerza el scheduler a QD1. Sustituir esa regla fija por coordinación source/write basada en medio y medición: HDD debe evitar seek thrash; SSD/NVMe compartido puede admitir concurrencia si el throughput físico mejora.

### P1 — limpiar telemetría/ordenamiento de backlog ya obsoletos

`ReserveBacklogAsync` ya no espera y `QueueWaitTime` puede medir solo overhead de admisión. Auditar/eliminar telemetría engañosa y simplificar el two-pass `deferred` si no aporta rendimiento medible.

### P1 — medir y reducir flush/commit/recovery del hot path

La telemetría ya registra flush, commit y recovery. Usarla para decidir agrupación/solapamiento preservando el contrato de durabilidad; no mantener una barrera por archivo solo por tradición si puede demostrarse una estrategia equivalente y más rápida.

## Benchmark físico contra ExtremeCopy

Usar mismo origen, destinos, dataset y opciones. Registrar tiempo de copia/verificación/total, MB/s origen y cada destino, throughput lógico agregado, CPU/RAM, fases de telemetría, backlog/QD, profundidad realmente observada y fallbacks Direct I/O.

La meta mínima es paridad reproducible. **Superar ExtremeCopy requiere evidencia física A/B**; CI y una arquitectura potencialmente superior no constituyen por sí solos una prueba de rendimiento.

## Regla permanente de unificación

Una optimización se cierra solo cuando el consumidor real usa la nueva ruta, las rutas equivalentes antiguas fueron migradas/eliminadas, no queda código huérfano, existe gate de arquitectura y suite completa + WinUI Release pasan. Cualquier regresión devuelve el ítem a PARCIAL.
