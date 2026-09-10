# RepartoCopier — checklist de pruebas de campo

Este documento define las pruebas manuales que deben ejecutarse sobre hardware real antes de publicar una nueva release.

## Preparación

- Usar un origen de prueba con archivos grandes, archivos pequeños, subdirectorios y directorios vacíos.
- Incluir al menos un archivo suficientemente grande para mantener una transferencia activa durante varios segundos.
- Usar dos o más destinos para comprobar aislamiento FAN-OUT.
- Activar **Verificar BLAKE3** en al menos una ronda completa.
- Guardar una copia de los hashes BLAKE3 del origen antes de empezar.

## A. Copia FAN-OUT normal

1. Seleccionar el mismo origen y dos o más destinos.
2. Ejecutar hasta finalizar.
3. Confirmar que todos los destinos terminan en `Done`.
4. Confirmar que los directorios vacíos existen en cada destino.
5. Comparar hashes de los archivos del destino contra el origen.
6. Confirmar que no se crea metadata operacional dentro del árbol copiado.

**Éxito:** contenido y estructura equivalentes en todos los destinos y estado final correcto.

## B. Desconexión de un destino

1. Iniciar copia hacia al menos dos destinos físicos independientes.
2. Durante una escritura activa, desconectar uno de los destinos USB.
3. No tocar los demás destinos.
4. Observar el estado individual de cada destino.

**Éxito:**

- el destino desconectado termina en `Failed` dentro del timeout correspondiente a la fase de I/O;
- los destinos sanos continúan cuando está habilitado **Continuar con errores**;
- ningún archivo posterior al fallo se registra como completado para el destino muerto;
- la aplicación no queda bloqueada esperando indefinidamente al destino desconectado.

## C. Cancelación y reanudación

1. Iniciar una copia con varios archivos.
2. Cancelar aproximadamente a mitad del trabajo.
3. Cerrar normalmente la aplicación.
4. Volver a abrirla y ejecutar el mismo origen y destino.

**Éxito:**

- los archivos ya comprometidos y validados se conservan;
- un archivo interrumpido a mitad de escritura puede reiniciarse desde cero;
- la reanudación es por archivo confirmado, no por offset de bytes;
- no se marca como completado ningún `.part` incompleto;
- `completed.jsonl` solo conserva entradas físicamente válidas.

## D. Recuperación tras cierre abrupto

1. Iniciar una copia.
2. Terminar el proceso de RepartoCopier desde Task Manager mientras un archivo está en transferencia.
3. Volver a iniciar RepartoCopier con el mismo origen y destino.
4. Completar la copia.

**Éxito:**

- los `.part` obsoletos son limpiados por el preflight;
- si existe un backup pendiente, se restaura o limpia de acuerdo con el estado físico del archivo final;
- `completed.jsonl` y `manifest.b3` no permiten omitir un archivo cuyo contenido ya no coincide;
- el resultado final pasa la verificación BLAKE3.

## E. Mutación del origen

1. Iniciar una copia de un archivo grande.
2. Modificar, reemplazar o truncar un archivo del origen durante la ejecución.

**Éxito:** la corrida no termina declarando completo un destino basado en un snapshot de origen que ya cambió.

## F. Carpeta con solo directorios vacíos

1. Crear un origen sin archivos, con varios niveles de directorios vacíos.
2. Copiar a uno o más destinos.

**Éxito:** estructura preservada, destino en `Done` y progreso mostrado en 100%.

## G. Pausa y velocidad reciente

1. Iniciar una transferencia sostenida.
2. Pausar durante varios segundos.
3. Reanudar.

**Éxito:** la velocidad reciente cae durante la pausa y vuelve a reflejar rápidamente la transferencia real tras reanudar, sin quedar dominada durante minutos por el promedio histórico.

## H. Repetición sobre destino ya completo

1. Completar una copia con BLAKE3 activo.
2. Ejecutar otra vez sin modificar origen ni destino.

**Éxito:** el estado persistido y el manifest permiten reconocer archivos ya válidos sin comprometer integridad.

## Evidencia mínima a guardar

Para cada ronda registrar:

- commit exacto probado;
- SHA256 del `RepartoCopier.exe` usado;
- tipo de almacenamiento de origen y destinos;
- resultado de cada destino;
- mensajes de error visibles;
- hashes BLAKE3 de una muestra representativa o de todo el conjunto de prueba;
- cualquier `.part`, `.bak`, `completed.jsonl` y `manifest.b3` relevante después de una interrupción.

No debe publicarse una release basándose únicamente en CI: las pruebas de desconexión, cancelación y recuperación requieren hardware/Windows real.
