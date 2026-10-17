# Revisión final conservadora — 2026-10-17

## Estado revisado

Rama `fix/ui-audit-polish`, código `dce0d59`. Árbol limpio al comenzar. Fetch de origin ejecutado; main continúa en `a5da5a2`. Esta revisión no modifica código productivo, dependencias ni configuración. No encontró nuevos defectos confirmados que justifiquen una limpieza o refactorización de la versión estabilizada. Esto no certifica ausencia de todos los errores ni rendimiento óptimo en cualquier hardware.

## Código sin uso y duplicación

- Roslyn: `dotnet format style` con `--verify-no-changes --diagnostics IDE0051 IDE0052 IDE0005 --severity info`, aplicado por separado a Core y WinUI. Ambos informes vacíos. La ejecución UI válida usa `Configuration=Release`; una primera carga Debug carecía del assembly Core y no se toma como evidencia semántica completa.
- Análisis adicional con `dotnet format analyzers`, diagnósticos IDE0051/IDE0052/CA1812/CA1823: sin cambios propuestos. Solo acredita los diagnósticos disponibles y habilitados; no demuestra que cada API pública tenga consumidores externos.
- Búsqueda complementaria de métodos privados con una sola aparición en los C#/XAML versionados: sin candidatos. Se incluyen referencias de tests y XAML para no eliminar manejadores de eventos ni fixtures como supuesto código muerto.
- Hash SHA256 de los C#/XAML versionados: ningún archivo idéntico duplicado.
- Comparación de ventanas de 20 líneas no vacías y sin comentarios, con al menos 500 caracteres, entre archivos de producción Core/WinUI: cero bloques idénticos entre archivos. No es una detección de toda duplicación semántica ni de fragmentos menores.
- Se conservan la compatibilidad de perfiles v1, migraciones de recovery, fallback Direct/buffered, checks de layout y comando source-bench: tienen función explícita y referencias. Las repeticiones breves de validación/rollback y manejo de errores no se unifican por semejanza superficial.

## Robustez y rendimiento

Revisión dirigida de admisión/cancelación, buffers compartidos, verificación de lecturas cortas, commit/backup, checkpoint y ciclo de vida de ventana/diálogo. Se mantiene la actualización UI a 250 ms en primer plano y 2 s minimizada, y el cambio de layout solo al cruzar su umbral. No se modifican pools, bloques, concurrencia, flush, hashes, prioridades ni política de archivos existentes. No hay una mejora de latencia medida en esta revisión.

**Pendiente conocido:** el PR #27 (`fix/engine-cancellation-cleanup`, `01e83c8`) sigue separado y sus cambios no están en esta rama ni en su ejecutable. Corrige tres problemas ya documentados: cancelación buffered envuelta en IOException, admisión de control con token previamente cancelado y cierre de un stream de recovery que impedía cerrar el otro. Esta revisión no debe interpretarse como aprobación de que esos problemas ya estén integrados. No se mezclan cambios de motor con el PR visual #26.

El commit atómico mantiene reintentos por sharing violation de hasta aproximadamente 5 s y no recibe token; la latencia real de una syscall depende del dispositivo/controlador. No se cambia este comportamiento estabilizado sin una prueba específica. Los resultados físicos USB/SATA/NVMe, desconexión real, ETW y consumo/composición Acrylic siguen fuera de esta validación.

## Validación ejecutada sobre esta revisión

- Restore Core.Tests y UI win-x64: correctos.
- Tests Release: **286 correctos, 1 omitido, 0 fallos**, total 287. El omitido exige crear nombres que solo difieren en mayúsculas en un filesystem sensible a ellas. Informe local: `.audit-artifacts/final-review-tests/final-review.trx`.
- Build WinUI Release win-x64: **0 errores, 0 advertencias**.
- Ejecución real `--layout-check`: **Passed=true, 50 entradas, salida 0**. Preparación/operación, seis tamaños, ambos temas, diálogos propios y transición del umbral responsive. Informe local: `.audit-artifacts/final-review-layout.json`.
- DPI real: 96. No se declara validación visual física a otras escalas ni que RenderTargetBitmap capture el backdrop del compositor. No se enviaron notificaciones ni se ejecutó apagado del equipo.
- `git diff --check`: correcto.

Los informes locales de análisis están en `.audit-artifacts/final-{core,ui}-analysis`, `final-core-style` y `final-ui-style-release`. El intento inicial dentro del sandbox falló al conectar el named pipe de Roslyn; se repitió correctamente con el acceso necesario. No se cuenta el intento fallido como validación.

Resultado: conservar el código productivo actual; no hay una eliminación de código o deduplicación demostrablemente necesaria en esta revisión. Revisar e integrar por separado el PR del motor antes de declarar una versión que incluya también esas correcciones.
