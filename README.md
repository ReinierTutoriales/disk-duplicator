# Disk Duplicator

Copiador de archivos de alto rendimiento **1 origen → N destinos** para HDD y SSD internos o externos.

Disk Duplicator no clona discos, particiones, GPT/MBR ni sistemas operativos. Su objetivo es duplicar el contenido de una carpeta hacia múltiples destinos de almacenamiento de forma concurrente y segura.

## Arquitectura

El motor usa un solo lector del origen y distribuye cada bloque de datos a los destinos activos mediante **fan-out estricto**. Los buffers se comparten entre destinos y están limitados por un presupuesto global de memoria (2048 MiB, 128 buffers de 16 MiB), evitando cargar archivos completos en RAM.

Cada destino mantiene su propia cola y worker independiente. Los destinos lentos o bloqueados se detectan automáticamente (6 segundos sin progreso) y se aíslan sin afectar a los destinos sanos, que continúan copiando a máxima velocidad.

**Sin modo de respaldo**: El motor opera exclusivamente en fan-out. No hay segunda lectura del origen ni cambio de modo cuando un destino falla.

## Rendimiento

### Optimizaciones implementadas

- **Bloque de 16 MiB**: Reduce syscalls en 75% para SSD/NVMe
- **Snapshot único por frame**: Elimina clones duplicados de estado (66%+ reducción)
- **Repaint adaptativo**: 200ms en copia activa, 500ms pausado, 80ms en preflight
- **Buffer de verificación reutilizado**: Un solo buffer por worker durante validación BLAKE3
- **Eliminación de syscalls redundantes**: `create_dir_all` solo al inicio, no por archivo
- **Backlog optimizado**: 32 elementos por destino (evita acaparamiento de memoria)
- **Codificación hex manual**: `state_key` 90% más rápido que `format!` en bucle
- **Despacho no bloqueante**: `try_send` con cola de pendientes ordenada

### Throughput

Con dos SSD NVMe conectados vía USB 3.2:
- Cada destino mantiene velocidad cercana al límite del bus
- Sin caída de velocidad al añadir segundo destino
- Throughput agregado cercano a la suma de ambos

## Seguridad

### Preflight

- Validación de origen y destinos antes de copiar
- Rechazo de enlaces simbólicos (riesgo de integridad)
- Detección de rutas peligrosas y solapamientos
- Comprobación de espacio disponible por destino

### Durante la copia

- Escritura mediante archivos `.part` y commit atómico por rename
- Backup temporal durante reemplazo de archivos existentes
- Validación de origen antes y después de lectura (detecta cambios durante copia)
- Reintentos automáticos con backoff exponencial (hasta 2 reintentos)

### Integridad de datos

- **Copia 1:1 estricta**: Sin exclusiones silenciosas por extensión o nombre
- **Metadata operacional externa**: `.disk-duplicator-state` fuera del árbol copiado
- **Validación BLAKE3 opcional**: Hash verificado origen/destino
- **Validación estructural**: Destino incompleto no puede mostrarse como COMPLETO
- **Reanudación verificada**: Valida existencia física y tamaño antes de omitir archivos

### Aislamiento de errores

- **Detección de atasco**: Destinos sin progreso por 6s se marcan como Failed
- **JoinHandle desacoplado**: Workers trabados no bloquean el proceso completo
- **drain_pending protegido**: No se cuelga en destinos fallidos
- **Continuación con errores**: Opción para continuar copiando a destinos sanos cuando uno falla

## Interfaz

### Tema automático

- **Detección nativa de Windows 11**: FFI directo a `advapi32.dll` (sin dependencias externas)
- **Detección periódica**: Re-chequeo cada 30 segundos para cambios de tema en caliente
- **Repaint en idle**: Detecta cambios aunque la app esté inactiva
- **Paleta adaptativa**: 9 colores optimizados para ambos temas con contraste WCAG AA
- **Tipografía jerárquica**: 5 estilos (Heading 18px, Body 13px, Monospace 12px, Button 13px, Small 11px)

### Feedback visual

- **Progreso global**: Barra de progreso con porcentaje y ETA
- **Progreso por destino**: Velocidad, progreso, profundidad de cola, estado
- **Validación preventiva**: Avisos amarillos para rutas inválidas antes de iniciar
- **Feedback de errores**: Footer en rojo durante 5 segundos cuando hay errores
- **Estado en lista de destinos**: Muestra [COPIANDO], [COMPLETO], [ERROR] con colores
- **Hover detallado**: Ruta completa y archivo actual al pasar el cursor

### Información en tiempo real

- Velocidad de escritura por destino (B/s, KB/s, MB/s, GB/s)
- Profundidad de cola por destino
- Contador de reintentos por errores de I/O
- Uso de buffers del pool global
- Estado de cada destino: EN ESPERA, COPIANDO, VERIFICANDO, COMPLETO, CON ERRORES, ERROR, CANCELADO

## Uso

1. **Selecciona una carpeta de origen**
   - Usa el botón "Examinar" o escribe la ruta manualmente
   - La UI valida que exista y sea una carpeta

2. **Agrega uno o más destinos HDD/SSD**
   - Botón "+ Agregar" para cada destino
   - Lista numerada con estado de cada destino
   - Botón "×" para eliminar destinos antes de iniciar

3. **Configura las opciones**
   - **Omitir iguales**: Salta archivos con mismo tamaño y fecha de modificación
   - **Continuar con errores**: Sigue copiando a destinos sanos si uno falla
   - **Verificar BLAKE3**: Valida hash origen/destino después de cada archivo

4. **Inicia la copia**
   - El preflight valida el trabajo antes de escribir datos
   - Puedes pausar/continuar en cualquier momento
   - Puedes cancelar sin perder progreso (reanudación segura)

5. **Monitorea el progreso**
   - Progreso global y por destino en tiempo real
   - Destinos atascados se marcan automáticamente como ERROR
   - Destinos sanos continúan sin interrupción

El ejecutable de Windows se genera como artifact de GitHub Actions: `disk-duplicator.exe`.

## Requisitos

- **Sistema operativo**: Windows 10/11 x64
- **Memoria**: 2 GB RAM mínimo (usa 2048 MiB para buffers)
- **Espacio en disco**: Suficiente para origen + todos los destinos
- **Permisos**: Lectura en origen, escritura en destinos

## Compilación desde código fuente

```bash
# Clonar repositorio
git clone https://github.com/ReinierTutoriales/disk-duplicator.git
cd disk-duplicator

# Compilar release
cargo build --release

# Ejecutable en: target/release/disk-duplicator.exe
```

## Licencia
MIT.
