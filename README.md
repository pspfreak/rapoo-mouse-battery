# Rapoo mouse battery

A small Windows tray app for Rapoo wireless mice on the 2.4 GHz dongle. It works without Rapoo's own
software.

**Known working:** Rapoo VT7 (dongle `24AE:1413`). Other models may work but are untested; they may use
different reports or addresses.

- Battery percentage as the tray icon, with low (20%) and critical (10%) notifications
- Click the icon for a popup with battery level and DPI stage selection
- Wired / wireless and charging detection, with a Logitech-style on-screen pop-up
- DPI stage changes made on the mouse show up too

C# / .NET 9 / WinForms, using [HidSharp](https://www.zer7.com/software/hidsharp). Framework-dependent,
single-file publish.

## Build

```
cd src/RapooBattery
dotnet publish -c Release -o ../../dist
```

Run `dist/RapooBattery.exe`. Right-click the tray icon for "Start with Windows" and "Show pop-ups".
`RapooBattery.exe --demo-osd` previews the pop-ups. Unhandled errors are logged to
`%LOCALAPPDATA%\RapooBattery\error.log`.

## Protocol notes

Reverse engineered from the dongle's HID interfaces, then checked against the public write-ups in
[pedro3z0/rapoo-software-linux](https://github.com/pedro3z0/rapoo-software-linux) and
[hsb689/VT3S-Rapoo](https://github.com/hsb689/VT3S-Rapoo), which document other Rapoo models. Everything
below was verified against a real mouse.

### Status (passive)

Usage `FF00:0002`, report ID 7, 19 bytes, pushed by the dongle about every 3 s when idle:

| Byte | Meaning |
|---|---|
| 0 | Report ID (7) |
| 1 | Low nibble = connection type: `0` wireless, `2` wired (data cable attached) |
| 2 | Current DPI stage (0-based) |
| 3-4 | DPI X, little-endian |
| 5-6 | DPI Y, little-endian |
| 7 | Validity/activity flag (0/1/2 seen), not a charging flag |
| 8 | Battery percent (0-100) |
| 13+ | Constant (ID / version); the last byte is noise |

With a data cable, the mouse also enumerates as `24AE:4613` with the same interfaces. A charge-only cable
is invisible to the dongle; the app infers charging from a quick rise in the battery reading.

### Control (active)

Commands are output report 6 on `FF00:000E`; replies are read as feature report 8 on `FF00:000F`
(status byte 0: `01` ok, `02` busy; data from byte 4).

```
06 <conn> <op> <len> <addr:4 LE> <data...>     conn: A5 via dongle, FF via cable
op: A4 read, A5 write
```

Profile 0 starts at `0x0600`. Within it: DPI X list at +648 (7 x u16), enabled stage count minus one at
+662, current stage at +664. The app only ever writes the current-stage byte. With a cable attached the
mouse answers only over USB (the dongle reports busy), so the app talks to the wired device then.

Bytes 0x0000-0x0FFF of the EEPROM are static configuration; nothing in that range changes with charging.
The 513-byte `FF00:0001` interface is probably firmware update and is deliberately left alone.

## License

[MIT](LICENSE)

## Tools

- `tools/probe` dumps HID descriptors and logs all reports from every interface
- `tools/ctl` reads/dumps EEPROM ranges and sets the DPI stage
