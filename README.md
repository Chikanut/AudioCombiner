# Audio Combiner

**English** · [Українська](README.uk.md)

A small Windows tool that relays audio from one device to several others at once, for example to two Bluetooth headsets so two people can watch a movie together.

> The app's interface is in Ukrainian. The steps below name the controls in English with the Ukrainian label in brackets.

## Download

Get `AudioCombiner.exe` from the [latest release](https://github.com/Chikanut/AudioCombiner/releases/latest) and run it. No installation needed.

Requires Windows 10/11 and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows offers to install it on first launch if it's missing).

## Setting up two headsets

**Option A: with VB-Cable (best sync)**

1. Install the free [VB-Cable](https://vb-audio.com/Cable/) and reboot.
2. In Windows Sound settings, make **CABLE Input** the default device.
3. In the app, pick **[Вхід] CABLE Output** as the source.
4. Tick both headsets and press Start («Старт»).

Both headsets get the sound the same way, through the app.

**Option B: no extra drivers**

Pick as the source an output you're not listening to (for example `[Вихід, перехоплення] Speakers` or an HDMI monitor) and make it the default device. The app captures its sound and sends it to the headsets. That output keeps playing too, so turn its physical volume down or mute the monitor.

If you pick one of the headsets as the source, it plays directly and the other one lags behind. Delay can only be added, not removed, so avoid this setup.

## Syncing

Different Bluetooth headset models have their own latency (often 100–250 ms).

1. Turn on the Metronome («Метроном»), so both headsets click once a second.
2. Hold one earpiece from each headset to your ears.
3. Add delay to the headset that clicks **earlier** until the clicks merge.
4. Turn the metronome off.

Delay and volume can be changed while watching; changes apply immediately.

## What it handles for you

* Smoothly compensates for clock drift between devices, without clicks or buffer restarts.
* Reconnects a headset that dropped out as soon as it comes back.
* Converts sample rate and channel count for each device.
* Warns when a headset is in low-quality "Hands-Free" mode, so you can pick its "Headphones" device instead.
* Runs from the system tray. Can start with Windows and begin relaying right away.
* Checks GitHub for new releases and updates itself in one click.

Settings are stored in `%APPDATA%\AudioCombiner\settings.json`.

## Updates

The app checks for a new version on launch and then every 6 hours. When one is available, an update link appears at the bottom of the window and an "update" item («Оновити») appears in the tray menu. The app downloads the new `AudioCombiner.exe`, replaces itself and restarts, and relaying resumes.

Automatic checks can be turned off with the "check for updates automatically" box («Автоматично перевіряти оновлення»). The "check for updates" link («Перевірити оновлення») at the bottom of the window always works.

If the app sits in a folder it can't write to (such as `Program Files`), it can't replace itself and offers to open the release page instead.

## Limitations

Some Bluetooth adapters can't handle two stereo headsets at once, and the sound stutters. The app can't fix that; a different adapter (Bluetooth 5.x) helps.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o publish
```

### Releasing a new version

Create a GitHub release with a `vX.Y.Z` tag. The [`release.yml`](.github/workflows/release.yml) workflow builds `AudioCombiner.exe` with that version and attaches it to the release, and installed copies then offer the update.

## License

[MIT](LICENSE)
