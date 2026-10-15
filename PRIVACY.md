# NexScreen privacy policy

[Español](PRIVACY.es.md)

NexScreen does not collect, sell or send personal data, and it includes no analytics, advertising
or crash reporting.

## Internet connections

The only internet connection NexScreen makes is the **update check**:

- **What it does:** the tray app queries
  `https://api.github.com/repos/AlexPerez7/NexScreen/releases/latest` one minute after starting
  and then every 24 hours. If there is a newer version, it downloads it from GitHub only when you
  choose "Install version X.Y.Z" in the tray menu.
- **What is sent:** a regular HTTPS request to GitHub (with the NexScreen version in the
  `User-Agent` header). GitHub may log your IP address according to its
  [privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).
  NexScreen sends nothing else, neither to GitHub nor to any other server.
- **How to turn it off:** in the tray icon menu, untick "Buscar actualizaciones automáticamente"
  (automatic update check), or set `"checkForUpdates": false` in
  `%APPDATA%\NexScreen\settings.json`. With that off, NexScreen makes no internet connection at
  all.

## Connection to your device

The desktop video and the touch input travel only between your PC and your Android device, over
the USB cable or your local network. Over WiFi they are encrypted (TLS) and only with devices you
paired using the PIN. Nothing goes through third-party servers.

## Data NexScreen stores on your PC

- `%APPDATA%\NexScreen\`: preferences, the virtual monitor position and the list of paired
  devices.
- `%LOCALAPPDATA%\NexScreen\logs\`: the app log (it does not include screen content).
- The "NexScreen host" certificate in your user certificate store, used to encrypt the WiFi
  connection.

Uninstalling NexScreen removes the program, the virtual monitor and the firewall rules; you can
delete this user data by hand.
