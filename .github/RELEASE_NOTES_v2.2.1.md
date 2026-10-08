## RepartoCopier v2.2.1

Motor que elige cómo leer según el disco de origen, interfaz Windows 11 adaptable y diálogos con el tema de la aplicación. Resultados medidos con copias reales de 28,24 GiB (5 ISO).

### Motor
- **NVMe de origen:** un lector y un búfer por destino. Cada destino copia a su propia velocidad y termina sin esperar al más lento (NVMe listos en ~30 s mientras un USB seguía copiando).
- **SSD/HDD SATA, USB, red o disco no identificado de origen:** un único lector compartido que lee el origen una sola vez para todos los destinos, con el bloque siguiente ya en lectura. HDD USB → 3 NVMe: de 14:02 a **4:26** (3,16× más rápido), con el HDD leyendo el 99,97 % del tiempo a su máximo.
- Cada destino se marca como terminado en cuanto acaba el suyo.
- Los lectores acuerdan el hash BLAKE3 de cada archivo: si el origen cambia durante la copia, se detiene antes de que otro destino confirme contenido distinto.
- Archivos existentes: los iguales en tamaño y fecha se omiten sin leerlos; los distintos se reemplazan de forma atómica.
- Con «Continuar si falla un archivo», un archivo existente que cambia durante la copia es un error de ese archivo, no de todo el destino.

### Interfaz
- Diseño adaptable a cualquier ancho, márgenes y tipografía de Windows 11, iconos Segoe Fluent con colores de estado.
- Tarjetas por destino con su propia barra de progreso.
- Diálogos (Ajustes, Acerca de, Apagar, Resultados) como ventanas propias, modales, fuera de la app y con el tema claro/oscuro de la aplicación.
- Menú nativo con Ctrl+O / Ctrl+S y Ctrl+Enter para iniciar.

### Validación
- Pruebas del núcleo en Release, WinUI Release x64, comprobación de layout renderizado en claro y oscuro (ventana y diálogos), banco de lectura y publicación self-contained win-x64.
- `SHA256SUMS.txt` incluido.
