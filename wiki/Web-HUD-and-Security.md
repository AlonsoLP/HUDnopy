# Web HUD and Security

HUDnopy embeds an **ASP.NET Core Kestrel** HTTPS and WebSocket server that serves a modular responsive MFD (`Web/index.html`, `Web/styles.css`, `Web/app.js`) with **7 synchronized tabs** and a **Split MFD** mode for wide tablets and secondary monitors.

## Access Modes

Configure the Web Server under **Settings → Web Server**:

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

## QR Code Pairing & Transport Optimizations

- **ISO-Standard QR Pairing:** When the Web Server is running, HUDnopy renders a native vector QR code (`QrCodeGenerator.cs`, ISO/IEC 18004 4-module quiet zone) for the selected URL so you can scan and open the Web HUD on a tablet or phone without typing IP addresses or ports.
- **Pre-Compressed Brotli / GZip Assets:** `index.html`, `styles.css`, and `app.js` are pre-compressed once at startup into in-memory Brotli and GZip buffers (`Content-Encoding: br/gzip`, `Vary: Accept-Encoding`) for fast mobile loading with zero per-request CPU overhead.
- **Parallel WebSocket Broadcasts & Frame Deduplication:** Broadcasts run in parallel across connected clients with per-socket locks, 5-second per-client send timeouts, UTF-8 `SequenceEqual` frame deduplication, and RFC 6455 clean close handshakes.
- **Security Hardening & Client Error Telemetry:** All POST endpoints and `/ws` enforce same-origin `Origin` CSRF validation, strict `Content-Security-Policy` / `nosniff` / `no-store` headers, and LAN brute-force `429 Too Many Requests` lockouts. Unexpected HTTP 500 errors and browser JavaScript errors (`POST /api/client-error`, with host/LAN IP stripped) are logged cleanly to `%LocalAppData%\HUDnopy\logs\`.
