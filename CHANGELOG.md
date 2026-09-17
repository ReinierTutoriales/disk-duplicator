# Changelog

## Unreleased — main

## v2.1.1 — 2026-09-16

- FAN-OUT productivo simplificado al modelo compartido: una lectura del origen, bloques de 8 MiB con refcount y pool activo de 256 MiB para todos los destinos.
- Eliminadas del camino productivo las capas de replay, staging por rama, aislamiento por copia privada y exploración adaptativa de queue depth.
- Escritura por destino alineada con el enfoque estable de ExtremeCopy: una operación física en vuelo por dispositivo, `SEQUENTIAL_SCAN`, `NO_BUFFERING` cuando es elegible y fallback buffered seguro; la ruta de destino ya no usa `OVERLAPPED`.
- Recuperación de I/O transitorio sin el corte arbitrario de tres fallos consecutivos, manteniendo cancelación y fallo inmediato para errores no transitorios.
- Verificación streaming con workspace fijo de 8 MiB: relee origen y destinos completados por el mismo offset, compara CRC32C inmediatamente y no conserva historial CRC durante COPY.
- Retry/fallback de verificación convertido a flujo iterativo para evitar crecimiento recursivo de pila.
- Progreso, velocidad y ETA de COPY usan el mismo avance lógico del destino más atrasado; VERIFY expone velocidad lógica y ETA independientes.
- WinUI compactada con Compact Density oficial, `TitleBar` nativo, Mica, barra global gruesa, destinos horizontales autoajustados y verificación siempre activa sin toggle visible.
- Ventana principal reducida y árbol visual aligerado; refresco de telemetría limitado a 4 Hz para minimizar trabajo del UI thread durante las copias.
- Producto C#/.NET-only validado en Core Release, WinUI Release x64 y publicación self-contained.
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

