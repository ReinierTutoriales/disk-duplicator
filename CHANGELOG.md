# Changelog

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
