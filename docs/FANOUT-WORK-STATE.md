# Estado de trabajo — motor FAN-OUT por destino

> Documento de continuidad. Leer esto antes de retomar cambios en el motor.
>
> Rama de trabajo: `feature/fanout-per-destination-spill`
>
> No mezclar ni modificar `main` hasta terminar validación real.

## Objetivo vigente

RepartoCopier debe leer cada bloque del origen una sola vez y distribuirlo a todos los destinos que lo necesiten, sin permitir que un destino lento reduzca artificialmente el rendimiento de los rápidos.

Cada destino tiene ciclo de vida independiente. Cuando termina copia + verificación debe hacer flush durable, cerrar handles de datos/verificación/recovery, liberar `DestinationStateLease` y quedar en `Releasable`. Un USB en ese estado se puede retirar mientras los demás continúan. Un disco interno queda igualmente liberado lógicamente; su extracción física requiere apagar el equipo.

Al relanzar una copia, los archivos ya confirmados deben saltarse por destino. El resume permanece a nivel de archivo: no añadir journal/checkpoint por bloque, replay store, base de datos ni historial gigante en RAM.

Los archivos se procesan primero por cantidad de destinos que todavía los necesitan; empate = orden original. Así se maximiza un read del origen para múltiples escrituras.

## Arquitectura implementada

### QD fijo por clase de hardware

El QD se decide una sola vez al inicio. No existe exploración/adaptación de QD en runtime y el backlog nunca reduce QD.

| Clase | QD | Backlog target |
|---|---:|---:|
| NVMe | 8 | 512 MiB |
| SATA SSD / USB SSD | 4 | 256 MiB |
| HDD rotacional | 2 | 128 MiB |
| Network | 2 | 32 MiB |
| USB flash | 1 | 128 MiB |
| desconocido/conservador | 1 | 64 MiB |

### FAN-OUT y spill

- Pool compartido: **256 MiB**.
- Bloque de producción: **8 MiB**.
- Spill global máximo: **512 MiB**.
- Techo por destino: `clamp(512 MiB / destinosActivos, 16 MiB, BacklogTargetBytes)`.
- Canal independiente por destino.
- Ruta normal: `source read -> shared block -> referencia -> write`; sin copia privada por destino.
- Si un destino alcanza presión de backlog, sólo esa rama usa un bloque privado alineado/reutilizable.
- La rama en spill queda fuera de las referencias del bloque compartido.
- Recupera a normal cuando `SpillBytes == 0` y backlog <= 50% del target.
- Si agota su techo de spill, falla sólo ese destino; los demás continúan.
- Spill/backlog no modifica QD.

Memoria principal acotada: aproximadamente 256 MiB shared + hasta 512 MiB spill, más runtime/hash/direct-I/O/UI. No aumentar buffers sin benchmark.

### Direct I/O

Se encontró una corrupción real bajo QD concurrente: la ruta anterior hacía `SetFilePointerEx + WriteFile` sobre un handle compartido. Con varias escrituras concurrentes el file pointer podía competir y colocar un bloque en el offset incorrecto.

Corregido: la escritura Direct I/O usa `RandomAccess.WriteAsync(handle, data, offset, token)`, es decir, offset explícito por operación. Se mantiene `NO_BUFFERING + SEQUENTIAL_SCAN + OVERLAPPED`, de modo que el QD fijo puede representar I/O realmente concurrente sin compartir cursor de archivo.

Existe un test runtime con QD=8 y ocho bloques concurrentes con patrones diferentes que verifica que cada bloque termina exactamente en su offset.

## Ciclo de vida y liberación

Orden requerido de un destino exitoso:

`última escritura -> esperar I/O pendiente -> flush durable -> cerrar handle de datos -> verificar -> cerrar handles de verificación -> cerrar recovery manifest/journal -> liberar DestinationStateLease -> Releasable`

Nunca anunciar retiro seguro antes de esa secuencia.

`RecoveryCheckpointWriter.Dispose()` cierra manifest y journal. Hay prueba runtime que después abre ambos con `FileShare.None`.

`DestinationStateLease` también tiene prueba runtime de exclusividad y reacceso después de `Dispose`.

La verificación ya no espera a que todos los writers terminen: los writers terminados se verifican por lotes mientras los lentos continúan drenando.

Si una verificación falla, el error se conserva pero todos los writers restantes deben terminar/drenar antes de liberar el pool FAN-OUT y propagar el error. Un fallo de otro destino no debe convertir un destino ya `Releasable` o `Done` en `Failed`.

## Resume seguro

- `SkipSame` verifica candidatos por tamaño/timestamp y hash antes de saltarlos.
- Recovery preverified evita recalcular hashes de archivos ya confirmados por checkpoint.
- No considerar mera existencia del archivo como prueba.
- No añadir resume por bloques.
- Orden de producción: archivos necesitados por más destinos primero.

## UI / telemetría

La UI ya separa progreso lógico de throughput real:

- `Velocidad fuente` = `SourceRead5sBytesPerSecond`.
- Cada destino muestra `SustainedWrite5sBytesPerSecond`.
- La derivada del destino activo más atrasado se usa sólo para ETA/progreso general; no se presenta como velocidad física de todos.
- `Releasable` se excluye de destinos activos.
- Estado global puede indicar que hay destinos liberados/listos para retirar mientras otros continúan.

Mantener esta separación. No volver a etiquetar la velocidad lógica del progreso como velocidad real del motor.

## CI confirmado

Último CI completamente confirmado antes de los dos commits de hardening de verificación:

- Commit: `08df8d9234d829dcb71891ef3b1d475b6d6820be`
- Windows .NET CI run #340.
- **206 passed, 0 failed, 1 skipped; total 207**.
- WinUI: **0 warnings, 0 errors**.

El único skipped es intencional:

`MeasureSpillCopyCandidatesWithoutChangingProductionPolicy`

Está marcado `[Ignore]` porque es benchmark sintético de memoria para candidatos 1/2/4/8/16 MiB y debe ejecutarse explícitamente en hardware objetivo. No debe elegir el tamaño de bloque usando una VM de CI.

Después de ese CI se añadieron:

- `4958e59a9d6fb521be1a69137e3ba7e92080a0e2` — drenar writers antes de propagar fallos de verificación y preservar destinos terminales.
- `bc82367234e7d48d99c53cf800535b3210ab9a68` — tests de aislamiento/orden de ese caso.

**Al retomar: primero comprobar CI del HEAD actual. No asumir verde hasta verlo.**

## Tests/garantías importantes

Hay cobertura para:

- tabla exacta de QD estático;
- backlog/spill no altera QD;
- techo global de spill <=512 MiB;
- fair-share según destinos activos;
- ownership y liberación de bloques privados;
- rama lenta no retiene páginas shared de ramas rápidas;
- drenaje FAN-OUT termina con shared/spill/pending/queued en cero;
- pause/resume rápido con múltiples destinos preserva datos;
- QD=8 Direct I/O preserva offsets concurrentes;
- recovery manifest/journal quedan sin handles después de dispose;
- lease de destino se libera;
- verificación precede estado retirable;
- recovery-close failure libera lease pero no anuncia retiro seguro;
- destinos terminados pueden verificar/liberarse antes que writers lentos;
- UI usa throughput real de origen/destinos;
- candidato de benchmark 1/2/4/8/16 MiB mantiene 8 MiB como default de producción.

## Pendientes al retomar

1. Comprobar CI del HEAD `bc823672...` y arreglar cualquier regresión antes de optimizar.
2. Auditar que un destino exitoso deje de contar como destino activo para el cálculo dinámico del spill fair-share. Los fallidos decrementan; confirmar/corregir el camino exitoso sin mezclar `IsActive` con semántica de éxito.
3. Añadir integración de job completo: mientras otro destino sigue activo, un destino en `Releasable` debe permitir acceso exclusivo/rename/delete a sus archivos y estado recovery. Esto demuestra ausencia de handles del copiador; no demuestra que ningún otro proceso de Windows tenga el volumen.
4. Revisar presentación compacta de velocidades por destino. Actualmente se usa `CurrentPathText`; comprobar que no destruya información útil del archivo/path actual. Evitar UI pesada.
5. Diferenciar en UI, si se expone metadata removible de forma limpia, USB `COMPLETADO · SE PUEDE RETIRAR` frente a disco interno `LIBERADO`. No etiquetar un interno como físicamente retirable.
6. Ejecutar benchmarks en hardware real antes de cambiar 8 MiB, 256 MiB, 512 MiB o QD.
7. Medir pipeline del origen antes de añadir profundidad/complexidad adicional. No agregar mecanismos sólo por teoría.

## Principio de rendimiento

Rendimiento de un destino ≈ `min(capacidad origen, capacidad destino, capacidad enlace compartido)`.

No asumir que todos los destinos deben correr a la velocidad del más lento. El origen se lee una vez y el bloque se comparte. Sólo la contención física real de disco/controlador/hub/enlace debe limitar peers.

La ruta caliente debe seguir siendo simple. No introducir replay, adaptive QD, journaling por bloque, scans innecesarios, timers por bloque ni telemetría costosa por operación.

## Escenario de aceptación real

Escenario usado como referencia: USB 3.0 + SATA HDD + SD PCIe + 2 NVMe, aproximadamente 28.24 GB.

Resultado anterior observado: alrededor de 5:57 y NVMe ~79 MB/s. El objetivo no es imponer un número artificial, sino que los NVMe/SSD rápidos no queden atados por retención interna causada por HDD/USB lentos. El tiempo total de un job donde todos deben completar seguirá limitado físicamente por el destino exitoso más lento.

Un USB rápido debe poder terminar, verificar, cerrar y quedar listo para retirar mientras HDD/otros continúan.

## Reglas para próximos cambios

- Trabajar sólo en `feature/fanout-per-destination-spill`; no tocar `main`.
- No mergear PR #3 hasta terminar validación.
- Integridad primero; no ocultar fallos bajando QD.
- No afirmar que un test/CI pasó sin ejecución confirmada.
- Medir antes de cambiar números.
- Si una optimización añade coste permanente al hot path, exigir evidencia clara de beneficio.
- Tests pueden ser complejos; producción no.


## Fase final de validación (2026-09-21)

Se añadió un benchmark físico opt-in en `PhysicalDiskPerformanceBenchmarkTests.MeasureRealSourceAndDestinationThroughput`.

Variables:
- `REPARTOCOPIER_BENCH_SOURCE`: carpeta de trabajo en el disco origen.
- `REPARTOCOPIER_BENCH_DESTINATIONS`: carpetas base de destinos, separadas por `Path.PathSeparator` (en Windows, punto y coma).
- `REPARTOCOPIER_BENCH_GIB`: tamaño del payload; default 4 GiB.
- `REPARTOCOPIER_BENCH_VERIFY=0`: desactiva verify para medir sólo copy.
- `REPARTOCOPIER_BENCH_KEEP=1`: conserva los datos generados.

El benchmark es opt-in mediante `REPARTOCOPIER_RUN_PHYSICAL_BENCHMARK=1`; sin esa variable termina como inconclusive/skipped en CI. Sólo opera dentro de subdirectorios únicos `repartocopier-bench-...` bajo las rutas suministradas. No debe ejecutarse sobre una raíz de volumen sin intención explícita.

Se añadió `NonDestructiveStorageArchitectureTests`, que prohíbe APIs/tokens de formateo, particionado, inicialización, raw `PhysicalDrive`, lock/dismount y extensión de volumen en código de producto. La ruta Direct I/O debe seguir usando un path de archivo normal, nunca un dispositivo raw.

Se añadió prueba de liberación a nivel job: al observar `Releasable`, intenta abrir archivo copiado, manifest y journal con `FileShare.None` y reacquirir `DestinationStateLease` antes de finalizar el job global.

También se corrigió el conteo live de destinos: un destino liberado exitosamente ejecuta `MarkSuccessfullyReleased()`, libera su lease y sale exactamente una vez del active-destination count usado por spill fair-share.

Pendiente inmediato: esperar CI del HEAD y corregir cualquier fallo real o de contrato. Después ejecutar el benchmark físico en el hardware objetivo para obtener MB/s reales por NVMe/SATA/USB/HDD/SD y combinaciones.


## Referencia de rendimiento físico — baseline del motor actual

No registrar aquí resultados históricos del motor anterior como si fueran baseline vigente. El ~79 MB/s del escenario de aceptación precede Parte A/B y sirve sólo como referencia histórica.

El baseline válido debe generarse con el HEAD actual y el test `PhysicalDiskPerformanceBenchmarkTests.MeasureRealSourceAndDestinationThroughput`. Matriz mínima recomendada, usando el mismo NVMe como origen y el mismo payload en cada corrida:

| Corrida | Origen | Destino(s) | Copy MiB/s | Source MiB/s | Destino MiB/s | QD pico | Hash/pool/spill | Commit |
|---|---|---|---:|---:|---:|---:|---|---|
| 1 | NVMe | NVMe | PENDIENTE | PENDIENTE | PENDIENTE | PENDIENTE | PENDIENTE | HEAD |
| 2 | NVMe | SATA SSD | PENDIENTE | PENDIENTE | PENDIENTE | PENDIENTE | PENDIENTE | HEAD |
| 3 | NVMe | HDD | PENDIENTE | PENDIENTE | PENDIENTE | PENDIENTE | PENDIENTE | HEAD |
| 4 | NVMe | USB SSD/flash | PENDIENTE | PENDIENTE | PENDIENTE | PENDIENTE | PENDIENTE | HEAD |
| 5 | NVMe | NVMe + SATA SSD + HDD + USB | PENDIENTE | PENDIENTE | por destino | por dispositivo | PENDIENTE | HEAD |

Regla de comparación: medir también cada dispositivo con una referencia local en la misma máquina y sesión de prueba. El objetivo no es alcanzar una cifra publicitaria, sino determinar qué fracción de la capacidad física observada consigue el motor. Mantener payload, verify, rutas y condiciones constantes entre baseline y cualquier optimización posterior.

No implementar prefetch/profundidad concurrente de lectura antes de completar este baseline. Si después se cambia el pipeline, repetir exactamente esta matriz y registrar `antes -> después -> delta %`.


## Ruta de instrumentación para baseline — 2026-09-23

La instrumentación del baseline es observacional: no puede modificar QD, fan-out, pool, spill, tamaño de bloque, orden de producción ni admisión de I/O. No añadir logging por bloque, timers periódicos por bloque, allocations diagnósticas ni locks nuevos al hot path.

Métricas vigentes: origen = SourceReadBytes/SourceReadTime; BLAKE3 = SourceHashBytes/SourceHashTime; pool shared = BufferWaitTime separado de I/O; spill = SpillCopyBytes/SpillCopyTime (TryReserve es no bloqueante); escritura global = WrittenBytes/WriteTime; escritura por destino = DestinationSnapshot.WriteIoBytes, WriteIoOperations, WriteIoTime y WriteIoBytesPerSecond; QD = DeviceIoSnapshot.OutstandingIo y PeakOutstandingIo. No añadir promedio temporal de QD hasta que una medición demuestre que hace falta.

El tiempo de escritura por destino reutiliza exactamente el Stopwatch.GetElapsedTime(started) que ya mide la escritura global. DestinationProgress lo acumula dentro del mismo _gate que AddWritten ya tomaba; no se introduce un lock ni un timestamp adicional. La sobrecarga histórica AddWritten(int bytes) se conserva para callers sintéticos y no inventa tiempo de I/O.

Se eliminó FanoutWaitTime/RecordFanoutWait: el acumulador estaba expuesto pero nunca tuvo call-site, por lo que reportaba cero con semántica engañosa. No reintroducirlo sin definir primero qué espera concreta representa y dónde comienza/termina.

### Regla de decisión después del baseline

No optimizar por intuición. Ejecutar primero la matriz física del HEAD y comparar las fases anteriores. Hacer un cambio dirigido al cuello medido, repetir exactamente la misma corrida y registrar antes -> después -> delta. Si no hay mejora material o aparece regresión, retirar el cambio. En particular, no introducir prefetch/profundidad concurrente del origen antes de demostrar que la lectura serial actual es el límite medido.
