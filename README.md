# RepartoCopier

RepartoCopier es un copiador de carpetas de alto rendimiento para Windows que distribuye **1 origen → N destinos** mediante FAN-OUT estricto.

No clona discos, particiones, GPT/MBR ni dispositivos a nivel de bloque. Copia el contenido de una carpeta conservando su jerarquía y sus directorios vacíos.

## Arquitectura

El motor usa un único lector del origen. Cada bloque leído se comparte con todos los destinos activos mediante referencias al mismo buffer, evitando releer el origen una vez por destino durante la fase de copia.

- Bloques de **16 MiB** para reducir el número de operaciones de I/O.
- Cola acotada por destino, con capacidad máxima de **16 elementos**.
- Backlog pendiente acotado para evitar que un destino lento monopolice memoria.
- Pool reutilizable de buffers con un máximo de **16 buffers libres retenidos**.
- Presupuesto máximo de buffers en vuelo de **2 GiB**; no significa que 2 GiB sea un requisito mínimo de RAM del sistema.
- Cada destino tiene su propio worker y puede fallar sin detener automáticamente a los destinos sanos cuando está habilitado "Continuar con errores".

El preflight escanea el origen una sola vez para construir la lista de archivos y directorios. Esa misma planificación se reutiliza para crear la estructura de carpetas en los destinos, evitando un segundo recorrido completo del origen antes de copiar.

Las rutas de origen y destinos se canonicalizan antes de iniciar el trabajo para detectar alias, duplicados y solapamientos peligrosos.

## Integridad y verificación

### FAN-OUT estricto

No existe un modo alternativo que vuelva a leer el origen por cada destino. El lector calcula BLAKE3 mientras transmite los datos y guarda los hashes de la corrida para reutilizarlos durante la validación final.

Con **Verificar BLAKE3** activado:

1. El lector calcula el hash del archivo mientras lo lee.
2. Cada worker escribe primero un archivo temporal `.part` dentro del área de estado externa.
3. El worker puede releer el `.part` para verificarlo antes del commit.
4. Tras el commit, la validación final vuelve a leer el destino y lo compara con el hash ya calculado por el lector.
5. Para archivos reanudados de una corrida anterior, `manifest.b3` sirve como referencia persistente; si no existe un hash utilizable se recurre a una comparación segura con el origen.

Esto elimina relecturas redundantes del origen para los archivos recién leídos en la corrida actual sin eliminar la comprobación física del destino.

## Reanudación

El estado operacional no se escribe dentro del árbol copiado. Se almacena junto al destino bajo:

```text
.disk-duplicator-state/<id-del-destino>/
```

Los archivos principales son:

- `completed.jsonl`: journal de archivos confirmados.
- `manifest.b3`: hashes BLAKE3 persistidos.
- `tmp/`: temporales `.part` y backups controlados por la aplicación.

La reanudación es **por archivo comprometido**, no por byte dentro de un archivo. Si el proceso se interrumpe en mitad de un archivo, ese archivo incompleto puede tener que escribirse de nuevo; los archivos ya confirmados pueden conservarse si pasan las validaciones de reanudación.

El preflight normaliza el journal y descarta entradas que ya no corresponden a archivos físicamente válidos.

## Escritura y reemplazo seguro

Para cada archivo:

- se escribe primero en un `.part` externo al árbol copiado;
- se sincronizan los datos antes del commit;
- si ya existe un archivo destino, se crea un backup temporal;
- el `.part` se renombra a la ruta final;
- si el reemplazo falla, se intenta restaurar el backup y se reporta el error compuesto si también falla la restauración;
- el journal y el manifest se actualizan únicamente después de completar las comprobaciones correspondientes.

No se describe este proceso como una transacción de filesystem garantizada: es un protocolo de **backup + rename + restauración en fallo** diseñado para minimizar estados incompletos.

## Detección de destinos atascados

La detección de stall es consciente de la fase de I/O:

- **Write:** umbral corto de aproximadamente **6 s** sin progreso.
- **Sync / Verify / Commit:** umbral más tolerante de aproximadamente **60 s** para evitar falsos positivos durante operaciones legítimamente lentas.

Cuando un destino supera el umbral correspondiente se marca como fallido y deja de bloquear el flujo de los demás destinos. Un hilo que ya esté bloqueado dentro de una llamada de I/O del sistema operativo no puede ser terminado de forma segura por Rust; por eso el motor también aplica guardas para impedir que un worker declarado muerto registre posteriormente un archivo como completado.

## Rendimiento y telemetría

Las optimizaciones documentadas describen mecanismos implementados, no porcentajes de rendimiento no medidos:

- bloques de 16 MiB;
- buffers compartidos entre destinos;
- pool de buffers reutilizable y acotado;
- colas y backlog acotados;
- un snapshot de progreso por frame de UI;
- buffer de verificación de **8 MiB** creado únicamente cuando BLAKE3 está activado;
- despacho FAN-OUT con colas independientes;
- velocidad reciente mediante **EWMA con constante temporal de 2 s**;
- velocidad promedio acumulada conservada para métricas de largo plazo/ETA.

La velocidad se presenta con unidades IEC: **KiB/s, MiB/s y GiB/s**.

El throughput real depende del origen, cada destino, controladores USB/SATA/NVMe, cachés del sistema operativo y del patrón de archivos. No se publican cifras de rendimiento como garantía sin un benchmark reproducible.

## Seguridad del preflight

Antes de escribir datos se valida:

- que el origen exista y sea una carpeta;
- que los destinos sean escribibles;
- que origen y destinos no se solapen;
- que dos destinos no resuelvan a la misma ubicación ni se contengan entre sí;
- que no se escriba a través de enlaces simbólicos;
- que no existan conflictos archivo/carpeta;
- que haya espacio suficiente considerando el pico temporal de reemplazos y una reserva de seguridad por volumen.

El origen también se vuelve a comprobar durante la copia para detectar cambios de tamaño o modificación y, al finalizar, se valida que la estructura planificada siga siendo coherente.

## Interfaz

La UI muestra:

- progreso global y por destino;
- velocidad reciente por destino;
- ETA;
- profundidad de cola;
- reintentos de I/O;
- archivo actual;
- uso de buffers del pool;
- estado por destino: espera, copiando, verificando, completo, error o cancelado.

Una fuente formada únicamente por directorios vacíos termina mostrando **100%** cuando el destino alcanza `Done`.

El resumen final distingue explícitamente trabajos completados, destinos con errores, destinos fallidos y cancelaciones.

## Uso

1. Selecciona una carpeta de origen.
2. Agrega uno o más destinos.
3. Configura las opciones:
   - **Omitir iguales**: evita reescribir archivos que cumplen la comparación rápida configurada.
   - **Continuar con errores**: mantiene activos los destinos sanos cuando otro falla.
   - **Verificar BLAKE3**: habilita comprobación criptográfica de contenido.
4. Inicia la copia y espera a que el preflight termine.
5. Puedes pausar, reanudar o cancelar el trabajo.

## Recuperación y pruebas de campo recomendadas

Antes de publicar una release se recomienda probar con hardware real:

- desconectar un destino USB durante una copia y comprobar que solo ese destino falle dentro del timeout correspondiente a su fase;
- cancelar una copia parcial, relanzarla y comprobar que los archivos ya comprometidos y válidos se reanuden sin copiarse innecesariamente;
- terminar el proceso abruptamente, relanzarlo y comprobar que `completed.jsonl`, `manifest.b3` y los archivos físicamente comprometidos permanezcan coherentes;
- comparar hashes de archivos representativos después de cada escenario.

## Compilación desde código fuente

El workflow de release usa Rust **1.98.1** para obtener builds reproducibles respecto al toolchain. Además existe una comprobación no bloqueante contra el `stable` más reciente para detectar incompatibilidades futuras.

```bash
git clone https://github.com/ReinierTutoriales/disk-duplicator.git
cd disk-duplicator
cargo build --locked --release
```

Ejecutable:

```text
target/release/RepartoCopier.exe
```

GitHub Actions ejecuta antes del artifact:

```text
cargo clippy --locked --all-targets -- -D warnings
cargo test --locked --release --verbose
cargo build --locked --release --verbose
```

## Requisitos

- Windows 10/11 x64.
- Memoria suficiente para la aplicación, buffers en vuelo y cachés del sistema. El motor limita su presupuesto principal de buffers FAN-OUT a 2 GiB.
- Espacio libre suficiente en cada destino.
- Permisos de lectura sobre el origen y escritura sobre los destinos.

## Licencia

MIT.
