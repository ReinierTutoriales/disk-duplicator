# Disk Duplicator

Copia bloque a bloque `\\.\PhysicalDriveN` de **un origen a N destinos** en Windows.
Cada destino corre en su propio hilo: un USB lento no frena a otro rápido.

## Integridad

Mientras escribe calcula **BLAKE3** del origen (sin I/O extra). Al terminar muestra el hash y se puede copiar.
Si “Releer y comparar hash” está activo, relee el destino y lo compara. El hex sirve con `b3sum` en otra máquina.

## Reglas

- Administrador (manifiesto UAC).
- No escribe el disco de sistema.
- Destino >= origen.
- Si un volumen del destino no se bloquea y desmonta, **ese destino no se toca**.
- Los handles de lock se mantienen abiertos hasta terminar.
- Buffer alineado a 4096 + `FILE_FLAG_NO_BUFFERING`.
- Escritura parcial = error (no reintenta desalineado).

## Uso

1. Actions → artifact `disk-duplicator.exe`.
2. Ejecuta; acepta UAC.
3. Cierra Explorer en los destinos.
4. Origen = radio. Destinos = checks.
5. Confirma. Iniciar **borra** los destinos.
