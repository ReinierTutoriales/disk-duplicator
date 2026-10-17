# Uniformidad visual y respuesta al tamaño — 2026-10-16

## Qué se encontró

La UI no estaba ausente de adaptación: ya ocultaba etiquetas y reorganizaba tarjetas por ancho, pero los botones principales y la opción de apagado conservaban filas horizontales. El manejador de tamaño repetía asignaciones de padding/visibilidad en cada cambio de dimensiones aunque no cambiara de modo. El pie tenía una altura fija de 28 DIP. Los diálogos medían su contenido con el ancho solicitado, aunque el monitor obligase a reducirlo, y limitaban el cliente sin descontar el marco exterior. Tampoco recalculaban su tamaño al cambiar de DPI.

Estos son problemas concretos de adaptación y mantenimiento. No se ha demostrado que expliquen por sí solos todos los tirones percibidos durante copia o redimensionado; no se recogió una traza ETW/WPA de frames ni un benchmark de latencia de entrada.

## Cambios

- Se aplica el modo ancho/estrecho solo cuando cambia, evitando repetir las asignaciones del modo en cambios de altura o de píxeles dentro del mismo intervalo.
- Por debajo de 640 DIP, Iniciar copia pasa bajo el título y Apagar al terminar pasa bajo Pausar/Cancelar. Se reserva separación entre filas solo en ese modo.
- El pie mantiene un mínimo de 28 DIP y crece con su contenido. El comprobador usa su altura real al verificar que otros controles son alcanzables.
- Los diálogos miden el ancho cliente realmente disponible, descuentan el marco y mantienen la ventana completa dentro del límite de altura del 85 % del área de trabajo. Al cambiar el DPI vuelven a medir. Los botones largos permiten varias líneas.
- Hay pruebas de geometría del diálogo a 96/120/144/192 DPI, incluyendo un área de trabajo pequeña. Son pruebas matemáticas; no equivalen a renderizar físicamente en esos monitores.
- El verificador real añade anchos alrededor de 640 DIP, ida/vuelta sobre el breakpoint, un diálogo estrecho de 320 DIP y comprobación de familia tipográfica en encabezado/cuerpo/pie de los diálogos.
- Las capturas optativas se habilitan con `REPARTOCOPIER_LAYOUT_CAPTURE=1` al ejecutar `--layout-check`. Capturan únicamente el árbol XAML de esta aplicación, sin escritorio ni otras apps. RenderTargetBitmap no incluye Mica ni todos los elementos dibujados fuera del árbol; las zonas transparentes no representan el fondo real de Windows. No se generan capturas durante el uso normal.

## Fuentes, colores e iconos

Se inspeccionaron MainWindow.xaml, App.xaml, AppDialog, AppDialogs, DestinationRows y CompletionNotifier. Los textos de ventana principal y los encabezados/cuerpos/pies de los diálogos usan Segoe UI Variable en este Windows. Los tamaños Body/Caption/Subtitle mantienen una jerarquía deliberada; uniformidad no significa que todo tenga el mismo tamaño o peso.

Los controles, tarjetas, texto secundario y estados utilizan recursos de tema de WinUI. Los iconos de controles son FontIcon y los estados comparten glifos/recursos semánticos de éxito, advertencia y error. El logo PNG/ICO de identidad es una categoría distinta y conserva los tamaños de marca. No se añadieron fuentes o bibliotecas de iconos externas ni una paleta nueva. Las capturas revisadas de Acerca de, Ajustes y el diálogo estrecho muestran esa jerarquía y tema comunes.

La notificación final usa AppNotificationBuilder: Windows dibuja su presentación. No es un diálogo XAML de la app. Los selectores de archivos/carpetas también son superficies del sistema; su presentación no se debe confundir con la del tema forzado de la aplicación. No se modificó ni se envió una notificación del sistema durante esta revisión.

## Alcance de aceptación

Siguen pendientes Narrator/alto contraste, texto de accesibilidad elevado y arrastrar entre monitores físicos de distinto DPI. Para investigar una sensación de pesadez durante copia, se necesita reproducir la misma operación/topología y registrar CPU, memoria, actividad del hilo UI y tiempos de frame; esta modificación no afirma mejoras medidas de throughput ni de FPS. No se cambia el motor, sus buffers, la verificación ni las garantías de recovery.

Las capturas y los informes de ejecución se conservan localmente en `.audit-artifacts/`; no se publican imágenes de la máquina en el repositorio.

## Validación final ejecutada

- Tests Release: 286 correctos, 1 omitido, 0 fallos; 287 en total, 51 s. La omisión sigue siendo el caso de nombres a.txt/A.txt en un filesystem que no permite ambos.
- Compilación WinUI Release win-x64 sin errores ni advertencias y publicación self-contained final completada; carpeta publicada con un único ejecutable.
- Ese ejecutable ejecutó el verificador ampliado: salida 0, Passed=true, 46 entradas de comprobación. Incluye preparación/ejecución en claro y oscuro, 540/639/640/641/760/1200 DIP, cinco diálogos por tema, familia tipográfica y recorrido de vuelta a través del breakpoint. DPI de render real: 96.
- Capturas XAML de antes y después disponibles y revisadas; la transparencia/Mica excluida del render no se interpreta como un fondo negro de la app.
- Inicio normal sin copia, tras 2 s de asentamiento: muestra de 5014 ms, 0 ms de CPU registrados, working set 137.9 MiB y private bytes 102.3 MiB; cierre normal con salida 0. Es una muestra local de reposo, sin comparación A/B ni extrapolación al equipo del usuario o a la carga de copia.
- git diff --check sin errores. Pendiente CI del commit y validación física/manual descrita arriba.

API de captura contrastada con la [documentación oficial de RenderTargetBitmap](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.media.imaging.rendertargetbitmap.renderasync). Las capturas son optativas y no alteran la apariencia del producto durante el uso normal.
