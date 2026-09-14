from pathlib import Path


def exact(path, old, new, count=1, label='replacement'):
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    actual = text.count(old)
    if actual != count:
        raise RuntimeError(f'{label}: expected {count}, found {actual} in {path}')
    p.write_text(text.replace(old, new), encoding='utf-8', newline='\n')

# 1) Remove a topology wrapper with zero consumers.
exact(
    'dotnet/RepartoCopier.Core/StorageIoProfile.cs',
    '''\npublic sealed record StorageDeviceProfile(\n    StorageDeviceInfo Device,\n    StorageIoProfile Io)\n{\n    public static StorageDeviceProfile Create(StorageDeviceInfo device) =>\n        new(device, StorageIoProfile.For(device));\n\n    public string DeviceId => Device.PhysicalDeviceId;\n}\n''',
    '\n',
    label='remove dead StorageDeviceProfile')

# 2) Remove scheduler APIs kept only for tests; tests now exercise the real reservation path.
exact(
    'dotnet/RepartoCopier.Core/DeviceScheduler.cs',
    '''\n    // Kept for focused scheduler tests/diagnostics. Production FAN-OUT uses the\n    // reservation APIs above so the target is actually enforced.\n    public void NoteQueuedBytes(int bytes)\n    {\n        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);\n        var queued = Interlocked.Add(ref _queuedBytes, bytes);\n        UpdateMax(ref _peakQueuedBytes, queued);\n    }\n\n    public void NoteDequeuedBytes(int bytes) => ReleaseBacklog(bytes);\n''',
    '',
    label='remove test-only scheduler APIs')
exact(
    'dotnet/RepartoCopier.Core.Tests/DeviceSchedulerTests.cs',
    '''    public void QueueTelemetryTracksPeakWithoutEnforcingYet()\n    {\n        using var scheduler = new DeviceScheduler("PhysicalDisk3", 1, 16L * 1024 * 1024);\n\n        scheduler.NoteQueuedBytes(8 * 1024 * 1024);\n        scheduler.NoteQueuedBytes(4 * 1024 * 1024);\n        scheduler.NoteDequeuedBytes(8 * 1024 * 1024);\n\n        Assert.AreEqual(4L * 1024 * 1024, scheduler.QueuedBytes);\n        Assert.AreEqual(12L * 1024 * 1024, scheduler.PeakQueuedBytes);\n    }\n''',
    '''    public void RealBacklogReservationsTrackCurrentAndPeakBytes()\n    {\n        using var scheduler = new DeviceScheduler("PhysicalDisk3", 1, 16L * 1024 * 1024);\n\n        Assert.IsTrue(scheduler.TryReserveBacklog(8 * 1024 * 1024));\n        Assert.IsTrue(scheduler.TryReserveBacklog(4 * 1024 * 1024));\n        scheduler.ReleaseBacklog(8 * 1024 * 1024);\n\n        Assert.AreEqual(4L * 1024 * 1024, scheduler.QueuedBytes);\n        Assert.AreEqual(12L * 1024 * 1024, scheduler.PeakQueuedBytes);\n    }\n''',
    label='migrate scheduler telemetry test to real API')

# 3) Remove legacy SessionStore. WinUI has one active .rcopy serializer; SessionStore had no product consumer.
storage = Path('dotnet/RepartoCopier.Core/Storage.cs')
text = storage.read_text(encoding='utf-8')
marker = '\npublic static class SessionStore\n'
pos = text.find(marker)
if pos < 0:
    raise RuntimeError('SessionStore marker not found')
storage.write_text(text[:pos].rstrip() + '\n', encoding='utf-8', newline='\n')

core_tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
text = core_tests.read_text(encoding='utf-8')
start = text.find('    [TestMethod]\n    public void SessionFormatRoundTripsUnicodeAndUnc()')
if start < 0:
    raise RuntimeError('SessionStore test start not found')
end = text.find('    [TestMethod]\n    public void AtomicStorageReplacesWithoutLeavingSiblings()', start)
if end < 0:
    raise RuntimeError('SessionStore test end not found')
text = text[:start] + text[end:]
core_tests.write_text(text, encoding='utf-8', newline='\n')

# 4) Remove recovery helper used only by a test; exercise the real checkpoint writer instead.
recovery = Path('dotnet/RepartoCopier.Core/Recovery.cs')
text = recovery.read_text(encoding='utf-8')
start = text.find('    internal static void AppendDurable(')
if start < 0:
    raise RuntimeError('AppendDurable start not found')
end = text.find('    internal static Dictionary<string, byte[]> LoadManifestHashes', start)
if end < 0:
    raise RuntimeError('AppendDurable end not found')
text = text[:start] + text[end:]
start2 = text.find('    private static void AppendAndFlush(')
if start2 < 0:
    raise RuntimeError('AppendAndFlush start not found')
end2 = text.find('    private static FileStream OpenNewDurable', start2)
if end2 < 0:
    raise RuntimeError('AppendAndFlush end not found')
text = text[:start2] + text[end2:]
recovery.write_text(text, encoding='utf-8', newline='\n')
exact(
    'dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs',
    '        RecoveryManager.AppendDurable(destination, file, hash);\n',
    '''        using (var writer = new RecoveryCheckpointWriter(destination))\n        {\n            writer.Append(file, hash);\n            writer.FlushCheckpoint();\n        }\n''',
    label='use production checkpoint writer in recovery format test')

# 5) Remove production cache reset hook that existed only for one test.
exact(
    'dotnet/RepartoCopier.Core/StoragePreallocationPolicy.cs',
    '\n    internal static void ClearCacheForTests() => VolumePolicy.Clear();\n',
    '\n',
    label='remove preallocation test hook')
exact(
    'dotnet/RepartoCopier.Core.Tests/StoragePreallocationPolicyTests.cs',
    '        StoragePreallocationPolicy.ClearCacheForTests();\n',
    '',
    label='remove preallocation cache reset call')

# 6) Fix comments that still described 16 MiB after the 32 MiB block migration.
exact('dotnet/RepartoCopier.Core/FanoutPerformancePolicy.cs',
      '    private const long UsbFlashBacklog = 128L * MiB;     // 8 x 16 MiB\n',
      '    private const long UsbFlashBacklog = 128L * MiB;     // 4 x 32 MiB\n',
      label='fix USB flash block comment')
exact('dotnet/RepartoCopier.Core/FanoutPerformancePolicy.cs',
      '    private const long SataSsdBacklog = 256L * MiB;      // 16 x 16 MiB\n',
      '    private const long SataSsdBacklog = 256L * MiB;      // 8 x 32 MiB\n',
      label='fix SATA SSD block comment')

# 7) Current docs: describe the active pipeline, not superseded implementations.
readme = '''# RepartoCopier

RepartoCopier es una aplicación de escritorio para Windows que copia un origen hacia múltiples destinos simultáneamente mediante FAN-OUT, preservando la estructura completa de carpetas y priorizando integridad, recuperación y comportamiento nativo del sistema operativo.

## Versión actual

**RepartoCopier v2.0.0** es el baseline C#/.NET/WinUI del proyecto. `main` contiene además la evolución de rendimiento posterior a v2.0.0. La versión histórica v1.4.3 permanece publicada sin modificaciones.

## Plataforma

- C# y .NET 10
- WinUI 3 + XAML
- Windows App SDK 2.4
- Windows 10 2004 (19041) o posterior
- x64

## Arquitectura

`RepartoCopier.WinUI` contiene exclusivamente la interfaz Windows. Usa controles WinUI, recursos de tema/acento, escalado DPI del sistema, focus/teclado y pickers nativos.

`RepartoCopier.Core` contiene planificación, preflight, FAN-OUT, recuperación transaccional, telemetría, BLAKE3 para SkipSame/recovery y verificación post-copia automática mediante CRC32 por bloques. Las llamadas Win32 se mantienen aisladas en las rutas que requieren semántica de almacenamiento no expuesta directamente por las APIs de alto nivel.

## Invariantes de copia

- FAN-OUT es el único modo de copia.
- Una carpeta seleccionada se replica incluyendo su carpeta raíz.
- Se conserva exactamente la estructura de directorios, incluidas carpetas vacías.
- Un archivo seleccionado copia únicamente ese archivo.
- Los archivos adicionales del destino no se eliminan.
- El estado interno vive fuera del árbol copiado en `.disk-duplicator-state`.
- Los destinos recién escritos se verifican automáticamente después de la copia.
- Recovery y SkipSame conservan sus pruebas BLAKE3 independientes.
- Symlinks, junctions, reparse points y solapamientos peligrosos se rechazan de forma fail-closed.

## Rendimiento

El hot path actual usa bloques compartidos de 32 MiB, prefetch acotado, presupuesto global de RAM, backpressure y scheduler por dispositivo físico. En orígenes SSD locales con identidad exacta y alineación conocida existe una ruta Direct I/O `NO_BUFFERING + SEQUENTIAL_SCAN + OVERLAPPED`; la verificación usa el mismo principio cuando es elegible. Las escrituras de destino siguen siendo buffered con offsets explícitos y QD2 selectivo para SSD calificados; Direct I/O de escritura todavía no está implementado.

La telemetría de `CopyJob.DiagnosticsSnapshot()` permite identificar el cuello real antes de modificar parámetros. La hoja de ruta vigente está en `docs/FANOUT-PERFORMANCE-ROADMAP.md`.

## Compilar

```powershell
dotnet restore RepartoCopier.sln
dotnet test dotnet/RepartoCopier.Core.Tests/RepartoCopier.Core.Tests.csproj -c Release
dotnet build dotnet/RepartoCopier.WinUI/RepartoCopier.WinUI.csproj -c Release -r win-x64
```

El protocolo de validación y benchmark físico se mantiene en `TESTING.md`.
'''
Path('README.md').write_text(readme, encoding='utf-8', newline='\n')

testing = Path('TESTING.md').read_text(encoding='utf-8')
testing = testing.replace('- **Verificación:** repetir con verificación física habilitada.', '- **Verificación:** la aplicación verifica automáticamente; medir por separado copia, verificación y tiempo total. Los tests internos pueden desactivar `Verify` solo para aislar la fase de copia.')
testing = testing.replace('- `VerifyHashTime` dominante: revisar concurrencia CPU; `VerifyReadTime` dominante: el límite es lectura física de destinos.', '- `VerifyHashTime` dominante: el CRC32 es el cuello de CPU y debe optimizarse antes de aumentar I/O; `VerifyReadTime` dominante: el límite es lectura física de destinos.')
Path('TESTING.md').write_text(testing, encoding='utf-8', newline='\n')

roadmap = '''# FAN-OUT Performance Audit & Roadmap

## Objetivo

Igualar o superar el comportamiento de ExtremeCopy en FAN-OUT sobre destinos físicos independientes sin debilitar integridad, recovery, cancelación, memoria acotada ni seguridad de topología. El rendimiento solo se considera demostrado mediante A/B reproducible en hardware Windows físico.

## Arquitectura vigente en `main`

- FAN-OUT único: una lectura del origen alimenta a todos los destinos activos mediante `SharedBlock` con conteo de referencias.
- Bloque grande: **32 MiB**.
- Prefetch físico máximo: **8** bloques; pipeline de hash: **4**.
- BLAKE3 del origen/SkipSame/recovery permanece separado de la verificación post-copia.
- Lectura principal SSD elegible: Direct I/O con buffers alineados y `NO_BUFFERING | SEQUENTIAL_SCAN | OVERLAPPED`, usando I/O async real.
- Verificación post-copia automática: CRC32 por bloque generado una sola vez durante FAN-OUT; read-back Direct I/O overlapped cuando es elegible y fallback buffered async cuando no lo es.
- Escritura actual: buffered, offsets explícitos, QD2 selectivo en SSD calificados. **No existe todavía Direct I/O de escritura.**
- Scheduler y backlog por dispositivo físico; identidad incierta se trata conservadoramente.
- Estado interno fuera del árbol copiado en `.disk-duplicator-state/<state_id>`.

## Ventanas vigentes por rama

| Rama | Backlog | QD máximo actual |
|---|---:|---:|
| Network | 32 MiB | 1 |
| Conservador/virtual/storage spaces | 64 MiB | 1 |
| Rotational HDD | 128 MiB | 1 |
| USB flash / USB SSD incierto | 128 MiB | 1 |
| USB SSD exacto | 256 MiB | 2 |
| SATA SSD | 256 MiB | 2 |
| NVMe | 512 MiB | 2 |

El backlog absorbe jitter; QD controla I/O físico simultáneo. No deben confundirse ni aumentarse sin evidencia.

## Cerrado e integrado

- Topología física e identidad con política conservadora cuando no puede demostrarse el disco real.
- QD2 selectivo desde archivos de 8 MiB en SSD elegibles.
- Buffers de origen alineados y Direct I/O.
- H-10: la lectura Direct I/O del origen usa `OVERLAPPED`/async real; se eliminó la sesión síncrona.
- H-11/H-12/H-13: se eliminaron la verificación antigua escondida en `HashFileAsync`, APIs síncronas obsoletas, ramas nulas/test-only innecesarias y telemetría de verify que ya no tenía productor.
- Verificación automática CRC32 con `FastVerificationReader` como única ruta post-copia.

## Prioridad actual

### P0 — eliminar head-of-line entre ramas diferidas

`DeliverDataAsync` entrega primero a las ramas con crédito inmediato, pero después espera la lista `deferred` secuencialmente. Si A sigue saturada y B ya tiene crédito, B todavía queda detrás de A.

Corrección requerida:
- reservas de backlog de ramas diferidas independientes;
- entregar cada referencia del bloque tan pronto como su propia rama tenga crédito;
- conservar orden dentro de cada destino, conteo de referencias, límites de memoria, cancelación y aislamiento de fallos.

Gates:
- dos ramas diferidas liberadas en orden inverso;
- la rama lista primero recibe el bloque primero;
- cancelación/fallo no fuga backlog ni referencias.

### P1 — Direct I/O selectivo de escritura

Implementar solo después de cerrar P0 y sin sustituir ciegamente la ruta buffered. Requisitos:
- elegibilidad por topología/media/alineación;
- `NO_BUFFERING | OVERLAPPED` y offsets/buffers alineados;
- tail correcto;
- fallback cerrado solo para errores compatibles;
- ruta buffered actual permanece como fallback, no como segunda política contradictoria;
- telemetría que demuestre activación/fallback.

### P1 — acelerar CRC32

`FastCrc32` sigue siendo tabla byte-a-byte. Optimizar únicamente con equivalencia IEEE CRC32 probada (por ejemplo slicing-by-8/16 o una ruta intrínseca validada) y medir CPU vs `VerifyReadTime`.

### P1 — QD4 adaptativo

No subir QD globalmente. Evaluar QD4 solo en SSD/NVMe/USB-SSD exactos después de P0 y Direct I/O de escritura, con rollback automático/política conservadora si throughput o latencia empeoran.

### P1 — mismo dispositivo físico origen/destino

Coordinar prefetch de origen y escritura cuando comparten el mismo disco, especialmente HDD, para evitar seek thrash. Mantener QD1 conservador hasta tener una política compartida medida.

### P2 — BLAKE3 y metadata/durabilidad

Optimizar BLAKE3, flush/commit o small-file metadata solo si la telemetría demuestra que dominan. No debilitar recovery para ganar un benchmark.

## Benchmark físico contra ExtremeCopy

Solo después de cerrar P0 y las mejoras seleccionadas. Usar mismo origen, destinos, dataset y opciones, con datos suficientemente grandes para superar cachés transitorias. Registrar:

- tiempo de copia, verificación y total;
- MB/s del origen y de cada destino;
- throughput lógico agregado;
- CPU/RAM;
- `SourceRead`, `SourceHash`, `FanoutWait`, `QueueWait`, `Write`, `VerifyRead`, `VerifyHash`;
- backlog/QD por dispositivo y fallbacks Direct I/O;
- topología/bus/media reales.

La meta mínima es paridad reproducible con ExtremeCopy. “Arquitectura parecida” o CI hospedado no cuentan como prueba de rendimiento físico.

## Regla permanente de unificación

Una optimización no está terminada cuando aparece una ruta nueva. Se cierra solo cuando:
1. el consumidor real usa la nueva ruta;
2. se rastrean y migran todos los consumidores equivalentes;
3. se elimina código, parámetros, telemetría y tests obsoletos;
4. no quedan dos implementaciones del mismo propósito salvo un fallback explícito;
5. existe un gate que impide reintroducir la arquitectura retirada;
6. suite completa + WinUI Release pasan.
'''
Path('docs/FANOUT-PERFORMANCE-ROADMAP.md').write_text(roadmap, encoding='utf-8', newline='\n')

# Preserve v2.0.0 as history; add current main state above it.
change = Path('CHANGELOG.md')
text = change.read_text(encoding='utf-8')
if '## Unreleased — main' not in text:
    text = text.replace('# Changelog\n\n', '''# Changelog\n\n## Unreleased — main\n\n- Lectura Direct I/O del origen unificada en una única ruta overlapped/async con buffers alineados y fallback seguro.\n- Verificación post-copia automática migrada a CRC32 por bloques con read-back overlapped cuando el dispositivo es elegible.\n- Bloque FAN-OUT grande consolidado en 32 MiB; backlog y comentarios de política alineados con ese tamaño.\n- Eliminadas rutas síncronas/verify antiguas, telemetría sin productor, wrappers sin consumidor y APIs mantenidas únicamente para tests.\n- La escritura de destino continúa buffered con offsets explícitos; Direct I/O de escritura permanece como trabajo futuro y no se declara implementado.\n\n''')
change.write_text(text, encoding='utf-8', newline='\n')

print('Full unification cleanup prepared')
