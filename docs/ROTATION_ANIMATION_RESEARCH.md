# Animación nativa de rotación: investigación futura

Fecha de revisión: **2026-10-02**. Alcance: trabajo posterior a **0.2.0-alpha.7**.
Este documento no cambia la implementación ni condiciona la publicación de esa
versión. En esta investigación no se ejecutaron nuevos giros, cambios de sensor,
experimentos de interfaz ni instalaciones de controladores.

## Conclusión

No se ha encontrado una API pública documentada que ordene a Windows reproducir
la misma animación de zoom y giro que acompaña a la rotación física de la Surface.
Esto delimita las interfaces revisadas; no demuestra que Windows carezca de una
implementación interna ni que ningún cambio de modo pueda acabar animándose.

Hay dos experimentos públicos razonables, de alcance acotado:

1. Comparar el cambio actual de GDI con un cambio equivalente mediante CCD
   (`SetDisplayConfig`), preservando las características del modo y la topología.
2. Comprobar si una preferencia de orientación del propio proceso provoca una
   transición nativa en una ventana de prueba, y qué sucede al perder el foco.

Ninguno permite prometer de antemano la animación. El segundo tiene una limitación
de producto importante: una preferencia de una aplicación visible no equivale a
una selección global persistente que debe continuar después de cerrar el panel.
No recomiendo introducir interfaces privadas del shell, un controlador virtual
o una captura superpuesta de todo el escritorio para obtener este efecto.

## Qué está documentado

### La animación y el cambio de modo son cosas distintas

Microsoft describe una transición gestionada por Windows que reduce la imagen,
espera al nuevo diseño y después gira y amplía el resultado. La explicación se
refiere a Windows 10 y aplicaciones UWP/DirectX: sirve para identificar quién
gestiona la transición, pero no garantiza idéntico comportamiento de cualquier
API de escritorio en Windows 11. `CoreWindowResizeManager.NotifyLayoutCompleted`
avisa de que terminó el diseño tras un cambio ya recibido; no inicia un giro del
escritorio. [Transición del sistema](https://learn.microsoft.com/en-us/windows/uwp/gaming/supporting-screen-rotation-directx-and-cpp#reduce-the-rotation-delay-by-using-corewindowresizemanager),
[NotifyLayoutCompleted](https://learn.microsoft.com/en-us/uwp/api/windows.ui.core.corewindowresizemanager.notifylayoutcompleted?view=winrt-26100).

La documentación WDDM también usa el término **smooth rotation**, pero describe
mantener activa la salida del adaptador y evitar parpadeos durante el cambio de
modo. El controlador declara soporte y Windows comprueba compatibilidad de
modos, topología, frecuencia y formato. Esto no constituye una promesa de la
animación visual de zoom y giro. Las funciones `DxgkDdiCommitVidPn` y
`DxgkDdiUpdateActiveVidPnPresentPath` pertenecen al controlador; no son un botón
de animación para una aplicación WinUI. [Optimización de rotación WDDM](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/optimized-screen-rotation-support).

**Inferencia para Llampec:** conservar el modo físico y evitar una renegociación
innecesaria puede mejorar la continuidad del cambio. Un resultado sin pantalla
negra sería una mejora medible, pero no prueba que se haya invocado la transición
del sensor. Ambas cosas deben medirse por separado.

### APIs de cambio de pantalla

`ChangeDisplaySettingsEx` permite probar, aplicar y guardar un modo. Es la ruta
de alpha.7; `CDS_UPDATEREGISTRY` aplica y guarda en una llamada. `SetDisplayConfig`
permite validar y aplicar rutas, modos y topología CCD, además de persistirlos.
Las listas públicas de parámetros y flags de ambas APIs no incluyen una opción
para solicitar la animación del sensor. [ChangeDisplaySettingsEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-changedisplaysettingsexw),
[SetDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig).

**Inferencia:** sustituir GDI por CCD solo para conseguir la animación no está
justificado todavía. CCD sí es una alternativa pública que permite comparar
el resultado con control explícito de las rutas. La prueba debe mantener
resolución, frecuencia y pantallas; permitir a Windows cambiar esos valores para
hacer válida una configuración confundiría la comparación.

En la validación previa de Llampec se observó que el guardado separado con
`CDS_NORESET` no se verificaba correctamente en esta Surface, mientras que la
llamada combinada sí. Por eso alpha.7 utiliza aplicación y guardado conjunto.
Ese hallazgo local no prueba nada sobre la animación y no debe revertirse para
experimentar. El historial está en [TABLET.md](TABLET.md).

### Preferencias de orientación: una vía pública distinta, con alcance limitado

`SetDisplayAutoRotationPreferences` establece preferencias para el **proceso
que llama**. La API de consulta por proceso devuelve además `fRotateScreen`,
que indica si la pantalla fue girada para cumplir sus preferencias. Existe,
por tanto, un mecanismo público por el que la política de una aplicación puede
influir en la orientación; no es correcto descartarlo como una mera consulta.
Ninguna de esas dos páginas promete una animación o una orientación persistente
para una pantalla elegida. [Preferencia Win32](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayautorotationpreferences),
[Consulta por proceso](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdisplayautorotationpreferencesbyprocessid).

La propiedad WinRT `DisplayInformation.AutoRotationPreferences` advierte que
Windows puede ignorar la preferencia y que no la respeta en ventanas solapadas.
Esta advertencia pertenece al contrato WinRT; no convierte una prueba con el
setter Win32 en imposible, pero sí impide suponer que un panel de escritorio
obtendrá el comportamiento de una aplicación inmersiva. [Contrato WinRT](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.display.displayinformation.autorotationpreferences?view=winrt-26100).

**Hipótesis comprobable:** una ventana propia que Windows considere elegible
podría activar la misma política que anima algunas transiciones. Quedan por
demostrar la animación, el efecto del foco, el bloqueo del usuario y la postura
del teclado. Forzar una ventana a pantalla completa solo para conseguirlo
alteraría el flujo de lectura/dibujo y no sería una integración aceptable por
defecto. Tampoco se debe desbloquear temporalmente la Surface y esperar que el
sensor produzca casualmente la orientación seleccionada.

### WinUI, interoperabilidad y composición

El `DisplayInformation` de Windows App SDK, incluida la superficie documentada
con la vista 2.0 consultada, enumera información de color, estéreo, ventanas y
pantallas; no ofrece un método para rotar el escritorio o iniciar esa animación.
Esto describe la página revisada, sin afirmar que esa vista sea la versión
estable instalada por el proyecto. [API de Windows App SDK](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.graphics.display.displayinformation?view=windows-app-sdk-2.0).

Windows 11 dispone de `IDisplayInformationStaticsInterop` para obtener el objeto
WinRT asociado a un `HWND` o `HMONITOR`. Sus métodos son fábricas de objetos, no
órdenes de animación. [Interoperabilidad de escritorio](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.display.interop/nn-windows-graphics-display-interop-idisplayinformationstaticsinterop).

`IDXGISwapChain1.SetRotation` orienta los buffers de una cadena de presentación
de la aplicación; no rota otras ventanas ni controla el escritorio completo.
Las transiciones de WinUI y DirectComposition pueden animar el contenido propio.
Son apropiadas para dar respuesta visual al selector, pero ese resultado sería
una animación de Llampec. [DXGI SetRotation](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgiswapchain1-setrotation),
[Transiciones de WinUI](https://learn.microsoft.com/en-us/windows/apps/develop/motion/implicit-transitions).

### Sensor, controlador y shell privado

`SimpleOrientationSensor` expone lecturas y notificaciones. `ReadingTransform`
transforma los datos para el consumidor; no es un setter de la postura física
que Windows debe creer. Su superficie documentada no ofrece inyectar una
orientación del sistema desde una aplicación. [SimpleOrientationSensor](https://learn.microsoft.com/en-us/uwp/api/windows.devices.sensors.simpleorientationsensor?view=winrt-28000).

Microsoft publica ejemplos UMDF v2 de sensores virtuales y de emisión de valores
de orientación. Son una vía para desarrollar un controlador, no una API de
animación. Crear y distribuir ese componente añade instalación, firma,
compatibilidad de arquitectura, energía y convivencia con el sensor real.
La documentación revisada no garantiza que Windows elija un sensor virtual como
fuente de autorrotación ni que emita la transición deseada. Los requisitos de
firma varían según el tipo de controlador; las reglas específicas de kernel no
deben atribuirse sin más a un ejemplo UMDF. [Ejemplos de sensores](https://learn.microsoft.com/en-us/windows-hardware/drivers/samples/sensor-driver-samples),
[Firma de controladores](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/driver-signing).

La búsqueda de contratos Win32, WinRT, WinUI y la revisión del `WinUser.h`
publicado por Microsoft no identificaron una orden pública adicional del shell
para esta animación. Esto no es un inventario de interfaces privadas ni una
prueba de su inexistencia. Encontrar un ordinal, interfaz COM interna o símbolo
en un binario no establecería por sí solo un contrato soportado. Depender de
ello exigiría investigar versiones de Windows, diferencias ARM64/x64 y
recuperación ante cambios del shell; el coste seguiría sin acotarse.
[Cabecera de Microsoft revisada](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/WinUser.h).

## Opciones y coste estimado

Las valoraciones siguientes son estimaciones de ingeniería, no resultados de
prototipos ni compromisos de entrega.

| Opción | Posibilidad y límite | Coste y mantenimiento | Recomendación |
|---|---|---|---|
| Conservar el cambio actual | Orientación y persistencia verificadas; no promete animación | Bajo, integración existente | Base de alpha.7 |
| Comparar CCD con GDI | Puede revelar una transición más fluida; no hay flag de animación | Prototipo medio; integración de varias pantallas medio/alto | Primer experimento acotado |
| Preferencia del proceso | Windows puede orientar por preferencia; elegibilidad y persistencia inciertas | Prueba pequeña; encaje en el panel probablemente costoso | Segundo experimento opcional |
| Animar la tarjeta o panel propios | Respuesta visual soportada; no es el giro del escritorio | Bajo y mantenible | Alternativa si el usuario la desea |
| Captura del escritorio superpuesta | Imitación, con sincronización, escalado, HDR, foco y contenido protegido que resolver | Alto | Descartar para este objetivo |
| Shell privado o controlador virtual | Sin contrato identificado que garantice el efecto | Muy alto y abierto; posible mantenimiento por build | No incorporar a Llampec |

## Prototipo propuesto, aún no ejecutado

Realizarlo después de alpha.7, aislado de la aplicación publicada. Su objetivo es
resolver una pregunta de viabilidad antes de plantear una integración.

1. **Obtener una referencia.** Registrar build de Windows, controlador gráfico,
   postura y bloqueo; comparar giro físico, selector de Configuración y cambio
   actual de Llampec. Una captura fija no demuestra una animación: usar una
   grabación local breve o una observación temporal reproducible. Separar zoom y
   giro del sistema, parpadeo y tiempo hasta volver a interactuar.
2. **Probar CCD sin alterar otras características.** Capturar las rutas activas,
   modificar únicamente la orientación y dimensiones asociadas necesarias,
   validar y aplicar/guardar. Conservar posiciones, tasas, modos de las otras
   pantallas y topología. No activar `SDC_ALLOW_CHANGES` ni forzar un cambio de
   driver para conseguir una comparación favorable. No convertir directamente
   los enteros GDI a CCD: sus enumeraciones y convenciones de orientación deben
   mapearse expresamente, incluyendo paneles de orientación nativa vertical.
3. **Comparar los cuatro destinos.** Empezar con la pantalla interna; repetir con
   teclado acoplado y desacoplado, sin forzar su postura. Si hay una mejora,
   comprobar después una pantalla externa extendida. Mantener la limitación
   actual de fuentes clonadas hasta diseñar soporte explícito para ellas.
4. **Probar preferencias solo si sigue habiendo una hipótesis útil.** Usar un
   proceso de prueba propio, leer/restaurar su preferencia y observar el efecto
   en ventana normal y, opcionalmente, a pantalla completa. Comprobar qué ocurre
   al desactivar, ocultar y cerrar la ventana. No confundir una respuesta `TRUE`
   con la presencia de animación o con persistencia global.
5. **Verificar y restaurar.** Conservar estados activo y guardado por separado,
   bloqueo y configuración de todas las pantallas. Cualquier intento fallido
   debe restaurar solo cambios que sigan siendo atribuibles al prototipo. No
   pisar una intervención del usuario ni adoptar una topología nueva como si
   fuese la original. La recuperación es del experimento: no reintroduce una
   cuenta atrás en el producto.

Para CCD, los flags de modos virtuales y frecuencia virtual deben coincidir con
las estructuras y capacidades usadas; conservar la información evita cambios
colaterales en Windows 11. La documentación distingue validación, aplicación y
persistencia. [Flags y reglas de SetDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig),
[Rotación CCD](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ne-wingdi-displayconfig_rotation),
[Orientación GDI](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-devmodew).

## Criterios para continuar o cerrar la investigación

Continuar solo si una ruta pública produce una mejora visible repetible sin
cambiar el modo elegido, persistencia, bloqueo, foco o configuración ajena.
La prueba debe distinguir si se obtuvo la animación nativa o solo un cambio
con menos parpadeo. Una integración deberá respetar las animaciones desactivadas
por el usuario y no introducir una espera artificial para embellecer el cambio.

Cerrar el experimento si las dos rutas públicas no aportan una mejora clara en
una sesión acotada, si el efecto desaparece al ocultar la ventana, o si exige
foco a pantalla completa, desactivar un bloqueo, simular sensores, instalar
drivers o llamar a interfaces privadas. Una animación conseguida una vez no
compensa perder la fiabilidad del selector manual.

La recomendación actual es conservar la rotación inmediata de alpha.7 y dejar
los experimentos anteriores como investigación futura. No hay evidencia para
presupuestar esta animación como un cambio pequeño de API ya resuelto.
