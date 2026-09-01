# Disk Duplicator

Duplicador **libre** de discos físicos para Windows. 1 origen → N destinos.

No es Paquetecopies. Es una reescritura: bloque raw (`\\.\PhysicalDriveN`), UI propia, MIT.

## Qué hace

- Enumera `PhysicalDrive0..N` con modelo, serial, bus, letras y disco de sistema (extents de `C:\Windows`).
- Eliges **un origen** y marcas **varios destinos**.
- Cada destino abre el origen por su cuenta y escribe a su techo (USB lento no frena USB rápido).
- Confirmación: últimas 4 del serial del origen.
- Disco de sistema no seleccionable.
- Destino más chico que el origen = bloqueo.
- Intenta lock/dismount de volúmenes del destino antes de escribir.
- Tema oscuro tipo Obsidian (referencia visual, no el `.vsf` de VCL).

## Compilar

Administrador. Windows x64.

```bash
cargo build --release
```

GitHub Actions publica `disk-duplicator.exe` en Artifacts.

## Uso

1. Ejecutar como administrador.
2. Actualizar discos.
3. Radio = origen. Checkbox = destinos.
4. Escribir serial (4 últimos).
5. Iniciar.

Un uso incorrecto destruye el destino. No hay deshacer.

## Licencia

MIT.
