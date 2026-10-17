# Materiales, iconos y composición nativos

## Problema observado en el código

La ventana principal usaba Mica, que es un material tintado por el wallpaper y no cristal con visión del escritorio. Los diálogos no tenían SystemBackdrop y su raíz sólida tapaba cualquier material de fondo. Aunque compartían fuentes y recursos de tema, eso no acreditaba un acabado translúcido uniforme. Las capturas RenderTargetBitmap anteriores tampoco contenían el compositor y no podían demostrar Mica/Acrylic.

El ICO tenía solo una entrada de 256×256. El mismo PNG de 256×256 se reutilizaba directamente para la cabecera de 16 DIP y Acerca de de 48 DIP. El contenido principal tenía un límite de 980 DIP, dejando ancho sin aprovechar en ventanas grandes.

## Cambios

- Ventana principal y ventanas de diálogo usan el mismo NativeWindowMaterial, que conecta DesktopAcrylicBackdrop del sistema. Tras conectarlo, deja transparente la raíz XAML. No se simula vidrio mediante un fondo RGB o una opacidad del texto.
- Las capas de tarjetas, encabezado del diálogo y comandos conservan los recursos de tema de WinUI, incluidas sus versiones de alto contraste. Si no se puede conectar el backdrop, se mantiene el fondo sólido de tema definido en XAML. El SO gestiona la desactivación de efectos, batería, ventana inactiva, hardware y fallbacks propios del material.
- El logo maestro AppLogo.png queda intacto. El ICO incorpora tamaños 16, 20, 24, 32, 40, 48, 64, 96, 128 y 256. Se incluyen recursos PNG para cabecera de 16 DIP y marca de 48 DIP a escalas 100/125/150/200/250/300/400 %. El empaquetador tools/package_app_icons.py verifica dimensiones y entradas; necesita Pillow solo como herramienta de desarrollo, no en el producto.
- Los iconos funcionales siguen siendo FontIcon del sistema, sin convertirlos a capturas bitmap ni reemplazar la fuente Fluent por una fuente de texto. El empaquetado no rediseña el logo ni promete eliminar el suavizado que forma parte de su dibujo original.
- El contenido principal utiliza el ancho disponible, permitiendo más destinos por fila. Se conservan el layout estrecho y las medidas de legibilidad de controles.
- Acerca de usa una descripción más breve, sin prometer que cada disco mantendrá siempre su velocidad independiente. El botón único Cerrar tiene anchura de control y se alinea a la derecha. La zona de comandos reduce padding vertical de 24 a 16 DIP.
- Las notificaciones siguen usando AppNotificationBuilder nativo. Se especifica el PNG de marca de alta resolución como appLogoOverride. El título presenta el resultado y el cuerpo muestra destinos, bytes y duración; no repite la frase del título. No se intenta sustituir los colores/fuentes que controla Windows por un falso toast de la aplicación.

No se modifican el motor ni las correcciones independientes del PR #27. Este trabajo continúa en el PR de UI #26.

## Ajuste posterior de los símbolos pequeños

Los FontIcon propios de botones y destinos que medían 12/14 DIP pasan a 16 DIP. Un estilo común usa SymbolThemeFontFamily y desactiva IsTextScaleFactorEnabled únicamente para los símbolos: el texto conserva su escalado accesible. Esto evita que aumentar el texto agrande también los glifos dentro de controles compactos. Los indicadores de 20 DIP y los de diálogo de 32 DIP conservan su tamaño.

Validación repetida después del ajuste: build Release sin advertencias/errores, 286 tests correctos y uno omitido, y verificador de ventanas Passed=true a 96 DPI en claro/oscuro. Son cambios preventivos de consistencia y escalado; no se ha reproducido ni demostrado resuelto el defecto visual reportado por el usuario. El logo raster mantiene su detalle y suavizado original. Queda pendiente comprobar el resultado perceptual en el escritorio y escalas físicas adicionales.

## Comprobaciones

- La compilación Release win-x64 termina sin errores ni advertencias.
- Suite Release de la rama de UI: 286 correctos, 1 omitido, 0 fallos; 287 en total. El omitido es el caso de a.txt/A.txt en un filesystem que no admite ambos nombres.
- El empaquetador verificó diez tamaños ICO y catorce PNG. git diff confirma que el maestro PNG no cambió.
- El layout check real pasó en claro/oscuro, seis tamaños de ventana incluidos 639/640/641 DIP, los diálogos y un caso de diálogo estrecho. Verifica que la raíz principal y las de diálogo exponen un backdrop Desktop Acrylic y no lo cubren con una raíz opaca. Comprueba que el contenido ancho ya no queda limitado a 980 DIP y que los Image cargados disponen de suficientes píxeles a la escala de render actual.
- En este entorno: soporte Desktop Acrylic reportado True, AdvancedEffectsEnabled True y alto contraste False. DPI real de las capturas/comprobaciones: 96. Esto acredita configuración y recursos cargados, no una prueba perceptual del desenfoque en todos los equipos.
- Se construyó e inspeccionó el XML de la notificación con logo y texto que contiene caracteres escapables. No se envió una notificación del sistema para realizar esta prueba.
- RenderTargetBitmap sigue siendo útil para texto, iconos y layout, pero no captura Desktop Acrylic/Mica. Las zonas transparentes de esos PNG no se deben presentar como el aspecto del escritorio detrás de la ventana.
- La publicación self-contained contiene exactamente un ejecutable. Ese paquete final pasó las 50 entradas del verificador ampliado con salida 0 y Passed=true, incluidos los archivos de logo/ICO accesibles tras extraer el bundle. Informe local: .audit-artifacts/native-materials-package.json. El XML de notificación se verificó sin enviarla y no se declara aceptación visual de un toast real.

Las salidas locales se guardan en .audit-artifacts/native-materials*. No se midieron FPS, latencia de GPU ni un A/B energético entre Mica y Acrylic. Acrylic muestra contenido vivo detrás de la ventana y puede costar más composición que Mica; se usa el material del sistema por la transparencia solicitada, sin efectos manuales por tarjeta ni bucles de dibujo de la aplicación. Pendiente aceptación visual sobre el escritorio real, otras escalas físicas y alto contraste.

Referencias oficiales: [materiales de Windows](https://learn.microsoft.com/en-us/windows/apps/develop/ui/materials), [iconos y escalado](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/icons), [logo de notificación](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.appnotifications.builder.appnotificationbuilder.setapplogooverride).
