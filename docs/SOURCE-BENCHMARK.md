# Medir lecturas independientes sin copiar

Base: motor de lectores independientes integrado en `main` por PR #17; activado por defecto cuando el origen y los destinos son elegibles (ver README). Este banco solo lee el archivo indicado y crea el informe JSON mediante `CreateNew`; nunca sobrescribe un informe anterior ni escribe en destinos.

```powershell
./RepartoCopier.WinUI.exe --source-bench 'H:\ISOS\archivo.iso' 'C:\Temp\source-bench.json' --readers 1,2,4 --seconds 20
```

Cada lector abre el mismo archivo con la ruta Direct I/O del motor cuando el volumen permite la alineación; si no, usa el mismo modo buffered secuencial. Lee bloques de 8 MiB, hashea con BLAKE3 en serie con su lectura y repite el archivo completo hasta cubrir el tiempo solicitado. No interviene un escritor ni VERIFY. El último pase puede prolongar el tiempo real más allá de `--seconds`. El resultado indica bytes Direct por lector para distinguir la ruta efectivamente usada, tiempos de lectura y hash, velocidad por lector y agregada, CPU del proceso normalizada entre todos los procesadores lógicos y SHA de compilación. No se infiere de ahí el rendimiento de COPY.

Para una referencia externa, `tools/bench-diskspd.ps1` usa exclusivamente archivos normales. Ejemplo de lectura (no modifica el ISO):

```powershell
./tools/bench-diskspd.ps1 -Drive H -Mode Read -ReadFile 'H:\ISOS\archivo.iso' -ReadThreads 1,2,4 -Seconds 20 -RestSeconds 90
```

En Read, los hilos arrancan en el mismo desplazamiento, como los lectores independientes. `-t4 -o1` representa cuatro solicitudes potenciales en vuelo, una por hilo. DiskSpd no hace BLAKE3 ni usa el pool del copiador. En Write, hay que indicar una letra de destino y el script crea un archivo propio en una carpeta nueva con GUID. Esa carpeta se elimina al acabar; no acepta un destino de disco físico ni un ISO como objetivo de escritura. `-Su` elimina caché de software sin agregar write-through. Con `-Curve` se guardan XML con intervalos de un segundo; sin él se guardan informes de texto y un CSV con las medianas que pudieron extraerse. La ejecución de DiskSpd y la curva física quedan pendientes en Windows.

Comparación física: la aplicación ya no permite desactivar los lectores independientes. Para comparar con la lectura compartida, usar una compilación anterior a este cambio (o `CopyOptions.IndependentSourceReads = false` desde código) con el mismo conjunto de archivos y carpetas de prueba vacías, primero sin VERIFY y luego con VERIFY como medición separada. `IndependentSourceReads` en el diagnóstico indica el modo realmente usado. Guardar los dos diagnósticos y comparar `CopyFinishedAt`, `SourceReadBytes`, `SourceHashBytes`, `DirectSourceReadBytes`, errores y tiempos por destino. Los datos de 11,64 GiB no fijan la velocidad de los SSD con 28,24 GiB; 14–26 s para G/I son escenarios, no un criterio de aceptación.

Coherencia entre lectores: cada lector registra el hash BLAKE3 de cada archivo en un registro compartido antes de enviar el cierre a su destino. El primero fija la referencia; los demás la comparan y, si difiere, la copia se detiene antes de que ese destino confirme el archivo. El primer destino puede haber confirmado ya una versión coherente del origen anterior al cambio, igual que en la lectura compartida. VERIFY sigue sincronizado entre destinos; este banco no lo cambia.
