# To do

## Puck.js rename doesn't disconnect on Windows

After **Change Name** on a Puck.js, Windows keeps the Bluetooth LE link up, so the puck never restarts its advertising
and the new name never takes effect; the puck is left waiting in an in-between state. Only the puck is affected: a
Pixl.js disconnects fine.

What's known:

- The puck isn't paired (plain BLE).
- Closing the app ends the session, so something inside the app holds the device.
- Turning the computer's Bluetooth off and on makes something in the app reconnect to the puck (a "maintain
  connection" request, or an object that re-establishes the link).

Tried, without success (`AmiiTag.Maui/Services/PluginBleAdapter.cs`):

- Stopping notifications and releasing every characteristic before disconnecting.
- Disposing the device, then forcing garbage collection of leftover WinRT objects.
- `GattSession.MaintainConnection = false` on a new session object, then on Plugin.BLE's own session (found by
  reflection), before the rename is sent (`IBleConnection.ExpectDeviceDisconnectAsync`).
- Disposing every `BluetoothLEDevice` Plugin.BLE has for the device (connected and discovered lists).

Ideas:

- Check the debug output: how many GATT sessions `ExpectDeviceDisconnectAsync` switched off, and what Windows reports
  after the disconnect.
- What differs from the Pixl.js path: the puck's Espruino detection (banner listening, `fastMode()`), its pending
  write when it restarts (the rename reply never arrives), or a second driver probing the same connection.
- Plugin.BLE on Windows handling the device-initiated drop (dotnet-bluetooth-le#1009: `DisconnectInternal` called on
  the WinRT callback thread on an unexpected loss).
- Whether the background scan (tag screens, the devices screen) recreates a device object that reconnects.
- Bypassing Plugin.BLE for the Windows connection (WinRT `BluetoothLEDevice`/`GattSession` directly).
- Whether Android and iOS have the same problem.
