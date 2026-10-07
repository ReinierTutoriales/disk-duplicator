# Changelog

## Unreleased — lectura independiente por defecto y pulido de la interfaz

- Motor: la lectura independiente por destino pasa a ser el modo predeterminado para cualquier origen (HDD, SSD, NVMe, USB, red) con 2–16 destinos; con 1 o más de 16 se usa la lectura compartida en lugar de fallar. Los SSD NVMe/SATA locales se leen en paralelo; el resto de orígenes, por turnos de un bloque de 8 MiB.
- Motor: los lectores acuerdan el hash BLAKE3 de cada archivo antes de enviar el cierre a su destino; un origen que cambia entre lecturas detiene la copia antes de que un segundo destino confirme contenido distinto. Se elimina el riesgo documentado del prototipo.
- Motor: la comprobación final del árbol de origen se ejecuta una vez en lugar de una por lector. Un archivo existente que cambia tras la comprobación rápida ahora falla solo ese destino.
- Diagnóstico (esquema 5): `IndependentSourceReads` registra el modo realmente usado.
- UI: la omisión rápida por tamaño y fecha es la política fija para archivos existentes; se eliminan el selector, el diálogo de conflictos y la fase de comparación de contenido de la interfaz. La lectura independiente es fija (sin casilla).
- UI: iconos de estado Segoe Fluent rellenos (completado, error, aviso) en capas con pinceles de tema, que siguen el tema claro/oscuro sin código; tarjetas por destino con enlaces compilados `x:Bind` que solo notifican cambios reales; una sola pasada por las instantáneas en cada refresco de 250 ms.
- UI: diseño adaptable (márgenes 12/16, ancho máximo legible, botones solo con icono en ventanas estrechas), menú nativo con atajos Ctrl+O/Ctrl+S, Ctrl+Enter para iniciar, iconos Segoe Fluent con color de estado, tarjetas por destino con barra de progreso propia.
- UI: los diálogos (Ajustes, Acerca de, Apagar, Resultados) pasan de TaskDialog Win32, que no tiene modo oscuro, a ventanas WinUI propias: separadas de la app, modales y con propietario, ajustadas al contenido y con el tema claro/oscuro de la app, estilo de diálogo de Windows 11, botón principal en acento y Esc para cerrar. Resultados muestra todos los destinos en una lista desplazable. La comprobación de layout de la CI abre cada diálogo en ambos temas.
- Limpieza: se eliminan código y parámetros sin uso.

## Unreleased — independent source reads prototype

- Motor: modo de prueba explícito de lectura y pool independientes por destino para origen NVMe/SATA sólido, de 2 a 16 destinos. Presupuesto conjunto máximo de 256 MiB; la ruta compartida continúa por defecto. La telemetría JSON distingue modos y contabiliza las relecturas físicas; VERIFY continúa conjunto.

## Unreleased — main

- UI: Ajustes, Acerca de, confirmación de apagado y resultados dejan de usar ContentDialog dentro de la ventana compacta. Comparten TaskDialogIndirect con conflictos, propietario explícito, cierre/cancelación seguros y marshalling de callbacks protegido. Acerca de conserva enlaces; Ajustes conserva tema y persistencia; resultados recorren todos los destinos en páginas de tres y exportan el JSON completo. Apagado exige elegir Apagar y conserva No apagar por defecto.
- Ventana principal: tamaño inicial en área cliente (720×320 DIP), mínimo de área cliente (540×320 DIP) limitado al área de trabajo del monitor y actualizado al mover/cambiar DPI. El contenido deja de tener un máximo fijo de 720; las métricas y el detalle pueden envolver texto. No se fija un máximo que impida ampliar la ventana.
- CI: prueba real del EXE con --layout-check (preparación/copia a tres tamaños, temas claro/oscuro, alcance de botones y ajuste de etiquetas, propiedad/modalidad y límites de diálogos nativos y paginación). Informe adjunto en Actions. Se valida el DPI real del runner; las conversiones 100/125/150/200 % se prueban aparte. No sustituye inspección física de contraste, píxeles ni monitores mixtos. Sin cambios al motor de copia.

- Archivos existentes: nueva elección explícita «Omitir por tamaño y fecha (rápido)». Consulta tamaño y fecha UTC exacta, sin leer payload; omite coincidencias y autoriza reemplazar los demás existentes. No acredita igualdad de contenido y conserva la opción estricta. Se revalidan las coincidencias antes de omitirlas y se mantiene el rechazo de archivos aparecidos después de la preparación. El diálogo conserva Cancelar por defecto y no guarda autorizaciones en perfiles.
- UI: «Apagar al terminar» recibe una columna Auto con su ancho de contenido; la columna de velocidad ocupa el espacio restante. Evita que el texto del checkbox se recorte cuando crecen los contadores.

- Comparación de existentes: se sustituye el hash completo previo por comparación exacta de bytes por bloques, con una sola lectura del origen por bloque, salida temprana de destinos distintos y workspace fijo de 8 MiB. Los idénticos se comparan completos. Progreso y lectura de comparación separados de COPY; pausa y cancelación se atienden durante las lecturas. El diálogo de conflictos pasa a TaskDialogIndirect Win32 con ventana propia, propietario explícito y Cancelar por defecto.

- Archivos existentes: se elimina la casilla «Omitir iguales» y los booleanos `SkipSame`/`SkipExisting`. El plan lleva una única política opcional (`ExistingFilePolicy`: conservar, reemplazar distintos o reemplazar todos); un valor desconocido se rechaza antes de tocar nada. Sin política y con archivos existentes, el preflight aborta antes de modificar nada (`ExistingFilesConflictException`) y la aplicación pregunta. Si la recuperación restaura un archivo (backup de un reemplazo interrumpido), se vuelve a comprobar tras restaurarlo: con política se aplica a ese archivo y sin política se pregunta. El preflight solo comprueba existencia, sin hashear; los checkpoints del journal ya no deciden qué se omite, así que la comparación de contenido (tamaño y BLAKE3, sin exigir la misma fecha) ocurre una sola vez, en la fase visible «Comprobando archivos existentes…». El commit solo reemplaza archivos que existían al preparar la copia (`AtomicFileCommit.Commit` exige `allowReplace`). Los perfiles pasan a la versión 2 y dejan de guardar `skipExisting`; los de versión 1 se migran y nunca autorizan reemplazos. Sustituye la parte de «Omitir iguales» de la entrada «Preparación de copia nueva».

- Preparación de copia nueva: «Omitir iguales» desactivado evita el hash completo del origen y de cada destino anterior en recovery, y deja de omitir archivos por checkpoints antiguos. La elección efectiva de CopyOptions se respeta también si difiere del plan. Conserva leases, limpieza de temporales y restauración de backups; reinicia el journal para que los checkpoints correspondan a los commits de la nueva corrida. Con SkipSame activo se conserva la validación de contenido.

- Verificación final opcional desde WinUI, desactivada inicialmente para medir y usar COPY sin la relectura final. Se conserva la verificación completa al seleccionarla y la elección en perfiles. Los indicadores distinguen Copiado de Verificado; el diagnóstico SchemaVersion 3 añade VerificationRequested, sin alterar los contadores reales. No cambia el motor de escritura, pool, hash del origen, flush, commit ni recovery.

- Diagnóstico JSON `SchemaVersion` 2, solo instrumentación: identificación del disco físico por destino (`DeviceId`, `PhysicalDeviceNumber`, bus, tipo de medio, extraíble, disco compartido), `WriteTime`, `DurableFlushes` y `DurableFlushTime` acumulados por destino, marcas de fase (`PhaseMarks`), `CopyFinishedAt`/`VerifyFinishedAt`, duraciones y `Outcome`. Los destinos cancelados o fallidos conservan su estado y nunca reciben marca de finalización. `MeasurementNotes` documenta qué tiempos incluyen esperas y cuáles son acumulados entre destinos.
- El SHA del commit se incorpora al compilar (`BuildRevision`, desde `SourceRevisionId`; la CI pasa el SHA del commit descargado (merge provisional en PR, commit de main en push) y los builds locales lo leen de git). `ApplicationVersion` se conserva.
- Sin cambios en QD, bloques, pool, lectura, escritura ni planificación de VERIFY.

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
