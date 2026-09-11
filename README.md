# RepartoCopier

**RepartoCopier** es una aplicación de escritorio para Windows diseñada para copiar **una carpeta de origen a varios destinos en paralelo**, con una interfaz gráfica simple, verificación de integridad y reanudación segura.

> Una carpeta. Varios destinos. Una sola operación.

---

## Características

- **1 origen → N destinos** en una sola operación.
- Copia en paralelo mediante arquitectura **FAN-OUT**.
- Conserva la **carpeta raíz seleccionada** en cada destino.
- Mantiene la **estructura completa de subcarpetas**, incluidas las carpetas vacías.
- Verificación de integridad mediante **BLAKE3**.
- Reanudación de trabajos interrumpidos.
- Estado de reanudación almacenado **fuera del árbol copiado**.
- Recuperación segura mediante archivos temporales y backups.
- Detección de destinos que dejan de responder.
- Pausa y cancelación cooperativas.
- Los destinos pueden continuar de forma independiente si uno falla.
- Comprobación previa de espacio libre, permisos y conflictos de rutas.
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

El origen se lee una vez y los bloques leídos se comparten entre todos los destinos activos.

Conceptualmente:

```text
                ┌── Destino 1
Origen → Reader ├── Destino 2
                ├── Destino 3
                └── Destino N
```

Cada destino posee su propio worker y su propia cola limitada.

Esto permite que un destino más rápido siga avanzando aunque otro sea más lento, sin tener que volver a leer el archivo de origen independientemente para cada destino.

---

## Integridad y seguridad

RepartoCopier utiliza varias capas de protección.

### Archivos temporales

Los archivos se escriben primero como temporales dentro del área de estado del programa.

Solo después de completar correctamente la escritura se realiza el commit hacia el destino final.

### Backup durante reemplazos

Si ya existe un archivo en el destino:

1. el archivo existente se mueve temporalmente a un backup;
2. el archivo nuevo se coloca en su ubicación final;
3. si el commit falla, se intenta restaurar el original.

Si también falla la restauración, RepartoCopier conserva el backup y reporta claramente su ubicación.

### Verificación BLAKE3

Durante la lectura del origen se calcula un hash BLAKE3.

El destino puede volver a leerse para verificar que los datos escritos coincidan exactamente con el flujo original.

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

Dentro del estado pueden existir archivos como:

```text
completed.jsonl
manifest.b3
tmp\
```

Esto evita contaminar la carpeta copiada con archivos internos del programa.

Antes de reutilizar un estado previo, RepartoCopier valida que los archivos sigan coincidiendo con el manifiesto BLAKE3.

Si el origen o el destino divergen, el archivo vuelve a copiarse.

---

## Pausa y cancelación

La pausa es cooperativa y utiliza primitivas de sincronización del sistema en lugar de espera activa.

Los workers se detienen en puntos seguros entre operaciones de filesystem.

La cancelación despierta también a los workers que estén pausados.

> Una operación de I/O que ya se encuentre bloqueada dentro del sistema operativo debe regresar antes de que el thread pueda observar la cancelación.

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

El cálculo de espacio intenta considerar el pico temporal necesario durante el reemplazo de archivos, no únicamente la suma lógica del contenido.

También mantiene una reserva mínima para evitar llenar completamente el volumen.

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

Ejecutar:

```powershell
cargo test --release
```

Clippy:

```powershell
cargo clippy --all-targets -- -D warnings
```

---

## GitHub Actions

Cada push a `main` ejecuta en Windows:

- Clippy,
- pruebas release,
- build release,
- generación del ejecutable como artifact.

Cuando `Cargo.toml` contiene una versión que todavía no posee release, el workflow puede crear automáticamente la release correspondiente.

---

## Estructura principal del proyecto

```text
src/
├── main.rs
├── app_v2.rs
├── app_v2/
├── engine.rs
├── engine_v3/
├── paths.rs
├── preflight.rs
├── preflight_v2/
└── config.rs
```

Los archivos `app_v2.rs`, `engine.rs` y `preflight.rs` actúan como puntos de entrada para sus implementaciones divididas en partes.

---

## Filosofía del proyecto

RepartoCopier prioriza:

1. **integridad de datos**;
2. **recuperación segura ante fallos**;
3. **comportamiento predecible**;
4. **rendimiento realista con múltiples destinos**;
5. **interfaz simple**.

El objetivo no es solamente copiar rápido, sino poder confiar en el resultado.

---

## Licencia

MIT License

Copyright © 2026 ReinierTutoriales
