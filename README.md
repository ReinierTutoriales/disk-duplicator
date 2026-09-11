# RepartoCopier

RepartoCopier es una herramienta de copia 1 origen → N destinos para Windows, orientada a duplicar una carpeta completa a múltiples unidades conservando su estructura.

## Características principales

- Copia FAN-OUT: el origen se lee una vez y los bloques se comparten entre todos los destinos activos.
- Conserva la carpeta raíz seleccionada, su estructura interna y las carpetas vacías.
- Reanudación por destino con journal y manifiesto BLAKE3 fuera del árbol copiado.
- Verificación BLAKE3 y validación final de tamaño/estructura.
- Pausa y cancelación cooperativas sin espera activa.
- Los destinos fallidos se aíslan del FAN-OUT para que los demás puedan continuar.
- Recuperación de `.part`/`.bak` propios tras interrupciones.
- Preflight de espacio, rutas solapadas, enlaces simbólicos y permisos de escritura.

## Modelo de copia

RepartoCopier usa un único modo de copia: FAN-OUT. El lector produce bloques del origen y los distribuye a los workers de cada destino. Cada destino mantiene su propio estado, cola, verificación y commit.

La aplicación no copia metadata interna dentro de la carpeta seleccionada. El estado operativo vive como hermano del destino en `.disk-duplicator-state/<id>`.

## Seguridad e integridad

Antes del trabajo se escanea el origen y se valida cada destino. Durante la copia se comprueba que el snapshot del origen no cambie en tamaño/fecha, y el flujo se protege con BLAKE3. Con verificación habilitada se vuelve a comprobar físicamente el contenido antes de considerar el destino completo.

Los reemplazos se realizan mediante archivo temporal y backup: si el commit falla, se intenta restaurar el archivo original. Si también falla la restauración, el error informa explícitamente dónde permanece el backup.

La pausa se implementa mediante `Condvar`; cancelar despierta a los workers pausados. La cancelación es cooperativa: una llamada de I/O síncrona que ya esté bloqueada dentro de Windows no puede ser interrumpida por `std::fs` hasta que el sistema operativo devuelva el control.

La detección de destino atascado usa límites distintos por fase. Las escrituras disponen de margen suficiente para unidades USB/HDD lentas; `sync`, verificación y commit usan un límite más largo para evitar falsos positivos.

## Reanudación

El estado durable se mantiene en:

```text
<padre-del-destino>/.disk-duplicator-state/<id>/
├── completed.jsonl
├── manifest.b3
└── tmp/
```

El programa migra formatos anteriores de identificadores de estado y limpia/restaura temporales propios de versiones previas cuando corresponde.

Un registro de `completed.jsonl` solo se acepta como reanudable si existe prueba BLAKE3 durable y tanto origen como destino siguen coincidiendo con ella.

## Plataforma

- Windows 10/11 x64
- Rust 1.98.1 como toolchain fijado de CI
- Compatibilidad adicional comprobada con el Rust stable más reciente

## Compilación

```powershell
cargo build --locked --release
```

El ejecutable resultante queda en:

```text
target\release\RepartoCopier.exe
```

## Pruebas

```powershell
cargo test --locked --release
cargo clippy --locked --all-targets -- -D warnings
```

El workflow de GitHub Actions ejecuta Clippy, pruebas release y build release en Windows.

## Licencia

MIT
