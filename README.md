# RepartoCopier

RepartoCopier es una aplicación de escritorio para Windows que copia un origen hacia múltiples destinos simultáneamente mediante FAN-OUT, preservando la estructura completa de carpetas y priorizando integridad, recuperación y comportamiento nativo del sistema operativo.

## Versión actual

**RepartoCopier v2.1.1** es la versión actual del producto C#/.NET/WinUI. La versión histórica v1.4.3 permanece publicada sin modificaciones.

## Plataforma

- C# y .NET 10
- WinUI 3 + XAML
- Windows App SDK 2.4
- Windows 10 2004 (19041) o posterior
- x64

## Arquitectura

`RepartoCopier.WinUI` contiene exclusivamente la interfaz Windows. Conflictos, Ajustes, Acerca de, confirmación de apagado y resultados usan un Task Dialog Win32 modal con ventana propia y propietario explícito, independiente de la altura del copiador. Los resultados se paginan y el JSON conserva los detalles completos. Los cuadros nativos siguen la presentación del sistema; el tema elegido en Ajustes se aplica a la ventana WinUI. La vista principal usa controles WinUI, recursos de tema/acento, escalado DPI del sistema, focus/teclado y pickers nativos. Su tamaño inicial y mínimo se calculan sobre el área cliente y se limitan al área de trabajo; permite ampliar la ventana y envolver métricas sin anchos máximos fijos.

`RepartoCopier.Core` contiene planificación, preflight, FAN-OUT, recuperación transaccional, telemetría, comparación exacta por bloques de archivos existentes, BLAKE3 para los hashes de copia y recovery y verificación post-copia mediante CRC32C/Castagnoli por bloques. Las llamadas Win32 se mantienen aisladas en las rutas que requieren semántica de almacenamiento no expuesta directamente por las APIs de alto nivel.

## Invariantes de copia

- El modo de lectura depende del origen y no es configurable. Con un SSD NVMe local y de 2 a 16 destinos, cada destino tiene su propio lector y pool: un destino lento no frena a los rápidos. Con cualquier otro origen (SSD o HDD SATA, USB, red o no identificado), un único lector compartido lee el origen una sola vez y entrega cada bloque a todos los destinos, con el bloque siguiente ya en lectura; así cada destino recibe la velocidad completa del origen en lugar de 1/N (medido: HDD USB → 3 NVMe pasa de ~35 MB/s a la velocidad del HDD por destino; un SSD SATA de ~550 MB/s daría ~180 MB/s por destino con 3 lectores). Con 1 destino o más de 16 también se usa el lector compartido.
- Si ya existen archivos en el destino, la aplicación omite los iguales por tamaño y fecha (rápido, sin leer contenido) y reemplaza los que difieran. No hay diálogo ni selector: es la política fija del producto. El motor conserva las demás políticas para uso programático y pruebas.
- Una carpeta seleccionada se replica incluyendo su carpeta raíz.
- Se conserva exactamente la estructura de directorios, incluidas carpetas vacías.
- Un archivo seleccionado copia únicamente ese archivo.
- Los archivos adicionales del destino no se eliminan.
- El estado interno vive fuera del árbol copiado en `.disk-duplicator-state` por compatibilidad con recovery existente.
- La verificación CRC32C final es opcional mediante «Verificar contenido al terminar», desactivada inicialmente. Sin ella se conservan escritura, flush y commit atómico, pero no se comprueba el contenido del destino mediante relectura. La UI distingue «Copiado» de «Verificado» y el JSON registra la opción elegida. Los perfiles conservan su elección de verificación.
- Recovery conserva sus pruebas BLAKE3. La comparación de existentes contrasta los bytes completos; una diferencia detiene la lectura de ese destino.
- Un archivo que ya existe en el destino nunca se reemplaza sin una elección explícita. Si el preflight encuentra archivos existentes y no hay política, aborta antes de tocar ningún destino y la aplicación pregunta: «Conservar existentes» (no toca nada que ya exista), «Comparar contenido» (omite los idénticos por tamaño y comparación exacta de bytes, ignorando la fecha, y reemplaza los distintos), «Reemplazar todos» (sin comparar) u «Omitir por tamaño y fecha (rápido)» (omite coincidencias de tamaño y fecha de modificación UTC y reemplaza el resto). La elección no se guarda en los perfiles (versión 2; los de versión 1 se migran y nunca autorizan reemplazos). Los checkpoints del journal ya no deciden qué se omite: la comparación de contenido ocurre una sola vez, en la fase visible. Solo se pueden reemplazar los archivos que existían al preparar la copia: uno que aparezca después no se reemplaza. La verificación final es una elección independiente.
- El modo rápido solo consulta metadatos: no detecta daños o cambios que mantengan tamaño y fecha, ni acredita contenido verificado. La fecha UTC se exige exacta, sin tolerancia: sistemas de archivos con menor precisión pueden volver a copiar archivos aunque sean idénticos. Las coincidencias se vuelven a consultar antes de omitir; si cambiaron desde la preparación, el trabajo falla sin reemplazarlas. «Comparar contenido» mantiene la comprobación completa. «Verificar contenido al terminar» sigue siendo una opción independiente que relee los archivos escritos en esta corrida; los omitidos no se incluyen en esa verificación final.
- Symlinks, junctions, reparse points y solapamientos peligrosos se rechazan de forma fail-closed.

La fase de comparación muestra avance lógico y bytes leídos por destino. Usa un workspace fijo de 8 MiB, lee el origen una vez por bloque y deja de leer una rama al encontrar la primera diferencia. Declarar idéntico un archivo requiere leerlo completo; conservar existentes no compara contenido.

## Rendimiento

La ruta FAN-OUT compartida sigue un modelo shared-buffer deliberadamente simple: una lectura física del origen entra en un pool acotado de bloques alineados y el mismo bloque, con conteo de referencias, se entrega a una cola ligera por destino. Cada writer libera su referencia al completar la escritura; cuando el pool se llena, el lector espera espacio. En ese modo no existen replay/spool, staging por rama ni copias privadas del payload. El modo independiente (predeterminado cuando es elegible) da a cada destino su propia lectura del origen y un pool de 16 a 128 MiB, con un máximo conjunto de 256 MiB; así el retraso de un USB no retiene memoria de un NVMe. Todos los lectores deben obtener el mismo hash BLAKE3 de cada archivo antes de que otro destino lo confirme; si el origen cambia entre lecturas, la copia se detiene. Leer y hashear el origen N veces multiplica la carga del origen y de la CPU: con un origen lento y destinos de velocidad parecida, la lectura compartida es más rápida. La verificación final opcional conserva su planificación conjunta.

Cuando se solicita, después de copiar la verificación relee el origen y los destinos coordinadamente bloque a bloque: una lectura outstanding por dispositivo, CRC32C inmediato, comparación y reutilización del buffer. Usa un workspace fijo de 8 MiB, repartido entre origen y destinos. El pool de copia de 256 MiB se libera antes de esta fase. Las lecturas buffered parciales se completan antes de comparar; un fallo permanente de un destino no invalida las ramas sanas.

La telemetría mantiene lectura física del origen, escrituras, recuperación de I/O, Direct I/O y tiempos de copy/verify para que el cuello de botella sea medible.

## Compilar

```powershell
dotnet restore RepartoCopier.sln
dotnet test dotnet/RepartoCopier.Core.Tests/RepartoCopier.Core.Tests.csproj -c Release
dotnet build dotnet/RepartoCopier.WinUI/RepartoCopier.WinUI.csproj -c Release -r win-x64
```

El protocolo de validación y benchmark físico se mantiene en `TESTING.md`.

La CI también ejecuta el EXE con `--layout-check <informe.json>`: comprueba layout real de preparación y ejecución a 540×320, 720×320 y 1200×720 DIP, temas claro/oscuro, etiquetas de controles, alcance mediante scroll, cuadros nativos con propietario/modalidad y paginación. Usa datos de progreso sintéticos sin copiar archivos, cambiar preferencias, abrir enlaces ni autorizar apagado. El informe registra el DPI real del runner; los otros factores de DPI tienen pruebas de conversión geométrica, no una simulación visual de monitores. Recorte intencional de nombres largos, listas horizontales, contraste y rendering de píxeles requieren comprobación física adicional.
