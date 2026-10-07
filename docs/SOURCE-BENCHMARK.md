# Medir lecturas independientes sin copiar

Base: motor de lectores independientes integrado en `main` por PR #17. La casilla sigue desactivada por defecto. Este banco solo lee el archivo indicado y crea el informe JSON mediante `CreateNew`; nunca sobrescribe un informe anterior ni escribe en destinos.

```powershell
./RepartoCopier.WinUI.exe --source-bench 'H:\ISOS\archivo.iso' 'C:\Temp\source-bench.json' --readers 1,2,4 --seconds 20
```

Cada lector abre el mismo archivo con la ruta Direct I/O del motor cuando el volumen permite la alineación; si no, usa el mismo modo buffered secuencial. Lee bloques de 8 MiB, hashea con BLAKE3 en serie con su lectura y repite el archivo completo hasta cubrir el tiempo solicitado. No interviene un escritor ni VERIFY. El último pase puede prolongar el tiempo real más allá de `--seconds`. El resultado indica bytes Direct por lector para distinguir la ruta efectivamente usada, tiempos de lectura y hash, velocidad por lector y agregada, CPU del proceso normalizada entre todos los procesadores lógicos y SHA de compilación. No se infiere de ahí el rendimiento de COPY.

Para una referencia externa, `tools/bench-diskspd.ps1` usa exclusivamente archivos normales. Ejemplo de lectura (no modifica el ISO):

```powershell
./tools/bench-diskspd.ps1 -Drive H -Mode Read -ReadFile 'H:\ISOS\archivo.iso' -ReadThreads 1,2,4 -Seconds 20 -RestSeconds 90
```

En Read, los hilos arrancan en el mismo desplazamiento, como los lectores independientes. `-t4 -o1` representa cuatro solicitudes potenciales en vuelo, una por hilo. DiskSpd no hace BLAKE3 ni usa el pool del copiador. En Write, hay que indicar una letra de destino y el script crea un archivo propio en una carpeta nueva con GUID. Esa carpeta se elimina al acabar; no acepta un destino de disco físico ni un ISO como objetivo de escritura. `-Su` elimina caché de software sin agregar write-through. Con `-Curve` se guardan XML con intervalos de un segundo; sin él se guardan informes de texto y un CSV con las medianas que pudieron extraerse. La ejecución de DiskSpd y la curva física quedan pendientes en Windows.

Comparación física: usar el mismo SHA, los cinco ISO y carpetas de prueba vacías. Hacer una corrida con lectores independientes desactivados y otra con ellos activados, primero sin VERIFY y luego con VERIFY como medición separada. Guardar los dos diagnósticos y comparar `CopyFinishedAt`, `SourceReadBytes`, `SourceHashBytes`, `DirectSourceReadBytes`, errores y tiempos por destino. Los datos de 11,64 GiB no fijan la velocidad de los SSD con 28,24 GiB; 14–26 s para G/I son escenarios, no un criterio de aceptación.

Riesgo pendiente del prototipo: cada escritor puede confirmar el archivo antes de que el trabajo compare los hashes finales de sus lectores. Una diferencia detiene el trabajo, pero no revierte un archivo ya confirmado. Hacer estas pruebas iniciales con datos y destinos prescindibles. VERIFY sigue sincronizado entre destinos; este banco no lo cambia.
