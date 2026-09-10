# RepartoCopier

### Una carpeta. Varios destinos. Una sola operación.

**RepartoCopier** es una aplicación para Windows creada para copiar una carpeta a varios destinos al mismo tiempo de forma **rápida, segura y confiable**.

Selecciona lo que quieres copiar, agrega los destinos y deja que RepartoCopier se encargue del resto.

---

## ✨ Copiar a varios destinos ya no tiene que ser complicado

Cuando necesitas preparar varias unidades, memorias USB o ubicaciones con el mismo contenido, repetir la copia una y otra vez consume tiempo y multiplica el trabajo.

RepartoCopier simplifica todo el proceso: **lee el origen una vez y distribuye el contenido entre todos los destinos activos**, manteniendo la estructura original de carpetas.

### Lo esencial

- **Varios destinos a la vez** — agrega los destinos que necesites y gestiona toda la copia desde una sola ventana.
- **Protección del contenido** — puede comprobar que los archivos copiados coincidan con el origen.
- **Reanudación inteligente** — si una copia se interrumpe, aprovecha el trabajo ya completado y validado.
- **Destinos independientes** — un problema en una unidad no tiene por qué detener las demás.
- **Pausa y continuación** — conserva el control durante trabajos largos.
- **Progreso claro** — muestra avance, velocidad, tiempo estimado y estado individual de cada destino.
- **Carpetas intactas** — mantiene la jerarquía del origen, incluidas las carpetas vacías.

> **RepartoCopier trabaja con carpetas y archivos.** No modifica particiones ni realiza clonación física de discos.

---

## 🚀 Pensado para trabajar, no para complicarte

El flujo de uso es directo:

1. **Selecciona la carpeta de origen.**
2. **Agrega uno o varios destinos.**
3. Elige si quieres omitir archivos iguales, continuar si un destino falla o realizar una verificación completa.
4. **Inicia la copia.**

RepartoCopier comprueba las condiciones necesarias antes de comenzar y mantiene cada destino supervisado durante el proceso.

---

## 🛡️ Confianza de principio a fin

Una copia rápida sirve de poco si no puedes confiar en el resultado. Por eso RepartoCopier está diseñado alrededor de la integridad de los datos.

Con la verificación activada, el contenido se comprueba durante el proceso y vuelve a validarse en el destino. Los archivos no se consideran completados hasta superar las comprobaciones correspondientes.

Si una operación se interrumpe, RepartoCopier conserva fuera de la carpeta copiada la información necesaria para determinar qué archivos fueron terminados correctamente. Al volver a iniciar el trabajo, valida ese estado antes de reutilizarlo.

La reanudación funciona **por archivo completado**: un archivo que quedó a medio escribir puede comenzar nuevamente, mientras que los archivos ya confirmados pueden conservarse sin repetir trabajo innecesario.

---

## ⚡ Un motor moderno para copias múltiples

RepartoCopier no ejecuta una copia independiente completa para cada destino. Su motor comparte de forma eficiente los datos leídos entre los destinos activos y controla cada salida por separado.

Esto permite mantener un flujo de trabajo ordenado incluso cuando las unidades conectadas tienen velocidades diferentes. La memoria y las colas de trabajo están limitadas para evitar un crecimiento descontrolado durante copias grandes.

La velocidad real siempre dependerá del equipo, las unidades utilizadas, la conexión USB/SATA/NVMe y el tipo de archivos. RepartoCopier no presenta cifras artificiales de rendimiento como garantía.

---

## 💡 Ideal para

- Preparar varias memorias USB con el mismo contenido.
- Distribuir una colección de archivos a varias unidades.
- Repetir entregas de carpetas sin iniciar cada copia manualmente.
- Trabajos donde necesitas saber claramente qué destino terminó y cuál presentó un problema.
- Copias largas que pueden necesitar pausa, cancelación o reanudación posterior.

---

## 🖥️ Información clara mientras trabaja

Durante la copia puedes consultar:

- progreso general y por destino;
- velocidad actual;
- tiempo estimado restante;
- archivo en proceso;
- reintentos y estado de cada destino;
- copia completada, error o cancelación.

Al finalizar, RepartoCopier diferencia claramente los destinos correctos de los que terminaron con errores o fueron cancelados.

---

## ⚙️ Opciones principales

**Omitir iguales**  
Evita volver a escribir archivos que ya cumplen la comparación rápida utilizada por la aplicación.

**Continuar con errores**  
Permite que los destinos sanos sigan trabajando cuando otro destino presenta un problema.

**Verificar BLAKE3**  
Activa una comprobación criptográfica del contenido para aumentar la confianza en el resultado final.

---

<details>
<summary><strong>🔧 Detalles técnicos</strong></summary>

<br>

Esta sección está destinada a desarrolladores, revisores y usuarios que quieran conocer el funcionamiento interno.

### Motor de copia

- Arquitectura de distribución **1 origen → N destinos** con un único lector del origen.
- Bloques de **16 MiB** compartidos entre los destinos activos.
- Cola acotada por destino, con capacidad máxima de **16 elementos**.
- Backlog pendiente y pool de buffers limitados para controlar la presión de memoria.
- Máximo de **16 buffers libres retenidos** en el pool.
- Presupuesto principal de buffers en vuelo limitado a **2 GiB**; esto no representa un requisito mínimo de RAM del sistema.
- Worker independiente por destino.
- El preflight escanea el origen una sola vez y reutiliza la planificación para crear la estructura de directorios.
- Origen y destinos se canonicalizan para detectar alias, duplicados y solapamientos peligrosos.

### Integridad

El lector calcula BLAKE3 mientras transmite cada archivo y conserva los hashes de la ejecución para la validación final.

Con la verificación habilitada:

1. se calcula el hash mientras se lee el origen;
2. cada destino escribe primero un archivo temporal `.part` fuera del árbol copiado;
3. el temporal puede releerse y verificarse antes de ser confirmado;
4. se sincronizan los datos antes del reemplazo final;
5. la validación final relee el destino y compara su contenido con el hash esperado;
6. las reanudaciones utilizan `manifest.b3` y vuelven a validar origen y destino antes de aceptar estado previo.

El reemplazo utiliza un protocolo de **backup + rename + restauración en caso de fallo**. No se presenta como una transacción garantizada por el filesystem.

### Estado y reanudación

El estado operacional se mantiene fuera del árbol copiado:

```text
.disk-duplicator-state/<id-del-destino>/
```

Archivos principales:

- `completed.jsonl` — archivos confirmados;
- `manifest.b3` — hashes persistidos;
- `tmp/` — temporales y backups administrados por la aplicación.

### Supervisión de I/O

La detección de operaciones atascadas utiliza umbrales diferentes según la fase:

- escritura: aproximadamente **6 s** sin progreso;
- sincronización, verificación y commit: aproximadamente **60 s**.

Esto evita tratar del mismo modo una escritura detenida y una operación legítimamente lenta de sincronización o verificación.

Un hilo ya bloqueado dentro de una llamada de I/O del sistema operativo no puede terminarse de forma segura desde Rust. El motor utiliza guardas adicionales para impedir que un worker previamente declarado fallido registre después un archivo como completado.

### Telemetría

- velocidad reciente mediante EWMA con constante temporal de **2 s**;
- velocidad promedio acumulada para métricas de largo plazo y ETA;
- unidades IEC: **KiB/s, MiB/s y GiB/s**;
- buffer de verificación de **8 MiB** creado únicamente cuando la verificación está activa.

### Preflight

Antes de escribir se comprueba que:

- el origen exista y sea una carpeta;
- cada destino sea escribible;
- origen y destinos no se solapen;
- los destinos no apunten a la misma ubicación ni se contengan entre sí;
- no se escriba a través de enlaces simbólicos;
- no existan conflictos entre archivos y carpetas;
- exista espacio suficiente considerando reemplazos temporales y una reserva de seguridad.

El origen vuelve a comprobarse durante y al finalizar la operación para detectar cambios incompatibles con la planificación inicial.

</details>

---

## 🧪 Validación antes de una publicación

El repositorio incluye `TESTING.md` con las pruebas de campo recomendadas para validar escenarios que requieren hardware real, como desconexiones USB, cancelación y reanudación, o recuperación después de un cierre inesperado.

---

## 🛠️ Compilar desde el código fuente

La compilación principal utiliza **Rust 1.98.1**. El CI también comprueba de forma no bloqueante la compatibilidad con la versión `stable` más reciente.

```bash
git clone https://github.com/ReinierTutoriales/disk-duplicator.git
cd disk-duplicator
cargo build --locked --release
```

Ejecutable generado:

```text
target/release/RepartoCopier.exe
```

Antes de generar el artifact, GitHub Actions ejecuta Clippy, la batería de tests y la compilación release.

---

## 📋 Requisitos

- Windows 10/11 x64.
- Permisos de lectura en el origen y escritura en los destinos.
- Espacio libre suficiente en cada destino.
- Memoria disponible para la aplicación y las operaciones normales del sistema.

---

## 📄 Licencia

Distribuido bajo licencia **MIT**.
