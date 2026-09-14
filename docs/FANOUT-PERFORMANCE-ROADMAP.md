# FAN-OUT Performance Audit & Roadmap

## Objetivo

Igualar o superar el comportamiento de ExtremeCopy en FAN-OUT sobre destinos físicos independientes sin debilitar integridad, recovery, cancelación, memoria acotada ni seguridad de topología. El rendimiento solo se considera demostrado mediante A/B reproducible en hardware Windows físico.

## Arquitectura vigente en `main`

- FAN-OUT único: una lectura del origen alimenta a todos los destinos activos mediante `SharedBlock` con conteo de referencias.
- Bloque grande: **32 MiB**.
- Prefetch físico máximo actual: **8** bloques; pipeline de hash: **4**. Estos valores deben evolucionar por medición, no se consideran techos permanentes.
- BLAKE3 del origen/SkipSame/recovery permanece separado de la verificación post-copia.
- Lectura principal SSD elegible: Direct I/O con buffers alineados y `NO_BUFFERING | SEQUENTIAL_SCAN | OVERLAPPED`, usando I/O async real.
- El lector Direct I/O reconoce EOF exacto aunque el tamaño lógico final no sea múltiplo del sector; no vuelve a aplicar el guard de alineación después de consumir un tail no alineado.
- Verificación post-copia automática: CRC32 por bloque generado una sola vez durante FAN-OUT; read-back Direct I/O overlapped cuando es elegible y fallback buffered async cuando no lo es.
- `FastCrc32.Compute` usa slicing-by-8 como única implementación de producción.
- Escritura actual: buffered, offsets explícitos y **profundidad variable real** mediante `DestinationWriteCoordinator`; cada subescritura adquiere un único slot físico justo antes del I/O.
- Capacidades iniciales del scheduler por clase de hardware: USB SSD exacto **QD4**, SATA SSD **QD8**, NVMe **QD16**. No son límites de producto: deben poder aumentar/disminuir según evidencia física y política adaptativa futura.
- Ya no existen `WriteQueueDepthTwoAsync`, `WriteTwoAsync`, `AcquireIoPairAsync`, `IoPairLease` ni `BufferedLargeWriteQueueDepth`.
- `StorageWritePolicy.LargeWriteQueueDepth` limita profundidad útil por tamaño real del payload y capacidad del dispositivo, no por un cap global QD2.
- **No existe todavía Direct I/O de escritura**; el handle de destino sigue siendo buffered.
- El backlog por rama es un **soft watermark** de presión y ya no puede bloquear al productor mientras exista memoria FAN-OUT global disponible.
- El límite duro de payload en vuelo es `AdaptiveByteBudget`.
- Estado interno fuera del árbol copiado en `.disk-duplicator-state/<state_id>`.

## Cerrado e integrado

- Topología física e identidad con política conservadora cuando no puede demostrarse el disco real.
- Buffers de origen alineados y Direct I/O.
- Fix Direct I/O EOF: un archivo con tail lógico no alineado termina en EOF correctamente sin `ArgumentOutOfRangeException(fileOffset)`.
- H-04 cerrado mediante eliminación de la arquitectura de reserva por pares: cada operación física adquiere/libera un único slot y la profundidad N surge de operaciones independientes concurrentes. No existe reserva parcial multi-slot.
- Writer buffered variable-depth integrado en producción y cubierto por gate de arquitectura: USB SSD exacto QD4, SATA SSD QD8, NVMe QD16 como capacidades iniciales.
- H-10: lectura Direct I/O del origen con `OVERLAPPED`/async real.
- H-11/H-12/H-13: rutas antiguas de verificación y APIs obsoletas eliminadas.
- Verificación automática CRC32 con `FastVerificationReader` como única ruta post-copia.
- P0: backlog por dispositivo convertido en soft watermark; el presupuesto global de buffers compartidos sigue siendo el límite duro.
- P1 CRC32: slicing-by-8 dentro de la única API `FastCrc32.Compute`.

## Prioridad actual

### P1 — Direct I/O selectivo de escritura

Implementar como estrategia activa por archivo, con buffered como fallback documentado. Requisitos:
- elegibilidad por topología/media/alineación;
- `NO_BUFFERING | OVERLAPPED`;
- offsets y buffers alineados;
- reutilizar el mismo payload de `SharedBlock` para N destinos cuando sea elegible, evitando una copia completa de RAM por destino;
- tail exacto sin padding visible en EOF;
- fallback solo para errores de incompatibilidad explícitamente admitidos; fallos de hardware no se deben ocultar;
- telemetría de bytes/operaciones Direct Write y fallbacks;
- gate extremo-a-extremo que pruebe wiring desde el writer real.

### P1 — QD adaptativo sin techos arbitrarios

QD4/QD8/QD16 son puntos iniciales, no objetivos finales. Medir throughput/latencia por dispositivo y permitir subir o bajar dinámicamente. NVMe no queda limitado conceptualmente a QD16 si hardware/dataset muestran ganancia por encima; tampoco se debe mantener QD alto cuando empeora throughput, latencia o presión de memoria.

### P1 — eliminar clamp QD2 de verificación con presupuesto de memoria

`FastVerificationReader` todavía limita Direct I/O de verificación a QD2. No sustituirlo por QD16 fijo: cada lectura puede fijar un buffer grande. Crear una ventana gobernada por bytes/memoria y capacidad física para que la profundidad pueda crecer cuando haya RAM y dispositivo disponible sin multiplicar memoria sin control.

### P1 — mismo dispositivo físico origen/destino

Coordinar prefetch de origen y escritura cuando comparten disco, especialmente HDD, para evitar seek thrash sin imponer restricciones a dispositivos físicamente independientes.

### P1 — medir flush/commit/recovery en hot path

Instrumentar `FlushFileBuffers`, commit y checkpoint por archivo antes de modificar durabilidad; agrupar/solapar solo cuando se preserve el contrato de recovery.

## Benchmark físico contra ExtremeCopy

Usar mismo origen, destinos, dataset y opciones. Registrar tiempo de copia/verificación/total, MB/s origen y cada destino, throughput lógico agregado, CPU/RAM, fases de telemetría, backlog/QD y fallbacks Direct I/O.

La meta mínima es paridad reproducible. Superar ExtremeCopy requiere evidencia física A/B; CI y una arquitectura potencialmente superior no constituyen por sí solos una prueba de rendimiento.

## Regla permanente de unificación

Una optimización se cierra solo cuando el consumidor real usa la nueva ruta, las rutas equivalentes antiguas fueron migradas/eliminadas, no queda código huérfano, existe gate de arquitectura y suite completa + WinUI Release pasan. Cualquier regresión devuelve el ítem a PARCIAL.
