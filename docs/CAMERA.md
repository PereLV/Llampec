# Camera

The **Camera** tile (**Cámara** in Spanish, **Càmera** in Catalan/Valencian) lights
up while any camera is capturing. Its caption names the camera and the apps using
it, for example *Surface Camera Front · Camera*, or shows *Not in use*. Pressing it
opens Windows camera settings (`ms-settings:camera`), where Windows offers the
camera's own image, privacy and effects settings. Llampec does not change camera
settings.

## Implementation

- `Platform/CameraActivityMonitor` uses Windows' documented Media Foundation sensor
  activity monitor (`MFCreateSensorActivityMonitor`, Windows 10 1703+). Windows
  reports which processes are streaming from each camera. App names come from the
  packaged app's display name, else the executable's description or file name.
- The monitor runs only while the panel is visible. When started, Windows reports
  cameras that are already in use, so the tile is correct as soon as the panel
  opens. Hiding the panel stops the monitor and pairs `MFStartup` with `MFShutdown`.
  There is no polling and no work while the panel is closed.
- Infrared cameras used by Windows Hello also count as cameras in use while they
  stream.

Validated on a Surface (ARM64, build 28000): with the Windows Camera app open
before the panel, the tile showed *Surface Camera Front · Cámara* when the panel
opened, and returned to *Not in use* after the app closed. Startup took about
15 ms. With the panel hidden, the process used no CPU across 48 seconds.
