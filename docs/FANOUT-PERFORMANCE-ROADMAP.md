# FAN-OUT: diagnóstico y plan de rendimiento

## Contrato buscado

Una sola lectura física del origen entrega cada bloque a todos los destinos. Si el origen mantiene 150 MB/s y varios discos pueden escribir a esa velocidad, cada uno debe poder avanzar a 150 MB/s aunque otro destino USB sea más lento. Esta independencia requiere almacenamiento adicional para la diferencia de velocidades: memoria finita, un spool físicamente independiente o lecturas posteriores del origen. No se puede sostener indefinidamente una tasa de lectura superior a la del destino más lento con solo un buffer finito y sin spool.

## Estado comprobado en el código actual

- `CopyEngine.ReadAndFanOutSequentialAsync` lee una vez por bloque y lo entrega a las colas de los destinos activos.
- El pool compartido está fijado en 256 MiB y los bloques tienen un máximo nominal de 8 MiB. `SharedFanoutBufferPool.Lease.ReleaseReference` libera páginas solo después de que **todos** los destinos entregados liberen su referencia.
- `DeliverDataAsync` usa canales no acotados y `ReserveBacklog`/`ReservePendingPayload` solo contabilizan bytes; `BacklogTargetBytes` no es una barrera ni activa aislamiento de ramas.
- El pool sí aplica backpressure en `RentAsync`. Con un USB sostenidamente más lento, las referencias USB terminan reteniendo el pool; entonces la siguiente lectura se detiene y el rendimiento de los demás destinos converge con el USB. El test `FastDestinationCannotFreeTheSourceWindowWhileSlowDestinationStillOwnsBlocks` reproduce exactamente esta dependencia de memoria.
- `EndMessage` se procesa después de todos los datos de cada archivo; `FinishFile` espera las escrituras pendientes antes del commit. La finalización segura de un archivo lento puede retrasar las fases siguientes de ese destino.
- `DeviceScheduler` permite **una** operación I/O por dispositivo físico y no explora queue depth. La escritura Direct usa `WriteFile` síncrono. La ruta buffered ahora abre el archivo con `FileOptions.Asynchronous` y usa `RandomAccess.WriteAsync` por offset para evitar ocupar un hilo mientras espera un disco lento; esto no elimina el acoplamiento de memoria.
- El tamaño del bloque se selecciona por archivo con `SelectSharedFanoutBlockSize`, sin adaptación por throughput o latencia. La memoria del pool y el tamaño de bloque son constantes.
- La UI muestra el progreso y la velocidad lógica de finalización a partir del **mínimo** de bytes escritos por los destinos activos (`active.Min(item => item.Written)`). Esa cifra sigue al más lento por diseño y puede ocultar que uno rápido va adelantado; no demuestra por sí sola que su escritura física se haya frenado.

La versión anterior de este documento afirmaba que había replay por rama, `PipelineGovernor`, `AdaptiveTransferSizer`, queue depth adaptativo y alineaciones superiores a 64 KiB. Esas rutas no existen en el producto actual; las pruebas de arquitectura incluso prohíben algunas de ellas. No se deben usar esas afirmaciones para evaluar el rendimiento.

## Límite matemático y prueba física

Ejemplo ilustrativo: origen y destinos rápidos a 150 MB/s, USB a 20 MB/s. El retraso crece a ~130 MB/s. Con 256 MiB compartidos, la ventana puede agotarse en unos 2 s si el resto de condiciones permiten ese ritmo. Después, un destino rápido ya no recibe bloques nuevos a 150 MB/s de forma sostenida. La duración exacta depende de tamaños de archivo, cachés, hubs y latencia; esta estimación no es un benchmark.

Comparar A) origen → NVMe solamente, B) origen → NVMe + USB lento, y C) origen → SATA + NVMe + USB. Usar un archivo mayor que RAM/caché efectiva y medir, durante COPY separadamente de VERIFY, bytes/s físicos por dispositivo, `SourceRead5sBytesPerSecond`, `BufferWaitTime`, `PeakBufferedBytes`, `DeviceSchedulers[*].QueuedBytes`, `Written` por destino y tiempo de commit. Repetir con distintos puertos/hubs. Si B llena el pool y la tasa del origen cae hacia la del USB, la hipótesis queda confirmada en ese equipo.

## Cambio estructural pendiente para aislar una rama lenta

Diseñar un spool secuencial por destino lento que preserve **una sola lectura del origen** y libere su referencia del bloque compartido solo cuando una copia íntegra, comprobada y recuperable del bloque haya sido persistida en el spool. Situarlo en un dispositivo físico probado distinto del origen, de los destinos rápidos y del lento. Si no se conoce identidad física, espacio disponible o durabilidad, mantener la ruta actual con backpressure: perder rendimiento es preferible a perder datos. Un spool en el mismo HDD, hub o volumen origen puede empeorar todos los destinos.

La cola de replay necesita orden por archivo y offset, barrera de `EndMessage`, checksum y manejo de escritura parcial, cancelación, desconexión, falta de espacio, limpieza y reinicio. Su capacidad también es finita: una copia grande a 150 MB/s con USB de 20 MB/s acumula 130 MB/s de spool. Cuando se agote, debe volver al backpressure sin corromper los bloques ni fingir velocidad sostenida.

No activar replay ni aumentar memoria/QD por intuición. Primero medir la combinación real de discos y después validar el diseño con pruebas de falla y benchmarks A/B en Windows físico. La ruta buffered asíncrona actual es una corrección de ocupación de hilos; el desacoplamiento sostenido sigue abierto.
