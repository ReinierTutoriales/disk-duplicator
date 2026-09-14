# FAN-OUT Performance Audit & Roadmap

## Objetivo

Igualar o superar el comportamiento de ExtremeCopy en FAN-OUT sobre destinos físicos independientes sin debilitar integridad, recovery, cancelación, memoria acotada ni seguridad de topología. El rendimiento solo se considera demostrado mediante A/B reproducible en hardware Windows físico.

## Arquitectura vigente en `main`

- FAN-OUT único: una lectura física del origen alimenta a todos los destinos activos mediante un mismo `SharedBlock` con conteo de referencias.
- Bloque grande: **32 MiB**.
- Prefetch físico máximo actual: **8** bloques; pipeline de hash: **4**. Son límites pendientes de eliminar/adaptar, no techos de diseño.
- BLAKE3 del origen/SkipSame/recovery permanece separado de la verificación post-copia.
- Lectura principal Direct I/O ya no está limitada a SSD: un HDD local con identidad física exacta y alineación conocida también puede usar `NO_BUFFERING | SEQUENTIAL_SCAN | OVERLAPPED`.
- Los buffers Direct I/O grandes del origen se sobre-alinean a **64 KiB** para que el mismo payload FAN-OUT pueda ser consumido directamente por destinos con sectores/alineaciones distintas dentro del rango soportado.
- El lector Direct I/O reconoce EOF exacto aunque el tamaño lógico final no sea múltiplo del sector.
- Verificación post-copia: CRC32 por bloque generado una sola vez durante FAN-OUT; read-back Direct I/O overlapped cuando es elegible y fallback buffered async cuando no lo es.
- `FastCrc32.Compute` usa slicing-by-8 como única implementación de producción.
- Escritura: **Direct I/O selectivo por archivo** con `NO_BUFFERING | SEQUENTIAL_SCAN | OVERLAPPED` cuando topología/alineación lo permiten; buffered es únicamente fallback documentado.
- Direct Write reutiliza el mismo `SharedBlock` alineado para N destinos. No existe staging completo de 32 MiB por destino. Solo un tail final menor que un sector se copia a un scratch alineado pequeño y después se fija EOF exacto.
- Direct Write tiene fallback sticky por archivo: tras una incompatibilidad admitida no oscila de vuelta a Direct I/O durante reintentos.
- Telemetría productiva expone archivos Direct Write, bytes, operaciones y fallbacks.
- Escritura Direct o buffered usa **profundidad variable real** mediante `DestinationWriteCoordinator`; cada subescritura adquiere un único slot físico justo antes del I/O.
- Capacidades iniciales del scheduler: USB SSD exacto **QD4**, SATA SSD **QD8**, NVMe **QD16**. No son límites de producto: deben poder aumentar/disminuir según evidencia física y política adaptativa.
- Ya no existen `WriteQueueDepthTwoAsync`, `WriteTwoAsync`, `AcquireIoPairAsync`, `IoPairLease` ni `BufferedLargeWriteQueueDepth`.
- El backlog por rama es un **soft watermark** y no bloquea al productor mientras exista memoria FAN-OUT global disponible.
- El límite duro actual de payload en vuelo es `AdaptiveByteBudget`; su máximo fijo de 4 GiB queda pendiente de reemplazo por política basada en memoria real.
- Estado interno fuera del árbol copiado en `.disk-duplicator-state/<state_id>`.

## Cerrado e integrado

- Topología física e identidad con política conservadora cuando no puede demostrarse el disco real.
- Buffers de origen alineados y Direct I/O overlapped para SSD y HDD locales elegibles.
- Fix Direct I/O EOF para tails lógicos no alineados.
- H-04 cerrado mediante eliminación de reservas por pares: cada operación física adquiere/libera un único slot y la profundidad N surge de operaciones independientes concurrentes.
- Writer variable-depth integrado en producción; USB SSD exacto QD4, SATA SSD QD8 y NVMe QD16 son capacidades iniciales, no caps permanentes.
- **P1 Direct I/O de escritura cerrado**: ruta real de `CopyEngine`, una estrategia activa por archivo, mismo `SharedBlock` para N destinos, tail-only staging, EOF exacto, fallback cerrado/sticky, telemetría y gate end-to-end.
- Run de validación de cierre: Windows .NET CI **#173 / 34846562738**, Core Release + WinUI Release x64 + source gate + publish + artifact en verde.
- H-10: lectura Direct I/O del origen con `OVERLAPPED`/async real.
- H-11/H-12/H-13: rutas antiguas de verificación y APIs obsoletas eliminadas.
- Verificación automática CRC32 con `FastVerificationReader` como única ruta post-copia.
- P0 backlog por dispositivo convertido en soft watermark; el presupuesto global de buffers compartidos sigue siendo el límite duro.
- P1 CRC32 slicing-by-8 dentro de la única API `FastCrc32.Compute`.

## Prioridad actual

### P1 — eliminar clamp QD2 de verificación con presupuesto de memoria

`FastVerificationReader` todavía limita Direct I/O de verificación a QD2. Eliminar ese cap. No sustituirlo por otro número fijo: la ventana de lectura debe estar gobernada por bytes de memoria disponibles, tamaño de bloque y capacidad física del scheduler. SSD/NVMe deben poder ocupar tantos slots útiles como memoria y throughput justifiquen; HDD puede aumentar si medición demuestra beneficio.

### P1 — quitar topes fijos de prefetch/hash pipeline

`SourcePrefetchPhysicalCapacity = 8` y `SourceHashPipelineCapacity = 4` son límites artificiales. La cola física ya tiene `AdaptiveByteBudget`; el pipeline debe poder crecer según RAM disponible, starvation del consumidor y presión de entrega, sin duplicar un cap fijo en el `Channel`.

### P1 — eliminar máximo fijo de 4 GiB del FAN-OUT byte budget

`MaximumBufferBudget = 4 GiB` recorta máquinas con mucha RAM. Sustituirlo por una política derivada de memoria física/disponible y reserva de seguridad. `ControlBacklogCapacity` debe migrar en el mismo cierre porque hoy fue dimensionado suponiendo precisamente el máximo de 4 GiB.

### P1 — QD adaptativo sin techos arbitrarios

QD4/QD8/QD16 son puntos iniciales, no objetivos finales. Medir throughput/latencia por dispositivo y permitir subir/bajar dinámicamente. NVMe debe poder explorar QD32/QD64 o superior cuando hardware/dataset muestran ganancia; SATA/USB tampoco quedan conceptualmente clavados a los valores iniciales.

### P1 — mismo dispositivo físico origen/destino

Coordinar prefetch de origen y escritura cuando comparten disco, especialmente HDD, para evitar seek thrash sin imponer restricciones a dispositivos físicamente independientes.

### P1 — medir y reducir flush/commit/recovery del hot path

La telemetría ya registra flush, commit y recovery. Usarla para decidir agrupación/solapamiento preservando el contrato de durabilidad; no mantener una barrera por archivo solo por tradición si puede demostrarse una estrategia equivalente y más rápida.

## Benchmark físico contra ExtremeCopy

Usar mismo origen, destinos, dataset y opciones. Registrar tiempo de copia/verificación/total, MB/s origen y cada destino, throughput lógico agregado, CPU/RAM, fases de telemetría, backlog/QD, profundidad realmente observada y fallbacks Direct I/O.

La meta mínima es paridad reproducible. Superar ExtremeCopy requiere evidencia física A/B; CI y una arquitectura potencialmente superior no constituyen por sí solos una prueba de rendimiento.

## Regla permanente de unificación

Una optimización se cierra solo cuando el consumidor real usa la nueva ruta, las rutas equivalentes antiguas fueron migradas/eliminadas, no queda código huérfano, existe gate de arquitectura y suite completa + WinUI Release pasan. Cualquier regresión devuelve el ítem a PARCIAL.
