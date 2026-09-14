# FAN-OUT Performance Audit & Roadmap

## Objetivo

Igualar o superar el comportamiento de ExtremeCopy en FAN-OUT sobre destinos físicos independientes sin debilitar integridad, recovery, cancelación, memoria acotada ni seguridad de topología. El rendimiento solo se considera demostrado mediante A/B reproducible en hardware Windows físico.

## Arquitectura vigente en `main`

- FAN-OUT único: una lectura del origen alimenta a todos los destinos activos mediante `SharedBlock` con conteo de referencias.
- Bloque grande: **32 MiB**.
- Prefetch físico máximo: **8** bloques; pipeline de hash: **4**.
- BLAKE3 del origen/SkipSame/recovery permanece separado de la verificación post-copia.
- Lectura principal SSD elegible: Direct I/O con buffers alineados y `NO_BUFFERING | SEQUENTIAL_SCAN | OVERLAPPED`, usando I/O async real.
- Verificación post-copia automática: CRC32 por bloque generado una sola vez durante FAN-OUT; read-back Direct I/O overlapped cuando es elegible y fallback buffered async cuando no lo es.
- `FastCrc32.Compute` usa slicing-by-8 como única implementación de producción.
- Escritura actual: buffered, offsets explícitos, QD2 selectivo en SSD calificados. **No existe todavía Direct I/O de escritura.**
- Scheduler por dispositivo físico: QD es un límite duro de I/O físico. **H-04 sigue PARCIAL**: el intento de adquisición multi-slot atómica del 2026-09-14 provocó regresiones FAN-OUT (`fileOffset`) y fue retirado; no se considera cerrado.
- El backlog por rama es un **soft watermark** de presión y ya no puede bloquear al productor mientras exista memoria FAN-OUT global disponible.
- El límite duro de payload en vuelo es `AdaptiveByteBudget`.
- Estado interno fuera del árbol copiado en `.disk-duplicator-state/<state_id>`.

## Cerrado e integrado

- Topología física e identidad con política conservadora cuando no puede demostrarse el disco real.
- QD2 selectivo en SSD elegibles.
- Buffers de origen alineados y Direct I/O.
- H-10: lectura Direct I/O del origen con `OVERLAPPED`/async real.
- H-11/H-12/H-13: rutas antiguas de verificación y APIs obsoletas eliminadas.
- Verificación automática CRC32 con `FastVerificationReader` como única ruta post-copia.
- P0: backlog por dispositivo convertido en soft watermark; el presupuesto global de buffers compartidos sigue siendo el límite duro.
- P1 CRC32: slicing-by-8 dentro de la única API `FastCrc32.Compute`.

## Prioridad actual

### P0 — restaurar baseline verde tras H-04

El intento de reemplazar el scheduler QD2 por una cola multi-slot atómica falló 9/110 tests, incluyendo FAN-OUT multiblock, prefetch, pause/resume y fast-path, con `ArgumentOutOfRangeException(fileOffset)`. Se restauró el scheduler previamente probado. No avanzar la arquitectura de escritura sobre un baseline rojo.

### P1 — H-04 / scheduler multi-slot correcto

Rediseñar la reserva multi-slot sin cambiar accidentalmente la temporización/contrato del writer. Debe preservar cancelación exacta, no retener slots parciales y pasar toda la suite FAN-OUT antes de considerarse cerrado. Este trabajo debe converger con la futura API QD4/QD8, no crear otra capa temporal.

### P1 — Direct I/O selectivo de escritura

Implementar como estrategia activa por archivo, con buffered como fallback documentado. Requisitos: elegibilidad por topología/media/alineación; `NO_BUFFERING | OVERLAPPED`; offsets/buffers alineados; tail exacto; fallback cerrado; telemetría de activación/fallback. El mismo `SharedBlock` debe reutilizarse para N destinos cuando sea elegible, evitando copias RAM por destino.

### P1 — QD4/QD8 adaptativo

No asumir que QD2 es óptimo. Evaluar QD4 en SATA/USB-SSD exactos y QD4/QD8 en NVMe cuando el writer soporte más de dos operaciones reales en vuelo. Retroceder si throughput/latencia empeoran o identidad física es incierta.

### P1 — mismo dispositivo físico origen/destino

Coordinar prefetch de origen y escritura cuando comparten disco, especialmente HDD.

### P1 — medir flush/commit/recovery en hot path

Instrumentar `FlushFileBuffers`, commit y checkpoint por archivo antes de modificar durabilidad.

## Benchmark físico contra ExtremeCopy

Usar mismo origen, destinos, dataset y opciones. Registrar tiempo de copia/verificación/total, MB/s origen y cada destino, throughput lógico agregado, CPU/RAM, fases de telemetría, backlog/QD y fallbacks Direct I/O. La meta mínima es paridad reproducible; superar ExtremeCopy requiere evidencia física A/B, no solo arquitectura.

## Regla permanente de unificación

Una optimización se cierra solo cuando el consumidor real usa la nueva ruta, las rutas equivalentes antiguas fueron migradas/eliminadas, no queda código huérfano, existe gate de arquitectura y suite completa + WinUI Release pasan. Cualquier regresión devuelve el ítem a PARCIAL.
