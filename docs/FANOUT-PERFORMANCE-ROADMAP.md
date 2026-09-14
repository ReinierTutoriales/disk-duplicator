# FAN-OUT Performance Audit & Roadmap

## Objetivo

Igualar o superar el comportamiento de ExtremeCopy en FAN-OUT sobre destinos físicos independientes sin debilitar integridad, recovery, cancelación, memoria acotada ni seguridad de topología. El rendimiento solo se considera demostrado mediante A/B reproducible en hardware Windows físico.

## Arquitectura vigente en `main`

- FAN-OUT único: una lectura física del origen alimenta a todos los destinos activos mediante un mismo `SharedBlock` con conteo de referencias.
- Bloque grande: **32 MiB**.
- El source pipeline no tiene caps fijos de 8 bloques de prefetch ni 4 bloques de hash. Los canales source/hash son no acotados estructuralmente, pero cada bloque queda gobernado por `PipelineGovernor` + `AdaptiveByteBudget`.
- `PipelineGovernor` parte de una ventana pequeña y puede crecer multiplicativamente **4 → 8 → 16 → 32 → 64...** mientras memoria y telemetría lo permitan; reduce cuando aparece presión.
- `AdaptiveByteBudget` no tiene máximo fijo de 4 GiB. Su capacidad segura se recalcula desde memoria física disponible, manteniendo reserva de seguridad y un piso de un bloque para garantizar progreso.
- BLAKE3 del origen/SkipSame/recovery permanece separado de la verificación post-copia.
- Lectura principal Direct I/O no está limitada a SSD: un HDD local con identidad física exacta y alineación conocida también puede usar `NO_BUFFERING | SEQUENTIAL_SCAN | OVERLAPPED`.
- Los buffers Direct I/O grandes del origen se sobre-alinean a **64 KiB** para que el mismo payload FAN-OUT pueda ser consumido directamente por destinos con sectores/alineaciones distintas dentro del rango soportado.
- El lector Direct I/O reconoce EOF exacto aunque el tamaño lógico final no sea múltiplo del sector.
- Verificación post-copia: CRC32 por bloque generado una sola vez durante FAN-OUT; read-back Direct I/O overlapped cuando es elegible y fallback buffered async cuando no lo es.
- Verificación no tiene clamp QD2. Todos los destinos comparten un `VerificationReadBudget` por bytes derivado de memoria disponible y la lectura alimenta el mismo scheduler adaptativo físico.
- `FastCrc32.Compute` usa slicing-by-8 como única implementación de producción.
- Escritura: **Direct I/O selectivo por archivo** con `NO_BUFFERING | SEQUENTIAL_SCAN | OVERLAPPED` cuando topología/alineación lo permiten; buffered es fallback documentado.
- Direct Write reutiliza el mismo `SharedBlock` alineado para N destinos. No existe staging completo de 32 MiB por destino. Solo un tail final menor que un sector usa scratch alineado pequeño y después se fija EOF exacto.
- Direct Write tiene fallback sticky por archivo: tras una incompatibilidad admitida no oscila de vuelta a Direct I/O durante reintentos.
- Escritura Direct o buffered usa `DestinationWriteCoordinator`; cada subescritura adquiere un único lease físico justo antes del I/O.
- `DeviceScheduler` ya no usa `SemaphoreSlim` con máximo fijo. Mantiene una **ventana QD mutable por dispositivo físico** con waiters propios.
- USB SSD exacto **QD4**, SATA SSD **QD8** y NVMe **QD16** son únicamente **puntos iniciales**. Bajo demanda sostenida el scheduler explora multiplicativamente **8 → 16 → 32 → 64 → 128...** sin un máximo por clase de hardware.
- La decisión adaptativa mide throughput agregado y latencia: conserva/expande profundidades competitivas y vuelve al mejor QD observado ante regresión clara.
- Telemetría por dispositivo expone QD inicial/actual/exploración/mínimo/máximo/mejor, upshifts/downshifts, última razón de decisión, mejor throughput y mejor latencia observada.
- `StorageWritePolicy` ya no recorta por `StorageIoProfile`; para dispositivos locales con identidad física exacta la profundidad práctica queda limitada por payload/alineación y por la ventana adaptativa, no por QD4/8/16.
- La granularidad buffered de scheduling es **4 KiB**; Direct I/O eleva automáticamente el slice mínimo a la alineación requerida. Por tanto, el viejo límite indirecto QD32 causado por slices de 1 MiB desapareció.
- Compartir dispositivo físico entre origen/destino arranca en QD1 para no provocar thrash inmediato, pero **QD1 no es un cap permanente**: la ventana puede explorar QD2+ si la medición lo justifica.
- El backlog por rama es un **soft watermark** y no bloquea al productor mientras exista memoria FAN-OUT global disponible.
- `DataMessage` no consume `GlobalControlBacklogBudget`; el payload está gobernado por bytes + backlog/QD físico. `BeginMessage` y `EndMessage` sí usan el control budget para proteger árboles con millones de archivos pequeños.
- Estado interno fuera del árbol copiado en `.disk-duplicator-state/<state_id>`.

## Cerrado e integrado

- Topología física e identidad con política prudente cuando no puede demostrarse el disco real.
- Buffers de origen alineados y Direct I/O overlapped para SSD y HDD locales elegibles.
- Fix Direct I/O EOF para tails lógicos no alineados.
- H-04 cerrado mediante eliminación de reservas por pares: cada operación física adquiere/libera un único lease y la profundidad N surge de operaciones independientes concurrentes.
- Writer variable-depth integrado en producción.
- **P1 Direct I/O de escritura cerrado**: ruta real de `CopyEngine`, una estrategia activa por archivo, mismo `SharedBlock` para N destinos, tail-only staging, EOF exacto, fallback cerrado/sticky, telemetría y gate end-to-end. Cierre: Windows .NET CI **#173 / 34846562738** verde.
- **P1 Verify QD2 cerrado**: eliminado el clamp QD2; budget global de lectura por bytes, drenaje seguro de I/O pendiente y telemetría. Cierre: Windows .NET CI **#181 / 34848005638** verde.
- **P1 source pipeline fixed ceilings cerrado**: eliminados prefetch=8, hash=4 y máximo FAN-OUT=4 GiB; crecimiento multiplicativo gobernado por memoria/telemetría. Gates prueban >8 bloques y >4 GiB contables. Cierre: Windows .NET CI **#185 / 34850301010** verde.
- **P1 payload/control backlog desacoplado**: `DataMessage` no consume el budget fijo de control; `Begin/End` sí. Gate de arquitectura bloquea el regreso de `countsData` y de la herencia incorrecta. Cierre: Windows .NET CI **#186 / 34851256674** verde.
- **P1 QD físico adaptativo cerrado**: eliminados `SemaphoreSlim` fijo, `MaxOutstandingIo` y `RecommendedQueueDepth`; QD4/8/16 son solo arranque, Copy + Verify usan la ventana adaptativa, el gate demuestra crecimiento NVMe **QD16 → QD32** y la telemetría registra decisiones/óptimo observado. Cierre final: Windows .NET CI **#197 / 34856374422** verde con Core Release + WinUI Release x64 + source gate + publish + artifact.
- H-10: lectura Direct I/O del origen con `OVERLAPPED`/async real.
- H-11/H-12/H-13: rutas antiguas de verificación y APIs obsoletas eliminadas.
- P0 backlog por dispositivo convertido en soft watermark.
- P1 CRC32 slicing-by-8 dentro de la única API `FastCrc32.Compute`.

## Prioridad actual

### P1 — limpiar telemetría/ordenamiento de backlog ya obsoletos

`ReserveBacklogAsync` ya no espera y `QueueWaitTime` puede estar midiendo solo overhead de admisión. Auditar/eliminar telemetría engañosa y simplificar el two-pass `deferred` si no aporta rendimiento medible. No conservar complejidad histórica sin función productiva.

### P1 — coordinación profunda cuando origen y destino comparten dispositivo físico

El hard cap QD1 ya fue eliminado: ahora solo es el punto de partida. Falta coordinar explícitamente lectura/escritura cuando comparten el mismo medio, especialmente HDD, para evitar seek thrash y permitir que SSD/NVMe compartidos exploten concurrencia cuando el throughput físico mejore.

### P1 — medir y reducir flush/commit/recovery del hot path

La telemetría registra flush, commit y recovery. Usarla para decidir agrupación/solapamiento preservando el contrato de durabilidad; no mantener una barrera por archivo solo por tradición si puede demostrarse una estrategia equivalente y más rápida.

### P1 — benchmark físico y tuning contra ExtremeCopy

Ejecutar A/B reproducible con el mismo origen, destinos, dataset y opciones. Usar la telemetría adaptativa para observar QD elegido, throughput y latencia por dispositivo y ajustar la política donde hardware real muestre oportunidades adicionales.

## Benchmark físico contra ExtremeCopy

Usar mismo origen, destinos, dataset y opciones. Registrar tiempo de copia/verificación/total, MB/s origen y cada destino, throughput lógico agregado, CPU/RAM, fases de telemetría, backlog/QD inicial/actual/máximo/mejor, razones de adaptación y fallbacks Direct I/O.

La meta mínima es paridad reproducible. **Superar ExtremeCopy requiere evidencia física A/B**; CI y una arquitectura potencialmente superior no constituyen por sí solos una prueba de rendimiento.

## Regla permanente de unificación

Una optimización se cierra solo cuando el consumidor real usa la nueva ruta, las rutas equivalentes antiguas fueron migradas/eliminadas, no queda código huérfano, existe gate de arquitectura y suite completa + WinUI Release pasan. Cualquier regresión devuelve el ítem a PARCIAL.
