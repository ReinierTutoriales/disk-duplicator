# Motor: auditoría conservadora de errores y latencias — 2026-10-16

Base: main `a5da5a226a94ee8e0ba44a9bce134a33dd4ceb99`. Las correcciones se aíslan de UI/dependencias del PR #26 en `fix/engine-cancellation-cleanup`. No se cambia el baseline de bloques, pools, QD, prefetch, modos de lectura, prioridades, flush/commit, checksum ni formato persistido de recovery.

## Fallos reproducidos y corregidos

### Cancelación buffered convertida en fallo de disco

`CopyEngine.WriteBlockAtOffsetAsync` capturaba OperationCanceledException en el catch general y la envolvía en IOException. `CancellationAsIs` solo reconoce la excepción de cancelación directa; por tanto la cancelación podía recorrer la ruta de fallo de archivo/destino. La espera de reintento ya preservaba la cancelación, pero la entrada a la escritura y la operación buffered no.

Se añade un catch específico condicionado a que el token del job esté cancelado, liberando exactamente una vez el bloque de la rama y devolviendo la excepción original. No se ocultan errores reales de disco ni se alteran los reintentos.

Pruebas de regresión en la ruta productiva mediante el fixture existente: cancelación antes de escribir y cancelación al obtener el handle buffered. Ambas fallaron antes (se recibió IOException) y pasaron después. El caso existente de cancelación durante retry también pasa. Se comprueban pool, backlog físico y payload pendientes en cero, número de intentos y ausencia de bytes escritos.

### Reserva de control después de recibir un token ya cancelado

`AdaptiveControlByteBudget.AcquireAsync` comprobaba cancelación en su espera, pero no en el fast path. Con capacidad libre aceptaba la reserva incluso si el token ya estaba cancelado.

Se comprueba el token antes de consultar capacidad o reservar. La prueba falló antes porque no se lanzaba la excepción; con el cambio comprueba además cero consultas de capacidad y cero bytes usados/pico. No se cambia el proveedor ni el límite de capacidad. Esto evita trabajo de admisión después de una cancelación ya conocida; no se presenta como una mejora medida de throughput.

### Error al cerrar recovery podía impedir el cierre del otro stream

`RecoveryCheckpointWriter.Dispose` toleraba errores I/O al hacer FlushCheckpoint, pero no al cerrar los streams, donde también puede vaciarse un buffer. Un error en Dispose del journal impedía llegar al cierre del manifest y podía escapar de la limpieza del writer. Un error del manifest impedía marcar el objeto como dispuesto.

Ahora se marca la disposición y se intenta cerrar cada stream de forma independiente, tolerando únicamente IOException/UnauthorizedAccessException igual que el checkpoint best-effort existente. Los errores ajenos a I/O siguen propagándose y se intenta cerrar el segundo stream incluso si el primero produce uno de ellos. No se cambia el orden manifest-flush/journal-flush ni el commit durable de datos.

Dos casos de regresión inyectan fallo de cierre en journal o manifest: ambos fallaron antes con IOException y pasaron después. Comprueban los handles cerrados y la segunda llamada a Dispose. Es una inyección controlada de fallo, no una desconexión física de USB.

## Rutas revisadas y observaciones que se conservan

Revisión dirigida de productor compartido/independiente, read-ahead y liberación de referencias, mensajes Begin/Data/End/Abort, ventanas de escritura y fallback, scheduler por dispositivo, presupuesto de control, comparación de contenido, verificación coordinada y lecturas cortas, clasificación de errores transitorios, commit/backup, checkpoints y leases. No equivale a una prueba exhaustiva de todos los interleavings ni una certificación de hardware.

- Se conserva la barrera de escrituras pendientes antes de flush/commit y antes del fallback Direct→buffered. El pool se libera después de drenar consumidores.
- Los datos se verifican fuera de la fase de copia cuando se solicita; no se elimina checksum ni se cambian las políticas de omitidos.
- El scheduler físico continúa con una I/O; no se reintroduce QD concurrente ni cursor de archivo compartido bajo escrituras concurrentes.
- El pool compartido sigue acoplado al consumidor lento cuando retiene referencias; la ruta independiente elegible limita el conjunto de pools a 256 MiB. Medir el modo efectivo y la topología antes de intentar desacoplar ramas.

## Latencias y riesgos que requieren otra validación

| Ruta | Estado observado | Decisión |
| --- | --- | --- |
| Commit con sharing violation | Reintenta cada 250 ms hasta unas 20 esperas; usa Thread.Sleep y no recibe token. El scheduler físico se mantiene durante FinishFile. | Puede retrasar cancelación y otras operaciones del mismo disco. No se altera aquí: hay una prueba que necesita tolerar un bloqueo breve de scanner. Hacer cancelable el commit requiere preservar restauración/backup y diferenciar antes/después de la operación atómica. |
| Reintentos de bloques | Ventana de 90 s para errores clasificados transitorios y espera async creciente hasta 250 ms. | No recortar por intuición: podría transformar resets recuperables en fallos. La ventana limita reintentos, no el tiempo de una syscall bloqueada. |
| Watchdog Direct | Inspección cada 5 s, umbral de 120 s, CancelSynchronousIo sobre la escritura/flush tracked. | No cubre todo bloqueo posible: otras rutas buffered, source/verify, metadata, SetLength o commit pueden depender del SO/controlador. Probar discos/cajas reales que dejan de responder; no prometer cancelación instantánea. |
| ThreadPool | Se eleva el mínimo en función del número de destinos para alojar I/O Direct síncrona. | Con muchos destinos medir hilos, context switches, CPU y latencias antes de cambiarlo. No se modifica la política estabilizada. |
| Comparación exacta de existentes | La política programática ReplaceDifferent abre/compara candidatos antes de crear workers; errores pueden abortar la preparación común. | No se redefine el aislamiento de esta política sin pruebas específicas. La UI de main usa ReplaceMetadataDifferent. |
| Recovery | Manifest se hace durable antes que journal; su limpieza tolera fallos I/O. | El éxito de cierre no garantiza persistencia después de perder físicamente el dispositivo. Mantener la validación física BLAKE3 al recuperar. |

No se ejecutaron benchmarks físicos USB/SATA/NVMe, desconexiones reales, trazas ETW ni percentiles por operación. No se afirma una ganancia medida de MB/s, FPS ni milisegundos de cancelación. Se prioriza corregir fallos demostrados con una modificación pequeña.

## Validación ejecutada

- Antes del fix, dos casos buffered fallaron por recibir IOException en vez de OperationCanceledException; el caso de token ya cancelado en el presupuesto falló por no lanzar excepción; los dos casos de cierre de recovery fallaron con IOException. Después, las nueve pruebas enfocadas pasaron.
- Tras aislar la rama desde main se restauró la solución con sus propias versiones de dependencias; no se arrastraron overrides ni UI del PR #26.
- Suite completa Release de esta rama: 286 correctos, 1 omitido, 0 fallos, 287 en total, 41 s. Son cinco casos nuevos sobre la base de main (281 correctos y 1 omitido). El omitido exige nombres a.txt/A.txt simultáneos que el filesystem utilizado no permite.
- La ejecución de tests mostró un MSB3026 transitorio de copia del assembly durante los builds paralelos; el reintento tuvo éxito y la suite completó. No se cuenta como una ejecución sin advertencias del build de tests.
- Build WinUI Release win-x64: 0 errores y 0 advertencias. git diff --check correcto.
- Informe local: .audit-artifacts/engine-main-final.trx. Pendiente CI del nuevo PR y las pruebas físicas descritas arriba. El riesgo de regresión no se declara nulo por pasar tests.
