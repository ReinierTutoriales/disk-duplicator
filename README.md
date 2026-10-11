# RepartoCopier

RepartoCopier es una aplicación de escritorio para Windows que copia un origen hacia múltiples destinos simultáneamente mediante FAN-OUT, preservando la estructura completa de carpetas y priorizando integridad, recuperación y comportamiento nativo del sistema operativo.

## Versión actual

**RepartoCopier v2.2.3** es la versión actual del producto C#/.NET/WinUI. La versión histórica v1.4.3 permanece publicada sin modificaciones.

Se distribuye como un único ejecutable autocontenido (`RepartoCopier.WinUI.exe`, sin instalación ni archivos al lado) dentro de `RepartoCopier-vX.Y.Z-win-x64.zip`, con `SHA256SUMS.txt` y atestación de procedencia. La primera vez que se abre cada versión, .NET la extrae en `%TEMP%\.net`; al arrancar, la app borra las extracciones de versiones anteriores que no estén en uso.

## Plataforma

- C# y .NET 10
- WinUI 3 + XAML
- Windows App SDK 2.5
- Windows 10 2004 (19041) o posterior
- x64

## Arquitectura

`RepartoCopier.WinUI` contiene exclusivamente la interfaz Windows. Ajustes, Acerca de, apagado al terminar (cuenta atrás cancelable de 60 s) y resultados son ventanas WinUI propias: modales, con propietario explícito, fuera de la ventana principal y con el tema claro/oscuro de la app, que siguen aunque el tema cambie con el diálogo abierto. Los resultados muestran todos los destinos en una lista desplazable y el JSON conserva los detalles completos. La vista principal usa controles WinUI, recursos de tema/acento, escalado DPI del sistema, focus/teclado, pickers nativos y arrastrar y soltar desde el Explorador. Su tamaño inicial y mínimo se calculan sobre el área cliente y se limitan al área de trabajo.

Integración con Windows: progreso en el botón de la barra de tareas, notificación al terminar si la ventana no está en primer plano, nombres para Narrador y, mientras hay una copia en curso, una solicitud de energía que impide la suspensión por inactividad (visible en `powercfg /requests`; la pantalla puede apagarse) y la limitación de velocidad del proceso al minimizarlo.

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
- Solo se pueden reemplazar los archivos que existían al preparar la copia: uno que aparezca después no se reemplaza. El reemplazo es atómico (temporal `.part` y `ReplaceFile`).
- La omisión por tamaño y fecha solo consulta metadatos: no detecta daños o cambios que mantengan tamaño y fecha, ni acredita contenido verificado. La fecha se compara con la precisión del sistema de archivos del destino: 2 s en FAT, 10 ms en exFAT y exacta en NTFS/ReFS (como robocopy `/FFT`). Las coincidencias se vuelven a consultar antes de omitirlas; si cambiaron desde la preparación, ese archivo es un error. «Verificar contenido al terminar» relee los archivos escritos en esta corrida; los omitidos no se incluyen en esa verificación final.
- Se rechazan de forma fail-closed los enlaces (symlinks, junctions, puntos de montaje, alias de aplicación) y los solapamientos peligrosos. Los archivos de OneDrive, los comprimidos con CompactOS y los deduplicados se copian con normalidad.
- Un destino FAT32 rechaza antes de empezar los archivos de 4 GiB o más. En carpetas destino cifradas (EFS) o comprimidas (NTFS) los archivos copiados heredan esa propiedad.
- Los archivos que otro programa mantiene abiertos se pueden leer; si cambian durante la copia, el hash BLAKE3 y la comprobación del árbol de origen lo detectan y la copia se detiene.

El motor conserva para uso programático y pruebas las demás políticas de archivos existentes (conservar, comparar contenido por bloques, reemplazar todos), sin uso desde la aplicación.

## Fallos de dispositivo

- Un destino que falla o se desconecta se detiene solo; los demás terminan, también con verificación final.
- Los errores transitorios (disco ocupado, reinicio breve del puente USB) se reintentan como máximo 90 s por bloque; después falla ese destino.
- Una escritura o un vaciado que no termina en 120 s marca su destino como fallido y se cancela; los demás siguen. Cerrar la ventana dos veces mientras se cierra fuerza la salida (el estado es seguro ante cortes).
- Con «Continuar si falla un archivo», un archivo de origen ilegible o bloqueado es un error de ese archivo; un destino desconectado se informa una vez como no disponible.

## Rendimiento

La ruta FAN-OUT compartida sigue un modelo shared-buffer deliberadamente simple: una lectura física del origen (con el bloque siguiente ya en lectura mientras se calcula el hash del actual) entra en un pool acotado de bloques alineados y el mismo bloque, con conteo de referencias, se entrega a una cola ligera por destino. Cada writer libera su referencia al completar la escritura; cuando el pool se llena, el lector espera espacio. En ese modo no existen replay/spool, staging por rama ni copias privadas del payload. El modo independiente (predeterminado cuando es elegible) da a cada destino su propia lectura del origen y un pool de 16 a 128 MiB, con un máximo conjunto de 256 MiB; así el retraso de un USB no retiene memoria de un NVMe. Todos los lectores deben obtener el mismo hash BLAKE3 de cada archivo antes de que otro destino lo confirme; si el origen cambia entre lecturas, la copia se detiene. Leer y hashear el origen N veces multiplica la carga del origen y de la CPU: con un origen lento y destinos de velocidad parecida, la lectura compartida es más rápida. La verificación final opcional conserva su planificación conjunta.

Cuando se solicita, después de copiar la verificación relee el origen y los destinos coordinadamente bloque a bloque: una lectura outstanding por dispositivo, CRC32C inmediato, comparación y reutilización del buffer. Usa un workspace fijo de 8 MiB, repartido entre origen y destinos. El pool de copia de 256 MiB se libera antes de esta fase. Las lecturas buffered parciales se completan antes de comparar; un fallo permanente de un destino no invalida las ramas sanas.

La telemetría mantiene lectura física del origen, escrituras, recuperación de I/O, Direct I/O y tiempos de copy/verify para que el cuello de botella sea medible.

## Compilar

```powershell
dotnet restore RepartoCopier.sln
dotnet test dotnet/RepartoCopier.Core.Tests/RepartoCopier.Core.Tests.csproj -c Release
dotnet build dotnet/RepartoCopier.WinUI/RepartoCopier.WinUI.csproj -c Release -r win-x64
```

El protocolo de validación y benchmark físico se mantiene en `TESTING.md`.

La CI también ejecuta el EXE con `--layout-check <informe.json>`: comprueba layout real de preparación y ejecución a 540×320, 720×320 y 1200×720 DIP, temas claro/oscuro, etiquetas de controles, alcance mediante scroll y los diálogos (propietario, modalidad, área de trabajo y textos sin recortar) en ambos temas. Usa datos de progreso sintéticos sin copiar archivos, cambiar preferencias, abrir enlaces ni autorizar apagado. El informe registra el DPI real del runner; los otros factores de DPI tienen pruebas de conversión geométrica, no una simulación visual de monitores. Recorte intencional de nombres largos, listas horizontales, contraste y rendering de píxeles requieren comprobación física adicional.
