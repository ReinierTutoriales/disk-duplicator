# Disk Duplicator

Copia bloque a bloque `\\.\PhysicalDriveN` de **un origen a N destinos** en Windows.
Cada destino corre en su propio hilo: un USB lento no frena a otro rápido.

## Reglas (no negociables)

- Requiere administrador (manifiesto UAC).
- No escribe el disco de sistema.
- Destino debe ser **mayor o igual** que el origen.
- Si un volumen del destino no se puede **bloquear y desmontar**, ese destino **no se toca**.
- Los handles de lock se mantienen abiertos hasta terminar (o fallar).
- No degrada permisos en silencio a la hora de escribir.
- No marca éxito si no se escribieron todos los bytes del origen.
- Verificación CRC32 del destino (recomendada; duplica el tiempo).

## Uso

1. Actions → artifact `disk-duplicator.exe`.
2. Ejecuta; acepta UAC.
3. Cierra Explorer en los destinos.
4. Origen = radio. Destinos = checks.
5. Confirma la lista. Iniciar **borra todo** en los destinos.

## Compilar

```bash
cargo build --release
```
