# Disk Duplicator

Copiador de archivos de alto rendimiento **1 origen → N destinos** para HDD y SSD internos o externos.

Disk Duplicator no clona discos, particiones, GPT/MBR ni sistemas operativos. Su objetivo es duplicar el contenido de una carpeta hacia múltiples destinos de almacenamiento de forma concurrente y segura.

## Arquitectura

El modo principal usa un solo lector del origen y distribuye cada bloque de datos a los destinos activos mediante **fan-out**. Los buffers se comparten entre destinos y están limitados por un presupuesto global de memoria, evitando cargar archivos completos en RAM.

Cada destino mantiene su propia cola y worker. Un destino lento o bloqueado puede degradarse a un modo independiente de fallback sin obligar a los destinos rápidos a copiar permanentemente a su velocidad.

## Seguridad

- Preflight de origen y destinos antes de copiar.
- Validación de rutas peligrosas, solapamientos y enlaces simbólicos.
- Comprobación de escritura y espacio disponible por destino.
- Escritura mediante archivos `.part` y commit por rename.
- Estado persistente para reanudar trabajo ya completado.
- Opción de verificación BLAKE3.
- Pausa, cancelación y aislamiento de errores por destino.

## Interfaz

La aplicación muestra progreso global y por destino, velocidad, profundidad de cola, estado, modo de copia, reintentos y ETA mientras el trabajo está activo.

## Uso

1. Selecciona una carpeta de origen.
2. Agrega uno o más destinos HDD/SSD.
3. Configura las opciones de omisión, continuidad ante errores y verificación BLAKE3.
4. Inicia la copia y deja que el preflight valide el trabajo antes de escribir datos.

El ejecutable de Windows se genera como artifact de GitHub Actions: `disk-duplicator.exe`.

## Licencia

MIT.
