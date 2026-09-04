# Copiador 1→N

Copia **archivos y carpetas** de un origen a varios destinos. No clona discos físicos.

- Un solo lector. Cada destino tiene su cola: un USB lento no frena a otro rápido
  mientras el origen aguante la suma de escrituras.
- BLAKE3 por archivo al vuelo. Verificación opcional (relee el destino).
- Sin administrador. Pausa y cancelar.

## Uso

1. Artifact `disk-duplicator.exe` (Actions).
2. Elige carpeta origen.
3. Agrega una o más carpetas destino.
4. Iniciar. No pongas un destino *dentro* del origen.
