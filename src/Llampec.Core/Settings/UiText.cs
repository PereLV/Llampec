using System.Globalization;

namespace Llampec.Settings;

/// <summary>Small, shared UI catalogue. Unknown text (for example monitor names) is preserved.</summary>
public static class UiText
{
    public static string Language { get; private set; } = "en";
    public static string ResolveLanguage(string? tag) => (tag ?? CultureInfo.CurrentUICulture.Name).Split('-')[0].ToLowerInvariant() switch
    { "es" => "es", "ca" => "ca", _ => "en" };
    public static void SetLanguage(string? tag) => Language = ResolveLanguage(tag);
    public static string Get(string text) => Translate(text, Language);
    public static string Translate(string text, string language) => Catalogue.TryGetValue(text, out var value)
        ? language switch { "es" => value.Es, "ca" => value.Ca, _ => text } : text;
    public static string Format(string text, params object[] values) => string.Format(CultureInfo.CurrentCulture, Get(text), values);
    public static IReadOnlyCollection<string> Keys => Catalogue.Keys;

    private static readonly Dictionary<string, (string Es, string Ca)> Catalogue = new(StringComparer.Ordinal)
    {
        ["Caffeine mode"] = ("Modo cafeína", "Mode cafeïna"),
        ["Duration"] = ("Duración", "Duració"),
        ["Indefinite"] = ("Indefinido", "Indefinit"),
        ["1 hour"] = ("1 hora", "1 hora"),
        ["2 hours"] = ("2 horas", "2 hores"),
        ["3 hours"] = ("3 horas", "3 hores"),
        ["Custom"] = ("Personalizado", "Personalitzat"),
        ["Minutes (1–1440)"] = ("Minutos (1–1440)", "Minuts (1–1440)"),
        ["Keep the screen on"] = ("Mantener la pantalla encendida", "Mantín la pantalla encesa"),
        ["Until {0}"] = ("Hasta las {0}", "Fins a les {0}"),
        ["Activate"] = ("Activar", "Activa"),
        ["Deactivate"] = ("Desactivar", "Desactiva"),
        ["Apply and restart"] = ("Aplicar y reiniciar", "Aplica i reinicia"),
        ["Enter a whole number from 1 to 1440."] = ("Introduce un número entero del 1 al 1440.", "Introduïx un nombre enter de l’1 al 1440."),
        ["Could not activate caffeine mode."] = ("No se ha podido activar el modo cafeína.", "No s’ha pogut activar el mode cafeïna."),
        ["Could not save settings."] = ("No se han podido guardar los ajustes.", "No s’ha pogut guardar la configuració."),
        ["Prevents automatic sleep. Manual sleep ends the session. Windows may limit it on battery."] = ("Evita la suspensión automática. Suspender manualmente finaliza la sesión. Windows puede limitarlo con batería.", "Evita la suspensió automàtica. Suspendre manualment finalitza la sessió. Windows pot limitar-ho amb bateria."),
        ["Settings"] = ("Ajustes", "Configuració"),
        ["Language"] = ("Idioma", "Idioma"),
        ["Use Windows language"] = ("Usar el idioma de Windows", "Usa l’idioma de Windows"),
        ["Start with Windows"] = ("Iniciar con Windows", "Inicia amb Windows"),
        ["Opens in the notification area when you sign in."] = ("Se inicia en el área de notificación al entrar en tu sesión.", "S’inicia a l’àrea de notificació en entrar a la sessió."),
        ["Windows can also disable startup. Keep Llampec in this folder, or enable this option again after moving it."] = ("Windows también puede desactivar el inicio. Conserva Llampec en esta carpeta o vuelve a activar esta opción si lo mueves.", "Windows també pot desactivar l’inici. Conserva Llampec en esta carpeta o torna a activar esta opció si el mous."),
        ["Windows startup apps"] = ("Aplicaciones de inicio de Windows", "Aplicacions d’inici de Windows"),
        ["Could not update startup registration."] = ("No se ha podido cambiar el inicio con Windows.", "No s’ha pogut canviar l’inici amb Windows."),
        ["Could not open Windows Settings."] = ("No se han podido abrir los ajustes de Windows.", "No s’ha pogut obrir la configuració de Windows."),
        ["Reorder buttons"] = ("Reordenar botones", "Reordena els botons"),
        ["Drag rows to change their order, or select one and use the arrows. The panel fills from left to right."] = ("Arrastra las filas o selecciona una y usa las flechas. El panel se ordena de izquierda a derecha.", "Arrossega les files o selecciona’n una i usa les fletxes. El panell s’ordena d’esquerra a dreta."),
        ["Move up"] = ("Subir", "Puja"), ["Move down"] = ("Bajar", "Baixa"),
        ["Save"] = ("Guardar", "Guarda"), ["Cancel"] = ("Cancelar", "Cancel·la"),
        ["Back"] = ("Atrás", "Enrere"), ["Menu"] = ("Menú", "Menú"),
        ["About Llampec"] = ("Acerca de Llampec", "Quant a Llampec"),
        ["Exit"] = ("Salir", "Ix"), ["Close"] = ("Cerrar", "Tanca"),
        ["Version"] = ("Versión", "Versió"),
        ["On"] = ("Activado", "Activat"), ["Off"] = ("Desactivado", "Desactivat"),
        ["Dark mode"] = ("Modo oscuro", "Mode fosc"), ["Light mode"] = ("Modo claro", "Mode clar"),
        ["Dark"] = ("Oscuro", "Fosc"), ["Light"] = ("Claro", "Clar"),
        ["Dark in {0}"] = ("Oscuro en {0}", "Fosc en {0}"),
        ["Light in {0}"] = ("Claro en {0}", "Clar en {0}"),
        ["{0} min"] = ("{0} min", "{0} min"),
        ["{0} h"] = ("{0} h", "{0} h"),
        ["{0} h {1} min"] = ("{0} h {1} min", "{0} h {1} min"),
        ["Turn off display"] = ("Apagar pantalla", "Apaga la pantalla"),
        ["Auto-hide taskbar"] = ("Ocultar barra de tareas", "Oculta la barra de tasques"),
        ["Multiple displays"] = ("Varias pantallas", "Diverses pantalles"),
        ["PC screen only"] = ("Solo pantalla del PC", "Només la pantalla del PC"),
        ["Second screen only"] = ("Solo segunda pantalla", "Només la segona pantalla"),
        ["Duplicate"] = ("Duplicar", "Duplica"), ["Extend"] = ("Extender", "Estén"),
        ["Not supported"] = ("No compatible", "No compatible"), ["All on"] = ("Todas activadas", "Totes activades"),
        ["Some displays are on"] = ("Algunas pantallas están activadas", "Algunes pantalles estan activades"),
        ["{0} options"] = ("Opciones de {0}", "Opcions de {0}"),
        ["{0} of {1}"] = ("{0} de {1}", "{0} de {1}"),
        ["Schedule"] = ("Programación", "Programació"),
        ["Light and dark times must differ."] = ("Las horas de modo claro y oscuro deben ser distintas.", "Les hores de mode clar i fosc han de ser diferents."),
        ["Enter a latitude from -90 to 90 and a longitude from -180 to 180."] = ("Introduce una latitud entre -90 y 90 y una longitud entre -180 y 180.", "Introduïx una latitud entre -90 i 90 i una longitud entre -180 i 180."),
        ["Unknown schedule mode."] = ("Modo de programación desconocido.", "Mode de programació desconegut."),
        ["Fixed hours"] = ("Horario fijo", "Horari fix"),
        ["Sunrise / sunset"] = ("Amanecer / anochecer", "Eixida / posta del sol"),
        ["Latitude"] = ("Latitud", "Latitud"), ["Longitude"] = ("Longitud", "Longitud"),
        ["Light at sunrise, dark at sunset. Coordinates are saved for offline calculations."] = ("Modo claro al amanecer y oscuro al anochecer. Las coordenadas se guardan para calcularlo sin conexión.", "Mode clar a l’eixida del sol i fosc a la posta. Les coordenades es guarden per a calcular-ho sense connexió."),
        ["Use current location"] = ("Usar ubicación actual", "Usa la ubicació actual"),
        ["Location detected. Apply to save."] = ("Ubicación detectada. Pulsa Aplicar para guardarla.", "Ubicació detectada. Prem Aplica per a guardar-la."),
        ["Location unavailable or permission denied. Enter coordinates manually."] = ("Ubicación no disponible o sin permiso. Introduce las coordenadas manualmente.", "Ubicació no disponible o sense permís. Introduïx les coordenades manualment."),
        ["Location unavailable: "] = ("Ubicación no disponible: ", "Ubicació no disponible: "),
        ["Changes Windows and apps while Llampec is running. A manual toggle lasts until the next scheduled change."] = ("Cambia Windows y las aplicaciones mientras Llampec está en ejecución. Un cambio manual dura hasta el siguiente cambio programado.", "Canvia Windows i les aplicacions mentre Llampec està en execució. Un canvi manual dura fins al següent canvi programat."),
        ["Apply"] = ("Aplicar", "Aplica"),
        ["Could not save settings. Check access to the Llampec settings folder."] = ("No se han podido guardar los ajustes. Comprueba el acceso a la carpeta de ajustes de Llampec.", "No s’ha pogut guardar la configuració. Comprova l’accés a la carpeta de configuració de Llampec."),
        ["Apply to save changes."] = ("Pulsa Aplicar para guardar los cambios.", "Prem Aplica per a guardar els canvis."),
        ["Schedule is off."] = ("Programación desactivada.", "Programació desactivada."),
        ["Polar day/night: no sunrise or sunset today. Checked again tomorrow."] = ("Día o noche polar: hoy no hay amanecer o anochecer. Se comprobará mañana.", "Dia o nit polar: hui no hi ha eixida o posta del sol. Es comprovarà demà."),
        ["Next: {0} · {1:g}"] = ("Siguiente: {0} · {1:g}", "Següent: {0} · {1:g}"),
    };
}
