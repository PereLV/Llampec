# Investigación del uso de Surface como tablet

> Registro de la investigación inicial. La implementación y sus comprobaciones
> actuales se describen en [Tablet controls](TABLET.md); los pendientes de este
> documento reflejan el momento de la investigación.

Investigación del 2 de octubre de 2026 sobre barra de tareas, modo tablet y
rotación para el siguiente bloque de Llampec. El diseño de rotación queda
aceptado con pantalla interna por defecto y selección de otras pantallas en
las opciones ampliadas. Se añade una corrección local del botón de autoocultado;
el resto sigue pendiente de implementación y validación práctica.

La orientación manual tiene APIs públicas. El bloqueo de rotación tiene un
atajo oficial que conviene validar desde Llampec. Se descarta modificar los
iconos de la barra tablet al no encontrar una solución limpia. El usuario ha
identificado el ajuste: «Optimizar la barra de tareas para las interacciones
táctiles cuando este dispositivo se usa como tableta». Es una casilla de dos
estados; el futuro control de Llampec será activar/desactivar esa optimización.

Durante esta investigación se consultaron fuentes primarias y el estado local
del equipo. No se cambiaron ajustes de Windows, no se reinició Explorer, no se
giró la pantalla y no se hicieron commits ni publicaciones en GitHub.

## Resultado por función

| Función | Resultado | Propuesta para Llampec |
| --- | --- | --- |
| Iconos como en escritorio | Se puede desactivar la barra táctil desde Configuración. | Preferencia «Barra táctil», separada del autoocultado. Automatización por validar. |
| Barra plegable con iconos normales al expandirse | La segunda revisión tampoco encontró una solución limpia. | Fuera de alcance por decisión del usuario. |
| Autoocultado durante la barra táctil | La API actual no controla su plegado; el usuario confirma que no produce el efecto esperado. | Botón gris con motivo; recuperar disponibilidad con la barra normal sin cambiar la preferencia guardada. |
| Activar o desactivar barra táctil | El usuario confirma que se refiere a la casilla de optimización táctil. | Botón de dos estados; sustituye la propuesta inicial de tres modos. |
| Bloquear rotación | Lectura pública del estado; Win+O es el candidato para cambiarlo. | Botón con comprobación del resultado y motivo cuando no está disponible. |
| Elegir orientación | Viable mediante APIs públicas de pantalla. | Pantalla integrada por defecto; selección de otras pantallas en opciones ampliadas y recuperación del cambio. |
| Entender por qué no gira | Windows comunica varias causas de supresión; no todos los fallos del sensor. | Estado explicativo y diagnóstico local opcional. |

## Equipo observado

Lectura puntual, no una prueba de fiabilidad durante cambios físicos:

| Dato | Resultado |
| --- | --- |
| Modelo comunicado por WMI | Surface Pro 13in 12th Ed Snapdragon |
| Sistema | Windows 11 Home, 26H1, compilación 28000.2956, ARM64 |
| GetAutoRotationState | Consulta correcta, valor 0x0, AR_ENABLED |
| SM_CONVERTIBLESLATEMODE | 0, postura tablet/pizarra |
| SM_SYSTEMDOCKED | 0 |
| Pantallas activas según SM_CMONITORS | 1 |
| Sesión remota | No |
| Sensor de orientación en PnP | Simple Device Orientation Sensor, estado OK |
| Explorer Advanced | ExpandableTaskbar=1; TaskbarSmallIcons=0 |
| Capacidad convertible explícita | ConvertibilityEnabled=1 |
| AutoRotation en HKLM | Enable=1; SensorPresent=1; SlateEnable=1; LastOrientation=0 |

`TabletPostureTaskbar` no estaba presente en las ubicaciones consultadas de
Explorer y Explorer Advanced. Su ausencia no se interpreta como un tercer
valor numérico ni demuestra la existencia de un selector.

La API declara que la autorrotación está habilitada ahora. Esto no demuestra
que el sensor entregue datos correctos siempre ni explica un fallo pasado.
La orientación guardada en el registro tampoco sustituye a consultar el modo
activo de pantalla.

## Barra de tareas e iconos

La descripción del problema coincide con la barra optimizada para tablet de
Windows 11: tiene un estado contraído y otro expandido con iconos grandes.
Microsoft documenta su desactivación en Configuración > Personalización >
Barra de tareas > Comportamientos, desmarcando la optimización para
interacciones táctiles. [Configuración de la barra de tareas de Microsoft](https://support.microsoft.com/en-us/windows/experience/personalization/customize-the-taskbar-in-windows).

**Recomendación:** probar primero la barra normal con el teclado desacoplado.
Se recupera su presentación de escritorio, perdiendo el estado contraído
específico de la barra táctil. El autoocultado convencional, ya controlado por
Llampec, puede servir para liberar espacio; su experiencia no es idéntica.
El objetivo solicitado es el tamaño habitual de escritorio, por lo que no
hace falta reducir la escala de toda la pantalla.

Hay cambios recientes que requieren distinguir versiones. En 2025 Microsoft
documentó iconos pequeños con las opciones Nunca, Cuando esté llena y Siempre.
En mayo de 2026 anunció también menor altura y, en agosto, el ajuste «Tamaño
de la barra de tareas > Pequeño» para 24H2/25H2, con despliegue gradual. No se
ha comprobado su disponibilidad concreta ni su efecto sobre la barra táctil
expandida de esta Surface 26H1.
[Iconos pequeños en 2025](https://blogs.windows.com/windows-insider/2025/06/19/releasing-windows-11-build-26100-4482-to-the-release-preview-channel/),
[cambio de altura en 2026](https://blogs.windows.com/windows-insider/2026/05/15/improving-windows-quality-making-taskbar-and-start-more-personal/),
[KB5120998 de agosto de 2026](https://support.microsoft.com/en-us/servicing/os/windows-11/2026/08/kb5120998-windows-11-24h2-25h2-update).

La segunda revisión tampoco encontró una opción documentada para conservar la
barra plegable y configurar independientemente sus iconos expandidos. Hay
implementaciones que enganchan funciones privadas de Explorer y dependen de
sus estructuras internas; no cumplen el requisito de una solución limpia.
El usuario acepta dejar el tamaño tal como lo ofrece Windows, por lo que esta
personalización queda fuera de alcance. No se incorpora código ni dependencia
de esos proyectos. [Implementación original de Windhawk examinada](https://github.com/ramensoftware/windhawk-mods/blob/main/mods/taskbar-icon-size.wh.cpp).

## Autoocultado durante el uso como tablet

El usuario confirma que el botón actual de Llampec no produce el efecto
esperado mientras usa la barra táctil. Además aporta el texto de Configuración:
«Cuando la función de ocultar automáticamente la barra de tareas también está
seleccionada, se mostrará la barra de tareas optimizada para función táctil en
su lugar». Esta observación confirma la prioridad en su equipo. La llamada existente conserva
correctamente los bits de estado de la barra. La API pública ABM_SETSTATE
controla la preferencia de autoocultado y devuelve siempre TRUE, de modo que
ese retorno no demuestra un cambio visual. No expone un control del plegado
táctil. No se ha encontrado una corrección limpia que permita sustituir ese
comportamiento manteniendo la barra táctil.
[Contrato de ABM_SETSTATE](https://learn.microsoft.com/en-us/windows/win32/shell/abm-setstate).

Se aplica el comportamiento solicitado: deshabilitar el botón con el subtítulo
«Barra táctil activa» y conservar el estado real de autoocultado para el regreso
a la barra normal. No se cambia la preferencia de Windows al deshabilitarlo.
El control existente de WinUI ya representa el estado no disponible y evita
su pulsación. La acción vuelve a comprobar el modo antes de cualquier escritura,
para cubrir cambios de postura después del último refresco.

No se encontró una API pública que consulte directamente la presentación
efectiva de Explorer. La corrección usa una detección inferida y conservadora:
combina postura, capacidad del equipo y preferencia de barra expandible. Se
respeta el opt-out OEM ConvertibilityEnabled=0; un DWORD distinto de cero
confirma capacidad convertible, sin forzar por sí solo la postura. Tener
pantalla táctil o la optimización habilitada por sí solo no deshabilita el botón.
La preferencia desactivada permite el autoocultado en postura tablet. Si hay
anulaciones internas cuya precedencia no está validada, valores no reconocidos
o una lectura fallida, el botón explica «Modo de barra desconocido» en lugar
de afirmar que ha detectado la barra táctil.

La acción se actualiza al abrir el panel y ante cambios de ajustes, pantalla,
reanudación o recreación de la barra por Explorer. Se agrupan los eventos
pendientes sin sondeo periódico. Esta corrección requiere una prueba visual
con teclado conectado/desconectado y con la optimización activada/desactivada.
El control nativo de ventanas no estuvo disponible en esta sesión; no se
afirma haber completado esa prueba.

### Posibilidades de integración

- `ms-settings:taskbar` es una ruta pública para abrir Configuración; no cambia
  preferencias por sí sola. [URIs oficiales](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings).
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ExpandableTaskbar`
  es un candidato para un prototipo: el proyecto de laboratorio Microsoft HOBL
  usa el valor DWORD 0 para desactivar la barra expandible. Esa evidencia no
  lo convierte en una API pública ni garantiza aplicación inmediata.
  [Código original de Microsoft HOBL](https://github.com/microsoft/HOBL/blob/main/docs/support/docs/HOBL_Prep.md).
- Antes de integrarlo, comprobar correspondencia con la casilla de Windows,
  permisos de usuario normal, actualización inmediata, sincronización con
  cambios externos y restauración exacta del valor previo, incluida su ausencia.
  No dar por hecho que emitir WM_SETTINGCHANGE basta ni reiniciar Explorer
  automáticamente para ocultar una limitación.
- Los ajustes que fuerzan `ConvertibleSlateMode` o la clasificación OEM del
  equipo no son un sustituto adecuado de una preferencia de barra. La postura
  es una señal que también consumen otras funciones del sistema.
  [Configuración OEM de experiencias tablet](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/settings-for-better-tablet-experiences).

## Control de la barra táctil

La documentación actual de Microsoft sigue describiendo el modo tablet de
Windows 11 como una transición automática al cambiar la postura o conexión
del teclado, sin selector manual global. La API UserInteractionMode del SDK
28000 también distingue el antiguo modo tablet de Windows 10.
[Soporte de modo tablet](https://support.microsoft.com/en-us/windows/hardware/input-devices/turn-tablet-mode-on-or-off-in-windows),
[referencia UserInteractionMode](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.userinteractionmode?view=winrt-28000).

El texto exacto aportado por el usuario resuelve la identificación: se trata
de la casilla de optimización táctil en Personalización > Barra de tareas.
El botón propuesto se llamará **Barra táctil** y cada pulsación activará o
desactivará esa preferencia. Activarla permite a Windows usar esa presentación
cuando el equipo está en postura tablet; no fuerza esa postura con el teclado
acoplado. La propuesta de tres estados queda sustituida, y no se añadirán
anulaciones del hardware para simular un modo «Siempre».

El estado del botón deberá representar la preferencia y su subtítulo podrá
distinguir cuándo está en uso. Al desactivarla en una tablet, el autoocultado
convencional vuelve a estar disponible. La escritura de esta preferencia sigue
pendiente del prototipo que compruebe aplicación inmediata y sincronización con
Configuración, descrito arriba. No se confunde con la visibilidad o la apertura
automática del teclado táctil, que son ajustes independientes.

## Bloqueo y orientación

Propuesta de interfaz: **Bloqueo de rotación**, iluminado cuando el bloqueo del
usuario está activo. La flecha abre Horizontal, Vertical, Horizontal invertida
y Vertical invertida, junto a la orientación actual y el estado del sistema.
La acción principal se refiere a la pantalla integrada de Surface, identificada
dinámicamente; no se asume que sea siempre el monitor principal.

Las opciones ampliadas quedan diseñadas para elegir pantalla: «Pantalla de
Surface» seleccionada inicialmente y los monitores externos conectados, con
su nombre y orientación actual. La capa de orientación recibirá una identidad
de pantalla explícita y las capacidades disponibles. La lista se actualizará
al conectar o desconectar monitores, sin depender del inventario de arranque.
Si desaparece la pantalla seleccionada, no se aplicará el cambio a otra en su
lugar. En duplicado se comprobarán las restricciones de la topología.

El bloqueo de autorrotación del sistema se presenta como función de la Surface.
Una pantalla externa puede tener orientación manual aunque no tenga sensor;
no se le atribuye un bloqueo de autorrotación independiente. Ajustar esa pantalla
no debe cambiar el bloqueo de la Surface ni girar otras pantallas de la topología.

### Consultar el estado

`GetAutoRotationState` permite distinguir la preferencia del usuario de
impedimentos temporales. Sus banderas pueden indicar modo portátil, dock,
varias pantallas, sesión remota, preferencias de otra aplicación, ausencia de
sensor o configuración incompatible. Deben interpretarse como una combinación
de banderas, conservando la diferencia entre bloqueo, indisponibilidad y error
al consultar. [GetAutoRotationState](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getautorotationstate),
[AR_STATE](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ne-winuser-ar_state).

### Cambiar el bloqueo

Microsoft documenta **Win+O** para bloquear orientación. El candidato inicial
es reutilizar `Platform.KeyboardShortcut`: leer el estado, enviar una sola
pulsación y comprobar el resultado con una espera acotada. Se debe validar
que permite ambas transiciones en esta Surface y en los estados pertinentes.
[Atajos oficiales](https://support.microsoft.com/en-us/accessibility/windows/keyboard-shortcuts-in-windows).

Que `SendInput` acepte los eventos no garantiza que el shell haya cambiado el
ajuste. Hay restricciones de integridad y de teclas ya pulsadas; el resultado
debe proceder de la lectura posterior. Ante fallo se muestra el motivo y se
ofrece abrir Pantalla en Configuración. [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).

`SetDisplayAutoRotationPreferences` configura preferencias del proceso que
llama; no es el setter del bloqueo global. Tampoco se propone como base escribir
`HKLM\...\AutoRotation\Enable` o utilizar un ordinal privado de user32.
[Alcance de SetDisplayAutoRotationPreferences](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayautorotationpreferences).

### Elegir una orientación fija

Las APIs públicas permiten cambiarla. `QueryDisplayConfig` y `SetDisplayConfig`
encajan con la infraestructura existente, pero exigen ampliar las estructuras
de modos y conservar correctamente la topología. `EnumDisplaySettings` y
`ChangeDisplaySettingsExW` son una alternativa a evaluar sobre un dispositivo
concreto, con validación previa del modo. No hace falta implementar un motor
de rotación propio. [Escenarios de SetDisplayConfig](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/setdisplayconfig-summary-and-scenarios),
[SetDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig),
[ChangeDisplaySettingsExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-changedisplaysettingsexw).

Al elegir una orientación fija para la Surface, la propuesta es guardar el estado anterior,
bloquear la autorrotación si hace falta, comprobarlo y aplicar el modo al panel
integrado. Si el bloqueo falla, no presentar una orientación como fijada.
Un cambio aceptado mostraría «Mantener / Revertir», con recuperación temporizada.
La operación debe vivir fuera de la vista para sobrevivir al cierre del panel.
Un fallo parcial o cambio externo concurrente requiere releer y explicar el
estado, evitando restaurar ciegamente una configuración antigua.

### Investigar los fallos intermitentes

No se ha reproducido el problema. El siguiente diagnóstico debe registrar,
solo cuando se habilite localmente, hora, banderas AR_STATE, postura, pantalla
activa, orientación real y eventos de suspensión/reanudación. Una lectura del
sensor permitiría comparar el giro físico con el modo de pantalla; sus estados
boca arriba y boca abajo no deben convertirse automáticamente en giros.
[SimpleOrientationSensor](https://learn.microsoft.com/en-us/uwp/api/windows.devices.sensors.simpleorientationsensor?view=winrt-26100).

El diagnóstico puede orientar la investigación hacia postura, política o sensor.
No permite prometer que el botón reparará un problema de firmware o controlador.

## Encaje en el repositorio

La revisión parte de `d6f3421`, versión 0.2.0-alpha.6, con árbol limpio al inicio.

- `ProjectionAction` ya implementa pulsación cíclica y subpágina de selección
  exclusiva. `IQuickAction` permite reutilizar ese patrón.
- `TaskbarAutoHideAction` controla exclusivamente autoocultado mediante
  `SHAppBarMessage`. La optimización táctil debe ser una preferencia independiente.
- `SystemEvents` ya expone cambios de pantalla, ajustes y reanudación. Hay que
  conectar los eventos pertinentes al estado y posición del panel; actualmente
  los tiles se refrescan al abrir y no hay conexión general de DisplayChanged
  al refresco del panel. Para postura, Windows documenta WM_SETTINGCHANGE con
  ConvertibleSlateMode. [GetSystemMetrics](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetrics).
  La corrección de autoocultado conecta esos eventos a su tile; sigue pendiente
  el trabajo general de recolocación y actualización para rotación.
- `FlyoutWindow` debe recalcular el área de trabajo al rotar con el panel abierto
  y revalidar el monitor de apertura. Su ancla basada en coordenadas del cursor
  puede quedar desactualizada tras una reorganización de pantallas.
- `QuickActionBase` registra errores sin un mensaje general visible al usuario.
  Este bloque necesita explicar fallos y estados temporalmente no disponibles.
- `TileViewModel` captura los hijos al construirse. El selector de pantallas
  de rotación necesitará reconciliar la lista o usar una vista propia, evitando
  la limitación de inventario inicial de HDR.
- Registro de acciones en `ActionCatalog`, textos en `UiText` para español,
  catalán/valenciano e inglés; nuevos servicios propiedad de App, fuera de vistas
  que se eliminan al ocultar el panel.

## Mejoras adicionales propuestas

1. **Acceso táctil cómodo a Llampec.** Preparar un acceso directo que pueda
   anclarse en Inicio o en la barra. Ejecutar Llampec de nuevo ya abre la instancia
   existente. Un botón flotante opcional puede evaluarse después si ese acceso
   resulta insuficiente.
2. **Estado de rotación comprensible.** Mostrar por qué Windows no gira en la
   misma subpágina, con acción para volver a consultar.
3. **Teclado táctil a mano.** Investigar su apertura desde un botón y el selector
   de comportamiento, distinguiéndolo del teclado de accesibilidad.
4. **Vista de tareas o dictado.** Son accesos útiles sin teclado físico; los
   atajos oficiales Win+Tab y Win+H ofrecen candidatos para un prototipo.
   [Atajos de Windows](https://support.microsoft.com/en-us/accessibility/windows/keyboard-shortcuts-in-windows).
5. **Perfil de lectura, después.** Combinar orientación fija con la función
   Cafeína existente. Primero conviene validar los controles individuales y
   definir cómo conviven con cambios que el usuario haga fuera de Llampec.

Mantener iconos visualmente pequeños es compatible con conservar áreas cómodas
de pulsación en Llampec. Revisar especialmente flechas, volver y ajustes durante
las pruebas con dedo y lápiz.

## Próximas pruebas antes de integrar

1. Validar el prototipo del interruptor Barra táctil sobre la casilla identificada.
2. Validar que el autoocultado queda deshabilitado con barra táctil y vuelve a
   estar disponible con barra normal, incluidas las transiciones con el panel
   abierto. El tamaño de iconos queda descartado. Cualquier futura escritura
   de preferencias internas necesita su propia prueba de aplicación y restauración.
3. Prototipo local de bloqueo mediante Win+O con verificación; después, las
   cuatro orientaciones y recuperación temporal del modo anterior.
4. Validar teclado separado, conectado y plegado; suspensión/reanudación;
   panel abierto y cerrado; pantalla externa en extendido y duplicado;
   escalas de pantalla, batería, dedo, lápiz y ratón. Comprobar ARM64 nativo y
   validar x64 en hardware adecuado antes de afirmar esa cobertura.
5. Pruebas deterministas con backend simulado para ciclo de preferencias,
   banderas, errores parciales, tiempo de espera, cambios externos y elección
   de pantalla. Las pruebas que modifican hardware deben ser optativas.

No se han ejecutado escrituras sobre ajustes de Windows. La corrección del botón
se ha verificado con la suite segura del proyecto: 281 pruebas superadas, incluidas
37 nuevas de clasificación y comportamiento del autoocultado. La aplicación
compila en Release ARM64 sin errores ni advertencias. Una comprobación
nativa de solo lectura con el código nuevo en esta Surface devuelve
TabletOptimized, IsAvailable=false, subtítulo Tablet taskbar active y preferencia
de autoocultado Off. Esa lectura comprueba la detección y el estado de la acción;
no sustituye a observar el botón en pantalla ni las transiciones físicas.

Quedan pendientes la prueba visual, los mecanismos de cambio de rotación y la
reproducción del fallo intermitente. La instancia publicada 0.2.0-alpha.6 que
estaba abierta no se ha reemplazado ni reiniciado; los cambios permanecen en
el código y la compilación local del repositorio.
