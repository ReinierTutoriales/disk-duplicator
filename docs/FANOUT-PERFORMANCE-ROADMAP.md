> **CONTINUIDAD:** el estado exacto de implementación, CI confirmado, invariantes y próximos pasos está en [FANOUT-WORK-STATE.md](FANOUT-WORK-STATE.md). Leer ese documento antes de retomar cambios en `feature/fanout-per-destination-spill`.

# FAN-OUT Performance Audit & Roadmap

## Fuente de verdad

Este documento conserva contexto histórico, criterios de rendimiento y decisiones retiradas. No describe la arquitectura productiva vigente.

La arquitectura actual, sus invariantes, el CI confirmado y el trabajo pendiente están en `FANOUT-WORK-STATE.md`. No duplicar aquí tablas de QD, tamaños de pool/spill, tamaño de bloque ni otras constantes productivas: mantener una sola fuente de verdad evita deriva documental.

## Objetivo

Igualar o superar ExtremeCopy en FAN-OUT sobre hardware Windows real. El criterio rector es simple: una lectura física del origen debe alimentar a todos los destinos capaces sin dividir artificialmente el throughput del source entre N ramas.

El rendimiento solo se considera demostrado mediante A/B reproducible en hardware físico. CI, tests de arquitectura y telemetría validan corrección y observabilidad; no demuestran throughput físico.

## Principios obligatorios

- Una optimización no se cierra porque el código nuevo exista: debe sustituir la ruta productiva anterior, migrar consumidores, eliminar código/rutas huérfanas, añadir el contrato necesario y pasar Core + WinUI Release.
- Los límites y políticas productivas se modifican únicamente con evidencia reproducible; no introducir adaptación dinámica ni eliminar límites de seguridad por intuición.
- Integridad, durabilidad, aislamiento entre destinos y liberación completa de recursos tienen prioridad sobre throughput.
- Después de cada migración se elimina la infraestructura temporal. La ruta productiva debe conservar una sola implementación clara.
- Antes de cambiar el hot path por rendimiento, medir el HEAD actual y aislar el cuello observado.

## Retirado

Iteraciones anteriores exploraron mecanismos como `PipelineGovernor`, `AdaptiveByteBudget`, `AdaptiveControlByteBudget`, QD adaptativo, `BranchReplayStore` y `AdaptiveTransferSizer`.

Esos mecanismos no forman parte del motor vigente de esta rama. Fueron retirados del camino productivo en favor de una arquitectura FAN-OUT más simple y acotada, evitando complejidad adaptativa, replay a disco y políticas no justificadas por mediciones físicas reproducibles.

No reintroducirlos por intuición. Cualquier mecanismo equivalente requiere primero demostrar con el baseline físico del motor actual qué cuello de botella resuelve y después medir el cambio contra exactamente el mismo escenario.

Los detalles históricos de implementación de esas rutas retiradas no se mantienen aquí como contratos productivos: hacerlo volvería a crear dos descripciones incompatibles del motor.

## Principio físico de FAN-OUT

La referencia conceptual es:

```text
velocidad_destino ≈ min(capacidad_origen, capacidad_destino, capacidad_enlace_compartido)
```

Una lectura del origen debe poder alimentar a todos los destinos que necesiten ese bloque sin dividir artificialmente el throughput por el número de ramas. La contención física real de disco, controlador, hub o enlace sí puede limitar destinos y debe distinguirse de una limitación introducida por el motor.

El tiempo total de un job en el que todos los destinos deben completar puede seguir determinado por el destino exitoso más lento. Eso no implica que los destinos rápidos deban escribir a la velocidad del más lento ni permanecer retenidos después de terminar.

## Escenario histórico de aceptación

Escenario de campo conservado como referencia: USB 3.0 + SATA HDD + SD PCIe + 2 NVMe, aproximadamente 28.24 GB.

En el motor anterior se observó aproximadamente 5:57 de duración y ~79 MB/s en NVMe. Estos valores son **anteriores a Parte A/B** y no constituyen el baseline del motor actual. No usarlos para atribuir un cuello de botella al HEAD vigente ni como prueba de la capacidad actual.

El comportamiento funcional buscado sigue siendo válido: un destino rápido debe poder terminar, verificar, cerrar sus recursos y quedar liberado mientras destinos más lentos continúan.

## Baseline físico vigente

El baseline válido debe obtenerse contra el HEAD actual mediante `PhysicalDiskPerformanceBenchmarkTests.MeasureRealSourceAndDestinationThroughput`.

La matriz, variables de entorno, métricas vigentes y reglas de comparación están definidas en `FANOUT-WORK-STATE.md`. Mantener origen, destinos, dataset, opciones y condiciones constantes entre baseline y cualquier A/B posterior.

Hasta completar ese baseline, no modificar por rendimiento el pipeline de lectura, QD, `DestinationWriteCoordinator`, pool compartido ni política de spill.

## Benchmark físico contra ExtremeCopy

Para una comparación externa, usar el mismo origen, destinos, dataset y opciones en ambos motores. Separar siempre throughput físico del source, throughput por destino y throughput agregado lógico.

Escenario conceptual mínimo:

```text
SOURCE HDD:      ~150 MB/s
DEST A capaz:    ~150 MB/s
DEST B capaz:    ~150 MB/s
DEST C capaz:    ~150 MB/s
DEST D capaz:    ~150 MB/s
Aggregate write: ~600 MB/s
Physical source: ~150 MB/s
```

Estas cifras ilustran la semántica FAN-OUT; no son un objetivo garantizado para hardware concreto. La meta de paridad o mejora frente a ExtremeCopy requiere evidencia física A/B. CI y arquitectura por sí solos no son prueba de rendimiento.

## Regla permanente de unificación

Una optimización se cierra únicamente cuando:

```text
[ ] La ruta productiva nueva reemplaza realmente a la anterior
[ ] Búsqueda completa de símbolos/rutas antiguas en dotnet/
[ ] Consumidores reales del símbolo nuevo verificados
[ ] Código/telemetría/parámetros huérfanos eliminados
[ ] Infraestructura temporal de migración eliminada
[ ] Contrato de arquitectura añadido cuando corresponda
[ ] Suite Core Release verde
[ ] WinUI Release x64 verde
[ ] Source gate + publish + artifact verdes
[ ] Documentación coherente con la ruta productiva final
```

Si alguno falla, el ítem permanece parcial.
