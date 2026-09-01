# Disk Duplicator

Herramienta de duplicación de discos a nivel de bloque para Windows.

## Características

- Copia 1 fuente → N destinos simultáneamente
- Acceso raw a disco físico (\\.\PhysicalDriveN)
- Buffers alineados a sector para máximo rendimiento
- Verificación BLAKE3 opcional
- Protección contra selección accidental del disco de sistema

## Compilación

### Local

```bash
cargo build --release
```

El ejecutable estará en `target/release/disk-duplicator.exe`

### GitHub Actions

El proyecto incluye un workflow que compila automáticamente el ejecutable en cada push a la rama `main`.

Para descargar el ejecutable:

1. Ve a la pestaña "Actions" en GitHub
2. Selecciona el workflow más reciente
3. Descarga el artifact "disk-duplicator"

## Uso

1. Ejecuta el programa como administrador
2. Selecciona el disco fuente
3. Selecciona los discos destino (separados por comas)
4. Confirma la operación

## Requisitos

- Windows 10/11
- Permisos de administrador
- Discos de destino >= tamaño del origen

## Advertencia

Este software escribe directamente a discos físicos. Un uso incorrecto puede causar pérdida de datos. Verifica cuidadosamente antes de confirmar.
