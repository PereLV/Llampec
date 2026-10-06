# Investigación futura: modo táctil global

**Estado actualizado el 2026-10-03: aparcada.** El usuario ha reafirmado que
Llampec debe acercar ajustes existentes de forma sencilla y ligera, sin cambios
profundos en Windows. Las propuestas de experimentos que siguen son antecedentes
de investigación, no un plan activo. Solo reconsiderar esta función si aparece
una vía compatible con ese criterio.

Fecha de consulta: 2026-10-02. Trabajo posterior e independiente de
**0.2.0-alpha.7**. La publicación de esa versión no depende de este experimento.
Este documento amplía [Tablet controls](TABLET.md): no implementa un control,
no ejecuta un prototipo y no cambia ajustes, dispositivos ni servicios.

La hipótesis más útil para investigar es un cambio temporal de **Convertible
Slate Mode (CSM)**. Hay evidencia de que Windows y algunas aplicaciones consumen
esa señal, pero todavía no se ha probado su escritura en esta Surface ni su efecto
en Edge. No se ha encontrado una API pública de usuario que ofrezca el selector
global fiable que queremos para Llampec.

## Qué significa «global»

El objetivo es poder pedir una interfaz cómoda para ratón o para tacto desde
Llampec, manteniendo el funcionamiento táctil de la pantalla. No equivale a
desactivar el digitalizador, alterar la escala/DPI o cambiar solo la barra.

Microsoft confirma que Windows 11 decide automáticamente el modo tableta según
el teclado y la postura, sin selector manual. Su API antigua
`UIViewSettings.UserInteractionMode` remite a CSM desde Windows 11. Eso justifica
investigar la señal compartida, pero no implica que todas las aplicaciones deban
presentarse igual: cada una decide cómo adaptar su interfaz.
[Soporte de Windows](https://support.microsoft.com/en-us/windows/hardware/input-devices/turn-tablet-mode-on-or-off-in-windows),
[contrato de UIViewSettings](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uiviewsettings.userinteractionmode?view=winrt-28000).

| Capa | Qué controla | Consecuencia para Llampec |
| --- | --- | --- |
| `ExpandableTaskbar` | Preferencia de barra táctil del usuario | Ya implementada; no simula un teclado conectado. |
| `ConvertibleSlateMode` | Postura/teclado accesible que comunica el equipo | Candidato a experimento global, no preferencia pública de usuario. |
| `ConvertibilityEnabled`, DeviceForm, SMBIOS | Clasificación del dispositivo | No usarlos para alternar la postura momentánea. |
| Preferencia táctil de una aplicación | Presentación de esa aplicación | Puede sobreponerse a la detección automática. |

## CSM: candidato, no setter soportado para aplicaciones

Microsoft documenta
`HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl\ConvertibleSlateMode`:
0 indica que no hay teclado físico accesible; 1 indica modo portátil. El driver
OEM debe actualizarlo al cambiar la postura. La documentación lo relaciona con
el teclado virtual y la interfaz táctil; no lo presenta como una preferencia
que cualquier aplicación deba escribir.
[ConvertibleSlateMode](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/microsoft-windows-gpiobuttons-convertibleslatemode).

`GetSystemMetrics(SM_CONVERTIBLESLATEMODE)` consulta el estado. Windows notifica
los cambios mediante `WM_SETTINGCHANGE` y `ConvertibleSlateMode`. Enviar esa
notificación no cambia por sí mismo el estado. La documentación revisada de
`SystemParametersInfo` no incluye `SPI_SETCONVERTIBLESLATEMODE`; tampoco se ha
identificado un `SetSystemMetrics` público para esta función. Esto delimita la
API encontrada, no demuestra que no existan mecanismos internos.
[GetSystemMetrics](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetrics),
[SystemParametersInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow),
[notificaciones GPIO](https://learn.microsoft.com/en-us/windows-hardware/drivers/gpiobtn/interface-implementation-guidance).

Hay evidencia reciente de observación del registro por Explorer. El equipo de
soporte de Microsoft Japón documentó en septiembre de 2026 un caso de Windows
24H2/25H2 donde una transición de CSM se interpreta como cambio de postura y
afecta a `ConvertibleSlateModeChanged`. Es evidencia de un consumidor real,
no una garantía para 26H1 ni un contrato de control manual. No aplicar a la Surface
las instrucciones del artículo para corregir portátiles mal clasificados.
[Microsoft Windows Support, 2026-09-14](https://jpwinsup.github.io/blog/2026/09/14/Shell/MistakesLaptopForTablet/MistakesLaptopForTablet/).

El driver sigue siendo dueño de la postura física. Microsoft pide refrescarla
tras arranque, suspensión e hibernación y señala que cargar el driver GPIO puede
sobrescribir la configuración inicial. Un prototipo no debe competir con él
reescribiendo valores continuamente.
[Implementación del indicador](https://learn.microsoft.com/en-us/windows-hardware/drivers/gpiobtn/indicator-implementation).

## Alternativas investigadas y límites

**Inyección GPIO.** Existe una interfaz documentada
`GUID_GPIOBUTTONS_LAPTOPSLATE_INTERFACE`. `WriteFile` alterna el indicador y el
handle obtiene acceso exclusivo para evitar proveedores en conflicto. Es una vía
de integración de drivers/OEM; conseguir un handle no equivale a disponer de una
operación pública de usuario ni demuestra que pueda convivir con el controlador
de Surface. La exclusividad añade un problema de propiedad y el toggle exige
sincronizar el estado. No se ha enumerado ni abierto esta interfaz en el equipo.
[Interfaces GPIO](https://learn.microsoft.com/en-us/windows-hardware/drivers/gpiobtn/available-interfaces-and-related-apis),
[diseño de integración OEM](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/continuum#method-2---use-the-injection-interface).

**COM privado de ImmersiveShell.** `ITabletModeController.GetMode/SetMode` aparece
en un ejemplo de su autor probado en Windows 10 21H1. Esa publicación es evidencia
primaria de aquel experimento, no documentación oficial de una API compatible
con Windows 11. No se encontró un contrato actual de Microsoft para usarlo como
control global. Ni activar el objeto ni obtener éxito de una llamada probarían
que haya cambiado CSM o la presentación de otras aplicaciones. No se ha llamado
a esta interfaz; queda descartada como base del primer prototipo.
[Ejemplo original en Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/755947/is-there-a-api-that-can-enter-exit-tablet-mode-in).

**Clasificación OEM y overrides de Explorer.** `ConvertibilityEnabled` sustituye
la clasificación de SMBIOS/DeviceForm; no representa la postura actual.
`TabletPostureTaskbar` tampoco tiene un contrato público encontrado para gobernar
las aplicaciones. La existencia de esas claves no justifica convertirlas en un
selector global. No cambiar ninguna de ellas en el prototipo CSM.
[Configuración de convertibilidad](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/settings-for-better-tablet-experiences).

## Edge y diferencias entre aplicaciones

El equipo de Edge anunció **Touch Mode** en Apariencia en el canal Dev 117.
Por tanto, existe una decisión propia del navegador además de la señal del
sistema. La ubicación, los valores y el comportamiento de la versión instalada
siguen sin comprobarse; no asumir que una barra grande demuestra el estado global.
[Anuncio oficial de Edge Dev 117](https://techcommunity.microsoft.com/discussions/edgeinsiderdiscussions/dev-channel-update-to-117-0-2024-1-is-live/3895558).

Una nota oficial de Dev 110 también anunció una política Touch Mode, advirtiendo
que documentación y plantillas podían no estar actualizadas. En el catálogo
actual consultado no se encontró `TouchMode`. Falta identificar un nombre,
contrato y soporte vigentes antes de considerar esa vía; no crear políticas
supuestas ni editar archivos del perfil del navegador. Aunque existiera una
política válida, controlaría Edge, no todo Windows.
[Anuncio Dev 110](https://techcommunity.microsoft.com/discussions/edgeinsiderdiscussions/dev-channel-update-to-110-0-1587-6-is-live/3708590),
[catálogo actual de políticas](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies).

El código de Chromium consultado añade una limitación concreta: en Windows 11
combina convertibilidad, uso como tableta y topología de pantallas. Su comprobación
de topología busca pantalla interna activa y ausencia de escritorio extendido;
además almacena en caché la clasificación `ConvertibilityEnabled`. Es posible
que cambiar solo CSM no active una UI táctil en determinadas configuraciones.
Esto está probado por lectura del código de Chromium, no por ejecución local
ni por acceso al código propio de Edge.
[Chromium: detección de modo tableta](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/base/win/win_util.cc).

## Lo observado realmente en esta sesión

En una fase anterior, con la Surface en postura tableta, se leyeron CSM=0,
`ConvertibilityEnabled=1`, `ImmersiveShell\TabletMode=0` y los valores TabletTip
`ConvertibleSlateModeChanged=1` y `ConvertibleChassis=1`. Son observaciones
históricas, no una lectura del estado actual: después se conectó físicamente el
teclado durante las pruebas de orientación de alpha.7.

La comprobación de permisos fue **abrir la subclave con permiso de escritura**
mediante `OpenSubKey(..., writable: true)`. Devolvió
`WritableWithoutElevation: false` y un error cuyo fragmento conservado es
«Acceso denegado al Registro solicitado.». No se conserva aquí el prefijo completo
de la excepción. **No se ejecutó `SetValue` ni se intentó escribir 0 o 1.**
El resultado prueba la limitación del contexto de permisos de aquella sesión;
no prueba que Windows rechace un cambio CSM autorizado con otro contexto.

El intento de abrir `edge://settings/appearance` mediante Browser Use fue
rechazado por la política de URL de la herramienta: protocolo solicitado no
permitido; solo se admitían `http:` y `https:`. El rechazo prohibía buscar rutas
alternativas y se respetó. No se inspeccionó ni cambió el ajuste de Edge mediante
otra superficie. Esto es un límite de la herramienta, no un fallo de Edge ni una
prueba de incompatibilidad con CSM.

Las comprobaciones reales de orientación y de barra táctil descritas en
[TABLET.md](TABLET.md#validation) no validan un cambio de modo táctil global.
Esta investigación adicional ha sido de lectura de fuentes y documentación.

## Prototipo futuro propuesto, sin implementar

Primero resolver con un experimento corto si la escritura CSM tiene efecto útil.
No añadir todavía el botón a producción ni prometer los estados
«Siempre / Nunca / Automático»: escribir un valor de hardware una vez no define
por sí solo una preferencia persistente ni cómo volver a la detección automática.

1. Registrar versión de Windows/firmware, postura física, pantallas activas,
   valor/tipo CSM, métrica CSM, flags de autorrotación y preferencia de barra.
   Registrar la preferencia real de Edge mediante una vía permitida cuando esté
   disponible. Comparar al menos Explorer, Edge en automático y otra aplicación.
2. Usar una sesión local con postura física estable. Si es necesario elevar,
   preparar antes un ejecutable de prueba con una única operación fija sobre CSM,
   sin rutas, nombres de valores ni comandos arbitrarios. No relajar ACL, elevar
   todo Llampec ni instalar un servicio permanente. Si no hay un contexto
   autorizado adecuado, detener la escritura y conservar el diagnóstico.
3. Guardar un registro de recuperación tipado antes de escribir. Admitir solo
   una clave existente de tipo DWORD con valor 0 o 1; no crear estado OEM ausente.
   Cambiar una sola vez al valor opuesto y observar durante un intervalo corto.
   Leer registro y métrica por separado, medir latencia y capturar la UI. Si hace
   falta probar `WM_SETTINGCHANGE`, registrarlo como un segundo paso distinto.
4. Restaurar y verificar el valor original antes de concluir. La recuperación
   del prototipo debe tener un plazo máximo y sobrevivir al cierre accidental
   de su ventana. Esa temporización pertenece al experimento, no al selector de
   orientación ya publicado.
5. Si cambia la postura física, aparece otra sesión, falla una lectura o el
   driver reescribe CSM, terminar la intervención. No reimponer el valor de prueba.
   Tampoco sobrescribir automáticamente una señal nueva con una instantánea
   antigua: registrar el conflicto y verificar la postura real con el usuario.
6. Solo después de un ciclo reversible correcto, diseñar pruebas específicas de
   teclado, dock, monitores y suspensión. No automatizar esos cambios dentro del
   primer experimento ni reiniciar Explorer o aplicaciones para ocultar fallos.

La restauración condicional no es suficiente si solo se compara el mismo bit:
el driver puede producir un cambio intermedio y volver al valor del prototipo.
Determinar quién posee el estado exige eventos y evidencia física independiente
de la clave que se está sobrescribiendo. Esa decisión sigue abierta; no hay un
modo automático fiable implementado.

## Coste y criterios para continuar

| Alternativa | Coste para el producto | Criterio |
| --- | --- | --- |
| Diagnóstico de solo lectura | Bajo; lecturas puntuales y eventos | Útil para explicar diferencias sin alterar postura. |
| Escritura CSM temporal | Medio/alto: permisos, recuperación y conflictos con OEM | Continuar solo si registro, métrica y UI cambian y se restauran en vivo. |
| Preferencias por aplicación | Bajo para una guía; alto para automatizar muchos perfiles/versiones | Alternativa localizada, no presentarla como modo global. |
| COM privado de Explorer | Alto mantenimiento entre builds, sin contrato Win11 localizado | No usar como base de la función. |
| Driver/inyección GPIO | Alto: empaquetado, firma, instalación y convivencia con OEM | Fuera del primer prototipo y del alcance actual de Llampec. |

Para aceptar una función de uso diario debe responder sin reiniciar procesos,
cerrar sesión ni confirmar UAC en cada pulsación, respetar cambios de hardware y
seguir funcionando tras suspensión sin un bucle que luche con el driver. Debe
demostrar recuperación ante fallo y explicar qué aplicaciones siguen una
preferencia propia. Cualquier elevación futura requeriría una superficie mínima,
no permisos generales de administración.

Si solo cambia Explorer, necesita reinicios, degrada teclado virtual/autorrotación
o exige mantener un override del hardware permanentemente, conservar el control
de barra de alpha.7 y considerar opciones por aplicación. La siguiente decisión
es **autorizar y preparar ese prototipo acotado**, no incorporar el ajuste como
función estable basándose únicamente en documentación.
