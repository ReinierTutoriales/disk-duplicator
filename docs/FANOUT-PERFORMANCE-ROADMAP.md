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
- Escritura actual: buffered, offsets explícitos, QD2 selectivo en SSD calificados. **No existe todavía Direct I/O de escritura.**
- Scheduler y backlog por dispositivo físico; identidad incierta se trata conservadoramente.
- Estado interno fuera del árbol copiado en `.disk-duplicator-state/<state_id>`.

## Ventanas vigentes por rama

| Rama | Backlog | QD máximo actual |
|---|---:|---:|
| Network | 32 MiB | 1 |
| Conservador/virtual/storage spaces | 64 MiB | 1 |
| Rotational HDD | 128 MiB | 1 |
| USB flash / USB SSD incierto | 128 MiB | 1 |
| USB SSD exacto | 256 MiB | 2 |
| SATA SSD | 256 MiB | 2 |
| NVMe | 512 MiB | 2 |

El backlog absorbe jitter; QD controla I/O físico simultáneo. No deben confundirse ni aumentarse sin evidencia.

## Cerrado e integrado

- Topología física e identidad con política conservadora cuando no puede demostrarse el disco real.
- QD2 selectivo desde archivos de 8 MiB en SSD elegibles.
- Buffers de origen alineados y Direct I/O.
- H-10: la lectura Direct I/O del origen usa `OVERLAPPED`/async real; se eliminó la sesión síncrona.
- H-11/H-12/H-13: se eliminaron la verificación antigua escondida en `HashFileAsync`, APIs síncronas obsoletas, ramas nulas/test-only innecesarias y telemetría de verify que ya no tenía productor.
- Verificación automática CRC32 con `FastVerificationReader` como única ruta post-copia.

## Prioridad actual

### P0 — eliminar head-of-line entre ramas diferidas

`DeliverDataAsync` entrega primero a las ramas con crédito inmediato, pero después espera la lista `deferred` secuencialmente. Si A sigue saturada y B ya tiene crédito, B todavía queda detrás de A.

Corrección requerida:
- reservas de backlog de ramas diferidas independientes;
- entregar cada referencia del bloque tan pronto como su propia rama tenga crédito;
- conservar orden dentro de cada destino, conteo de referencias, límites de memoria, cancelación y aislamiento de fallos.

Gates:
- dos ramas diferidas liberadas en orden inverso;
- la rama lista primero recibe el bloque primero;
- cancelación/fallo no fuga backlog ni referencias.

### P1 — Direct I/O selectivo de escritura

Implementar solo después de cerrar P0 y sin sustituir ciegamente la ruta buffered. Requisitos:
- elegibilidad por topología/media/alineación;
- `NO_BUFFERING | OVERLAPPED` y offsets/buffers alineados;
- tail correcto;
- fallback cerrado solo para errores compatibles;
- ruta buffered actual permanece como fallback, no como segunda política contradictoria;
- telemetría que demuestre activación/fallback.

### P1 — acelerar CRC32

`FastCrc32` sigue siendo tabla byte-a-byte. Optimizar únicamente con equivalencia IEEE CRC32 probada (por ejemplo slicing-by-8/16 o una ruta intrínseca validada) y medir CPU vs `VerifyReadTime`.

### P1 — QD4 adaptativo

No subir QD globalmente. Evaluar QD4 solo en SSD/NVMe/USB-SSD exactos después de P0 y Direct I/O de escritura, con rollback automático/política conservadora si throughput o latencia empeoran.

### P1 — mismo dispositivo físico origen/destino

Coordinar prefetch de origen y escritura cuando comparten el mismo disco, especialmente HDD, para evitar seek thrash. Mantener QD1 conservador hasta tener una política compartida medida.

### P2 — BLAKE3 y metadata/durabilidad

Optimizar BLAKE3, flush/commit o small-file metadata solo si la telemetría demuestra que dominan. No debilitar recovery para ganar un benchmark.

## Benchmark físico contra ExtremeCopy

Solo después de cerrar P0 y las mejoras seleccionadas. Usar mismo origen, destinos, dataset y opciones, con datos suficientemente grandes para superar cachés transitorias. Registrar:

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
