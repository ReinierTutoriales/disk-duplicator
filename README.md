# RepartoCopier

**RepartoCopier** es una aplicación de escritorio para Windows diseñada para copiar **una carpeta de origen a varios destinos en paralelo**, con una interfaz gráfica simple, verificación de integridad y reanudación segura.

> Una carpeta. Varios destinos. Una sola operación.

---

## Características

- **1 origen → N destinos** en una sola operación.
- Copia en paralelo mediante arquitectura **FAN-OUT**.
- El origen se lee una vez durante la copia y los bloques se comparten entre todos los destinos activos.
- Conserva la **carpeta raíz seleccionada** en cada destino.
- Mantiene la **estructura completa de subcarpetas**, incluidas las carpetas vacías.
- Verificación de integridad mediante **BLAKE3**.
- Reanudación de trabajos interrumpidos con validación física del estado persistido.
- Estado de reanudación almacenado **fuera del árbol copiado**.
- Recuperación segura mediante archivos temporales y backups.
- I/O de escritura cancelable en Windows mediante **overlapped I/O + `CancelIoEx`**.
- Sincronización de archivos aislada y cancelable mediante **`CancelSynchronousIo`**.
- Detección de destinos que dejan de responder.
- Pausa mediante `Condvar`, sin espera activa.
- Los destinos pueden continuar de forma independiente si uno falla.
- Comprobación previa de espacio libre, permisos y conflictos de rutas.
- Memoria del FAN-OUT acotada mediante un pool global de buffers compartidos.
- Interfaz clara con:
  - progreso general,
  - progreso por destino,
  - velocidad,
  - ETA,
  - archivos completados,
  - omitidos,
  - errores,
  - reintentos,
  - profundidad de cola.
- Tema **Sistema / Claro / Oscuro**.
- Integración con color de acento de Windows.
- Ejecutable autónomo para **Windows 10/11 x64**.

---

## Cómo funciona

Cuando seleccionas una carpeta de origen, RepartoCopier replica esa carpeta completa dentro de cada destino.

Ejemplo:

```text
Origen:
D:\Instaladores

Destinos:
E:\
F:\
G:\
```

Resultado:

```text
E:\Instaladores\...
F:\Instaladores\...
G:\Instaladores\...
```

La estructura interna se conserva tal como existe en el origen.

---

## Arquitectura FAN-OUT

RepartoCopier utiliza un único modo de copia: **FAN-OUT**.

El reader obtiene cada bloque del origen una sola vez y entrega referencias compartidas del mismo bloque a los workers de los destinos activos.

```text
                ┌── Worker destino 1
Origen → Reader ├── Worker destino 2
                ├── Worker destino 3
                └── Worker destino N
```

Cada destino posee su propio worker y su propia cola limitada. Los datos de cada bloque se mantienen en un pool global compartido, por lo que agregar destinos no multiplica linealmente la memoria utilizada por el payload.

El backpressure impide que el reader se aleje indefinidamente de los destinos lentos. Un destino con problemas puede desconectarse del FAN-OUT sin obligar a reiniciar a los demás.

---

## I/O nativo de Windows

La ruta de escritura utiliza un backend específico para Windows en `windows_io.rs`.

### Escritura

Los bloques se escriben mediante un segundo handle abierto con `FILE_FLAG_OVERLAPPED`. La operación espera por intervalos cortos y puede abortarse con `CancelIoEx` cuando:

- el usuario cancela el trabajo, o
- el destino ha sido declarado no operativo por el supervisor.

La posición del archivo se controla explícitamente mediante el `OVERLAPPED`, por lo que no depende del file pointer del handle estándar.

### Sincronización

La sincronización durable del `.part` sigue siendo una operación síncrona de Windows. Para que no pueda secuestrar al worker principal, se ejecuta en un thread auxiliar. El worker conserva un handle de ese thread y puede solicitar la cancelación de la I/O síncrona mediante `CancelSynchronousIo`.

La lógica de cancelación contempla carreras entre el inicio/finalización del flush y la solicitud de cancelación.

---

## Integridad y seguridad

RepartoCopier utiliza varias capas de protección.

### Archivos temporales

Los archivos se escriben primero como temporales dentro del área de estado del programa.

Solo después de completar correctamente escritura, sincronización y verificaciones se realiza el commit hacia el destino final.

Una escritura parcial fallida no se reanuda desde un offset incierto: el temporal vuelve a truncarse al último offset confirmado antes de reintentar.

### Backup durante reemplazos

Si ya existe un archivo en el destino:

1. el archivo existente se mueve temporalmente a un backup;
2. el archivo nuevo se coloca en su ubicación final;
3. si el commit falla, se intenta restaurar el original.

Si también falla la restauración, el error se considera **crítico**, se conserva el backup y se reporta claramente su ubicación.

### Verificación BLAKE3

Durante la lectura del origen se calcula un hash BLAKE3.

Cuando la verificación está habilitada, los destinos se vuelven a leer y se comparan contra el hash esperado. La validación final de múltiples destinos puede ejecutarse en paralelo.

### `skip_same`

Un archivo no se omite únicamente porque coincidan tamaño y fecha. Cuando `skip_same` necesita demostrar igualdad, se compara el contenido mediante BLAKE3.

La prueba se mantiene **por archivo y por destino**: que un destino sea válido no autoriza a omitir el mismo archivo en otro destino.

### Validación final

Al terminar se valida:

- existencia de archivos,
- tamaño,
- estructura de directorios,
- hash cuando corresponde.

Un destino no se considera completo si la validación final falla.

---

## Reanudación

El estado de una copia se almacena fuera de la carpeta duplicada.

Ejemplo:

```text
E:\Instaladores\
E:\.disk-duplicator-state\<id>\
```

Dentro del estado pueden existir:

```text
completed.jsonl
manifest.b3
tmp\
```

Esto evita contaminar la carpeta copiada con archivos internos del programa.

Antes de reutilizar un estado previo, RepartoCopier valida que los archivos sigan coincidiendo con el manifiesto BLAKE3. Los hashes del origen necesarios durante el preflight se comparten entre destinos para evitar releer innecesariamente el mismo archivo fuente.

Si el origen o el destino divergen, el archivo vuelve a copiarse.

---

## Pausa y cancelación

La pausa es cooperativa y utiliza `Condvar`; no realiza polling activo mientras el trabajo está pausado.

La cancelación despierta también a los workers pausados.

En Windows, las dos operaciones de I/O potencialmente largas del camino de escritura tienen mecanismos de cancelación específicos:

- `WriteFile` overlapped → `CancelIoEx`;
- sincronización síncrona → `CancelSynchronousIo` sobre el thread auxiliar.

La cancelación del sistema operativo es una solicitud: Windows puede completar una operación que ya haya terminado antes de procesar la cancelación. El motor comprueba después el estado del trabajo y no hace commit de un archivo cancelado.

---

## Preflight

Antes de iniciar una copia se comprueba:

- que el origen exista;
- que los destinos sean válidos;
- que origen y destino no se solapen;
- que dos destinos no apunten a la misma ubicación;
- que el destino sea escribible;
- que no haya enlaces simbólicos peligrosos;
- que no existan conflictos archivo/carpeta;
- que exista espacio libre suficiente;
- que temporales/backups anteriores puedan recuperarse correctamente.

---

## Espacio libre

El cálculo de espacio considera el pico temporal necesario durante el reemplazo de archivos, no únicamente la suma lógica del contenido.

También mantiene una reserva para evitar llenar completamente el volumen.

---

## Requisitos

### Para ejecutar

- Windows 10/11 x64.

### Para compilar

- Rust estable compatible con el proyecto.
- Toolchain MSVC para Windows.

El workflow principal utiliza Rust **1.98.1** y además existe una comprobación no bloqueante contra el stable más reciente.

---

## Compilar

```powershell
cargo build --release
```

El ejecutable se genera en:

```text
target\release\RepartoCopier.exe
```

---

## Pruebas

```powershell
cargo test --release
cargo clippy --all-targets -- -D warnings
```

El backend nativo contiene pruebas específicas de Windows para verificar la coexistencia del handle estándar con el handle overlapped, cancelación previa de escritura y sincronización normal mediante el helper cancelable.

---

## GitHub Actions

Cada push a `main` ejecuta en Windows:

- Clippy,
- pruebas release,
- build release,
- generación del ejecutable como artifact.

Los runs obsoletos de la misma rama se cancelan cuando entra un commit más reciente, para concentrar los recursos de CI en el HEAD actual.

Cuando `Cargo.toml` contiene una versión que todavía no posee release, el workflow puede crear automáticamente la release correspondiente.

---

## Estructura principal del proyecto

```text
src/
├── main.rs
├── app.rs
├── app/
├── config.rs
├── engine_impl.rs
├── engine_impl/
├── paths.rs
├── preflight.rs
├── preflight/
└── windows_io.rs
```

- `app` contiene la interfaz.
- `engine_impl` contiene el motor FAN-OUT.
- `preflight` contiene planificación, validación, reanudación y verificación final.
- `paths` centraliza IDs y rutas de estado/transitorios.
- `windows_io` encapsula el backend Win32 cancelable y mantiene el `unsafe` fuera del motor FAN-OUT.

---

## Filosofía del proyecto

RepartoCopier prioriza:

1. **integridad de datos**;
2. **recuperación segura ante fallos**;
3. **comportamiento predecible**;
4. **rendimiento realista con múltiples destinos**;
5. **cancelación controlada de I/O**;
6. **interfaz simple**.

El objetivo no es solamente copiar rápido, sino poder confiar en el resultado.

---

## Licencia

MIT License

Copyright © 2026 ReinierTutoriales
