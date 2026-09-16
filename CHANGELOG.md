# Changelog

## Unreleased — main

- Recuperación de I/O unificada para buffered, Direct-write y Direct-read: los errores Win32 transitorios reducen presión mediante `DeviceScheduler` y dejan de depender de sleeps/retries fijos.
- Verificación CRC32C posterior a la copia configurable desde WinUI y persistida en perfiles `.rcopy`; activada por defecto pero desactivable para benchmarks y copias sin verify.
- FAN-OUT con aislamiento por rama: el productor entrega a un staging independiente por destino y una rama rezagada se desacopla del `SharedBlock` según su ventana real de QD/backlog, evitando retener indefinidamente el presupuesto global del origen.
- Replay retirado del hot path del productor: el spill a disco temporal se ejecuta en la etapa de la rama lenta. Si no existe placement físico seguro o el spill falla, la rama usa un buffer propio sin bloquear las demás.
- Eliminado `BranchReplayGate` y su política temporal de 500 ms; la activación de aislamiento se deriva ahora de presión de payload, tamaño de bloque, QD actual y backlog objetivo del dispositivo.
- Admisión de escrituras pendientes acotada dinámicamente por destino mediante el `DeviceScheduler`, evitando crecimiento ilimitado de tareas en espera.
- El backlog físico permanece contabilizado hasta la finalización/liberación real del payload, no solo hasta sacarlo del canal.
- El tamaño global de transferencia deja de reducirse por el QD máximo de una sola rama y deja de promediar una rama lenta con las rápidas para decidir bytes por operación.
- `CopyJob.DiagnosticsSnapshot()` expone `BranchFlows` con cola de entrada, payload pendiente, pico de payload, backlog físico, I/O outstanding, QD actual y disponibilidad de replay.
- Contratos de arquitectura actualizados para impedir la reaparición de replay inline, del gate temporal retirado y de pending writes sin ventana de admisión.
- El corte se valida en el workflow permanente de Windows con suite Core Release, build WinUI x64 y publish self-contained antes de pasar a pruebas físicas.

## v2.1.0 — 2026-09-15

- FAN-OUT productivo unificado alrededor de `SharedBlock`, replay por rama lenta y tamaño de transferencia adaptativo; eliminadas capas intermedias sin consumidor productivo.
- Direct I/O `NO_BUFFERING + SEQUENTIAL_SCAN + OVERLAPPED` integrado para source, destinos y verify cuando la topología/alineación es elegible, con fallback buffered seguro.
- Escrituras full-block por offset explícito con multi-block in-flight y QD adaptativo por dispositivo; el antiguo QD2 fijo permanece eliminado.
- Replay por rama lenta limitado a un dispositivo físicamente distinto demostrado con identidad exacta, con entrada/salida sostenidas e histéresis para evitar spill por picos momentáneos.
- Reintentos buffered gobernados por clasificación explícita de errores transitorios; errores permanentes dejan de consumir retries inútiles.
- Verificación unificada en CRC32C/Castagnoli, incluida nomenclatura de código/telemetría, aceleración hardware, fallback software y clasificación `VerificationBottleneck`.
- Recovery endurecido frente a rewrites interrumpidos de journal/manifest, cleanup post-commit fallido, `.part` huérfanos y concurrencia entre procesos mediante lease exclusivo por destino sin romper migración de estado legacy.
- Fallo de una rama FAN-OUT aislado y recuperable sin corromper ni detener ramas sanas; cancelación desde pausa desbloquea correctamente las esperas.
- La UI fuerza throughput global y por destino a `0.0 B/s` mientras el trabajo está pausado, sin mostrar velocidad residual del promedio anterior.
- `StorageWritePolicy`, `FanoutPerformancePolicy` y `ExplicitOffsetWriter` eliminados tras quedar reemplazados por las rutas productivas únicas.
- `AppLogo.png` regenerado desde el ICO válido con transparencia real en los bordes y estructura PNG íntegra.
- Documentación y CI sincronizados con la arquitectura productiva actual.

## v2.0.0 — 2026-09-12

RepartoCopier 2.0.0 establece el nuevo baseline nativo de Windows en C#/.NET 10 + WinUI 3 y reemplaza por completo la implementación activa anterior en Rust.

### Motor de copia

- FAN-OUT como único modo de copia: una lectura del origen alimenta simultáneamente a todos los destinos activos.
- Preservación exacta de la carpeta raíz seleccionada, jerarquía completa y carpetas vacías.
- Buffers compartidos con conteo de referencias para evitar releer el origen por destino.
- Prefetch de origen, presupuesto de RAM y backpressure por destino adaptativos.
- Escrituras asíncronas secuenciales, preallocation en archivos grandes y reintentos controlados.
- `WriteThrough` selectivo para archivos de un solo chunk; archivos grandes conservan una escritura cached secuencial con flush durable final.
- BLAKE3 paralelo (`UpdateWithJoin`) en bloques multi-megabyte, manteniendo igualdad exacta con la ruta serial.

### Integridad y recuperación

- Verificación BLAKE3 física de los destinos.
- Recovery transaccional mediante manifest/journal fuera del árbol copiado.
- `.part`/backup y commit endurecidos.
- Detección de mutaciones del árbol de origen durante la operación.
- Rechazo fail-closed de symlinks, junctions, reparse points y solapamientos peligrosos.
- Pausa, reanudación y cancelación sin bloquear ThreadPool.

### Telemetría y rendimiento

- `CopyJob.DiagnosticsSnapshot()` mide lectura/hash del origen, espera de RAM, FAN-OUT, cola, escritura, flush durable, commit, recovery y verificación.
- Gobernador de CPU basado en carga real del proceso para trabajo de hashing/verificación.
- Baseline de rendimiento congelado: futuras optimizaciones requieren evidencia reproducible en hardware físico y no pueden degradar integridad ni otros escenarios representativos.

### Interfaz y plataforma

- C# 14 / .NET 10.
- WinUI 3 + XAML y Windows App SDK 2.4.
- Windows 10 2004 (19041) o posterior, x64.
- Pickers y comportamiento visual nativos de Windows.

### Validación

- Suite Core Release completa aprobada.
- Stress FAN-OUT, pausa/reanudación, prefetch, recovery, árboles densos, BLAKE3 y ruta WriteThrough.
- Build WinUI Release x64 con 0 warnings y 0 errores en el gate de integración.

La versión histórica v1.4.3 permanece intacta y publicada como referencia de la generación anterior.
