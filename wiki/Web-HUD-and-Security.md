# Web HUD and Security

HUDnopy embeds an **ASP.NET Core Kestrel** HTTPS and WebSocket server that serves a responsive single-page MFD (`Web/index.html`) for tablets, phones, and secondary monitors.

## Access Modes

Configure the Web Server under **Settings → Connectivity & Integrations**:

| Mode | Behavior | Recommended Use |
|---|---|---|
| **This PC only** *(Default)* | Binds strictly to `localhost` / `127.0.0.1`. | Second monitor on the same PC. |
| **Session Code** | Requires a rotating session code displayed in Settings (regenerated on restart; browser sessions expire after 12 hours). | Tablets or phones on a shared LAN. |
| **VPN Interface** | Listens only on the selected active VPN network interface plus `localhost`. | Remote access over your own VPN (e.g., WireGuard/Tailscale). |
| **Unrestricted** | Accepts any client that can reach the configured port. | Trusted private home networks only. |

> HUDnopy never modifies Windows Firewall rules or configures VPN software automatically.

## HTTPS Certificate & Fingerprint Verification

- On startup, `WebHttpsCertificate` generates a self-signed TLS certificate and stores it DPAPI-encrypted in `web-certificate.dat` next to the executable.
- The certificate is **never** installed into the Windows system root store and can be regenerated if your local IP addresses change.
- Because the certificate is self-signed, browsers display a security warning on first connection. Click the **Certificate Info** button in HUDnopy Settings and verify the **SHA-256 fingerprint** before trusting the connection.
- Access the Web HUD using `localhost` or a direct IP address (custom hostnames are rejected to mitigate DNS rebinding attacks).

## QR Code Pairing

When the Web Server is running, HUDnopy renders a native QR code (`QrCodeGenerator.cs`) for the selected URL so you can scan and open the Web HUD on a tablet or phone without typing IP addresses or ports.
