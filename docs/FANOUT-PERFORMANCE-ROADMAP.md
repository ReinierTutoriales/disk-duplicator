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
- `FastCrc32.Compute` usa slicing-by-8 como única implementación de producción; la equivalencia IEEE CRC32 queda cubierta por vector estándar, bordes 7/8/9 y comparación aleatoria contra referencia byte-a-byte en tests.
- Escritura actual: buffered, offsets explícitos, QD2 selectivo en SSD calificados. **No existe todavía Direct I/O de escritura.**
- Scheduler por dispositivo físico: QD es un límite duro de I/O físico; el backlog por rama es ahora un **soft watermark** de presión y ya no puede bloquear al productor mientras exista memoria FAN-OUT global disponible.
- El límite duro de payload en vuelo es `AdaptiveByteBudget`; evita crecimiento ilimitado aunque una rama lenta supere ampliamente su watermark.
- Estado interno fuera del árbol copiado en `.disk-duplicator-state/<state_id>`.

## Watermarks vigentes por rama

| Rama | Soft backlog watermark | QD máximo actual |
|---|---:|---:|
| Network | 32 MiB | 1 |
| Conservador/virtual/storage spaces | 64 MiB | 1 |
| Rotational HDD | 128 MiB | 1 |
| USB flash / USB SSD incierto | 128 MiB | 1 |
| USB SSD exacto | 256 MiB | 2 |
| SATA SSD | 256 MiB | 2 |
| NVMe | 512 MiB | 2 |

El watermark mide presión/lag de la rama; **no aplica backpressure al productor**. QD controla I/O físico simultáneo. El `AdaptiveByteBudget` global es el backpressure duro de memoria.

## Cerrado e integrado

- Topología física e identidad con política conservadora cuando no puede demostrarse el disco real.
- QD2 selectivo desde archivos de 8 MiB en SSD elegibles.
- Buffers de origen alineados y Direct I/O.
- H-10: la lectura Direct I/O del origen usa `OVERLAPPED`/async real; se eliminó la sesión síncrona.
- H-11/H-12/H-13: se eliminaron la verificación antigua escondida en `HashFileAsync`, APIs síncronas obsoletas, ramas nulas/test-only innecesarias y telemetría de verify que ya no tenía productor.
- Verificación automática CRC32 con `FastVerificationReader` como única ruta post-copia.
- P0: eliminado el hard gate de backlog por dispositivo. Una rama que supera su watermark entra en overflow sin esperar a que drene; el productor solo puede quedar frenado por el presupuesto global de buffers compartidos, cancelación o control-plane global. Se eliminó la cola `BacklogWaiter` y existe gate que impide reintroducirla.
- P1 CRC32: reemplazado el loop byte-a-byte por slicing-by-8 dentro de la única API `FastCrc32.Compute`; no existe segunda ruta de checksum de producción.

## Prioridad actual

### P1 — Direct I/O selectivo de escritura

Implementar sin sustituir ciegamente la ruta buffered. Requisitos:
- elegibilidad por topología/media/alineación;
- `NO_BUFFERING | OVERLAPPED` y offsets/buffers alineados;
- tail correcto;
- fallback cerrado solo para errores compatibles;
- ruta buffered actual permanece como fallback, no como segunda política contradictoria;
- telemetría que demuestre activación/fallback.

### P1 — QD4/QD8 adaptativo

No asumir que QD2 es óptimo. Evaluar QD4 en SATA/USB-SSD exactos y QD4/QD8 en NVMe cuando la ruta de escritura soporte más de dos operaciones reales en vuelo. La política debe retroceder automáticamente si throughput o latencia empeoran o si la identidad física es incierta.

### P1 — mismo dispositivo físico origen/destino

Coordinar prefetch de origen y escritura cuando comparten el mismo disco, especialmente HDD, para evitar seek thrash. Mantener QD1 en ese caso hasta tener una política compartida medida.

### P1 — medir flush/commit/recovery en el hot path

`FinishFile` todavía puede pagar `FlushFileBuffers`, commit y checkpoint de recovery por archivo. No eliminar durabilidad a ciegas: instrumentar y, si domina el tiempo, agrupar/solapar trabajo sin dejar una segunda ruta de finalización.

### P2 — BLAKE3 y small-file metadata

Optimizar BLAKE3 o metadata de archivos pequeños solo si la telemetría demuestra que dominan después de cerrar las rutas de escritura más importantes.

## Benchmark físico contra ExtremeCopy

Después de cada mejora relevante, usar mismo origen, destinos, dataset y opciones, con datos suficientemente grandes para superar cachés transitorias. Registrar:

- tiempo de copia, verificación y total;
- MB/s del origen y de cada destino;
- throughput lógico agregado;
- CPU/RAM;
- `SourceRead`, `SourceHash`, `FanoutWait`, `QueueWait`, `Write`, `VerifyRead`, `VerifyHash`;
- backlog/QD por dispositivo y fallbacks Direct I/O;
- topología/bus/media reales.

La meta mínima es paridad reproducible con ExtremeCopy. “Arquitectura parecida” o CI hospedado no cuentan como prueba de rendimiento físico.

## Regla permanente de unificación

Una optimización no está terminada cuando aparece una ruta nueva. Se cierra solo cuando:
1. el consumidor real usa la nueva ruta;
2. se rastrean y migran todos los consumidores equivalentes;
3. se elimina código, parámetros, telemetría y tests obsoletos;
4. no quedan dos implementaciones del mismo propósito salvo un fallback explícito;
5. existe un gate que impide reintroducir la arquitectura retirada;
6. suite completa + WinUI Release pasan.
