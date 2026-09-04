# Copiador 1→N

Copia una carpeta a varios destinos a la vez. Cada destino escribe a **su** tope
(USB lento no frena USB rápido). El origen se lee por destino; Windows deja
el archivo en caché, así el disco rápido no se lee N veces desde el plato.

## Por qué así (como ExtremeCopy)

Un solo lector con cola chica acaba yendo a la velocidad del destino **más lento**
cuando la RAM se llena. Aquí cada destino es una sesión independiente: 10 USB
a 150 MB/s pueden ir los 10 a 150, si el origen (SSD + caché) da ~1.5 GB/s.

## Uso

Artifact `disk-duplicator.exe`. Origen = carpeta. Destinos = otras carpetas.
No pongas un destino dentro del origen.
