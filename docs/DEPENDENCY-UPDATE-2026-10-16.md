# Dependencias estables — 2026-10-16

Actualización solicitada de todas las dependencias NuGet, incluidas las transitivas. No se usan versiones preview ni experimentales. No se cambia la semántica del motor ni la versión mínima de Windows.

| Componente | Antes | Ahora |
| --- | --- | --- |
| SDK mínimo en global.json | 10.0.100 | 10.0.402 |
| Microsoft.NET.Test.Sdk | 18.10.1 | 18.11.0 |
| Microsoft.ApplicationInsights (solo tests) | 2.23.0 | 3.2.0 |
| Microsoft.Web.WebView2 | 1.0.3719.77 | 1.0.4258.31 |
| Microsoft.Windows.AI.MachineLearning | 2.1.74 | 2.4.89 |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.4654 | 10.0.28000.2705 |
| Microsoft.Windows.SDK.BuildTools.MSIX | 1.7.251221100 | 1.7.260930100 |
| System.Numerics.Tensors | 9.0.0 | 10.0.13 |

Windows App SDK 2.5.1, Blake3 3.0.2 y MSTest Framework/Adapter 4.5.1 ya estaban en la última estable consultada.

Se fijan explícitamente las versiones transitivas que sus paquetes padre aún solicitan con mínimos anteriores. Las dependencias de test llevan `PrivateAssets="all"`; no pasan al producto. La actualización mayor de Application Insights incorpora una cadena más amplia de dependencias de test: Microsoft.Extensions/Bcl y System en 10.0.13, MSAL en 4.91.0, IdentityModel.Abstractions en 8.23.0, OpenTelemetry en 1.20.0 y PersistentStorage en 1.1.2. CodeCoverage y TestPlatform pasan a 18.11.0 mediante Test.Sdk.

Estas referencias explícitas se deben revisar en actualizaciones futuras: son overrides de versiones transitivas, no nuevas funcionalidades de la aplicación. No se añadió instrumentación ni configuración de telemetría al producto.

Fuentes de versiones: feed oficial https://api.nuget.org/v3/index.json y metadatos oficiales de .NET 10 en https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json. SDK instalado localmente 10.0.402, runtime 10.0.13. Las comprobaciones de paquetes se ejecutan con `dotnet list RepartoCopier.sln package --outdated --include-transitive --format json` y `--vulnerable --include-transitive --format json`.

## Validación ejecutada sobre las versiones finales

- Restore de la solución con RID win-x64 completado; consulta de versiones sin paquetes desactualizados, incluidos los transitivos, en los tres proyectos.
- Consulta de vulnerabilidades sin avisos reportados por el feed. No equivale a una garantía de ausencia de vulnerabilidades desconocidas.
- Tests Release: 282 correctos, 1 omitido, 0 fallos, 42 s. La omisión existente exige un filesystem capaz de almacenar a.txt y A.txt simultáneamente.
- WinUI Release win-x64 compiló sin errores ni advertencias; publish self-contained completó y se comprobó que la carpeta contiene únicamente RepartoCopier.WinUI.exe.
- El ejecutable publicado ejecutó --layout-check, cerró con código 0 y produjo Passed=true: preparación/ejecución en claro y oscuro, tres tamaños (540×320, 720×320 y 1200×720 DIP) y cuatro diálogos en ambos temas, a 96 DPI reales.
- El primer arranque dentro del sandbox falló con código -2147450721 sin producir informe; la repetición con acceso normal al escritorio pasó. No se cuenta aquel intento como validación exitosa ni se ha diagnosticado la causa exacta.
- git diff --check sin errores. Informes locales en .audit-artifacts/dependencies-complete.trx y dependencies-layout-validated.json.

Pendientes: CI del nuevo commit, aceptación manual de teclado/Narrator/alto contraste y otras escalas, y benchmarks/fallos físicos de almacenamiento. No se atribuye una mejora de throughput a actualizar bibliotecas.
