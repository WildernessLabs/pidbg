# PiDbg — Security Model

---

## 1. Threat Model Summary

The threat model assumes:
- Developer machine is trusted (it's the developer's workstation)
- The local network may not be trusted (shared Wi-Fi, open office LAN)
- The Pi is a development device, not a production server
- Attackers on the LAN should not be able to leverage PiDbg to access the Pi

The primary security controls are:
1. All remote ports (agent gRPC, vsdbg) are bound to localhost only
2. All external access is gated through SSH authentication
3. No credentials stored in plaintext
4. vsdbg is only accessible during an active debug session

---

## 2. SSH Authentication

### Supported authentication methods

| Method | Storage | Recommended |
|--------|---------|-------------|
| SSH private key (Ed25519) | User-specified file path | ✓ Preferred |
| SSH private key (RSA-4096) | User-specified file path | ✓ Acceptable |
| Password | Encrypted in Windows Credential Manager | ✗ Discouraged |

### Key generation
The provisioning script `setup-ssh-keys.sh` generates a dedicated keypair for PiDbg.
Using a dedicated key (not `~/.ssh/id_ed25519`) means:
- The key can be easily revoked without affecting other SSH usage
- The key can have a comment identifying it: `pidbg@<machinename>`
- PiDbg has the minimum necessary access, not all SSH access

Generation:
```bash
ssh-keygen -t ed25519 -C "pidbg@$(hostname)" -f ~/.pidbg/keys/<device-id>/id_ed25519
ssh-copy-id -i ~/.pidbg/keys/<device-id>/id_ed25519.pub pi@raspberrypi
```

### SSH key storage (VSIX side)
Key paths are stored in `DeviceRecord.SshKeyPath`. The key file itself stays on the
filesystem at the user-specified path. The VSIX never reads the key material into memory
— SSH.NET loads it only for connection establishment.

Passphrase (if set) is stored in Windows Credential Manager:
```csharp
// Windows Credential Manager via CredentialManager NuGet package
CredentialManager.WriteCredential(
    applicationName: $"PiDbg/{device.Id}",
    userName: device.Username,
    secret: passphrase,
    persistence: CredentialPersistence.LocalMachine);
```

Password auth (if used) is also stored in Credential Manager, never in `devices.json`.

### Pi-side authorized_keys hardening
The `setup-ssh-keys.sh` script adds the key with restrictions:
```
restrict,command="/usr/bin/pidbg-shell-wrapper" ssh-ed25519 AAAA... pidbg@devmachine
```

Phase 1: `restrict` limits key to no X11 forwarding, no agent forwarding, no PTY.
Phase 2: `command=` restriction limits what the key can execute (advanced hardening).

---

## 3. Network Exposure

### No firewall rule required
Because the agent and vsdbg listen on `127.0.0.1` only, no firewall holes are needed.
The only port that must be reachable from the developer machine is SSH (22).

### Pi firewall configuration (recommended)
The provisioning script configures `ufw` (if installed):
```bash
ufw allow ssh
ufw default deny incoming
ufw enable
```
This ensures only SSH is accessible, regardless of what services are running.

### Local port forward security (developer machine)
`ForwardedPortLocal` in SSH.NET binds to `127.0.0.1` on the developer machine.
Other processes on the developer machine could connect to these ports, but:
- The local forward is only open during an active debug session
- It closes when the session ends
- Any connection goes through the SSH tunnel to the Pi

---

## 4. Data in Transit

All data between VSIX and agent flows through the SSH tunnel (AES-256 encryption by default
with OpenSSH). No additional TLS layer is needed or desired — the SSH encryption is
sufficient and TLS would be redundant.

What flows through the tunnel:
- gRPC control messages (device status, session management)
- Application files during deployment
- vsdbg debugging protocol traffic
- Log events

What does NOT flow:
- SSH credentials (key negotiation happens at the SSH layer, not in-tunnel)
- User passwords

---

## 5. Application Binary Security

Deployed binaries are treated as trusted developer output (Debug builds). There is no
code signing requirement for development builds. This is consistent with local debugging.

The SHA-256 manifest serves as integrity protection against accidental corruption, not
against malicious tampering (which would require compromising the VSIX or the SSH session).

---

## 6. vsdbg Access Control

vsdbg is:
- Bound to `127.0.0.1` only
- Only running during an active debug session (started on demand, stopped when session ends)
- Running as the SSH user (same user that started it) — no privilege escalation

vsdbg does NOT:
- Listen on externally accessible ports
- Run as root
- Have access beyond what the SSH user has

### vsdbg idle timeout
vsdbg is configured with a 60-second idle timeout. If no debugger client connects within
60 seconds of launch, vsdbg exits automatically. This prevents orphaned vsdbg processes
from accumulating and leaving a TCP port open indefinitely.

---

## 7. Agent Process Security

### Service account
The agent runs as a systemd user service, not system service. It runs as the standard
SSH user (e.g., `pi`), not root.

`/opt/pidbg/` permissions:
```
/opt/pidbg/           drwxr-xr-x  root:root       (created by install script with sudo)
/opt/pidbg/agent/     drwxr-xr-x  pi:pi           (agent binary, agent-writable)
/opt/pidbg/apps/      drwxrwxr-x  pi:pi           (deployments, agent-writable)
/opt/pidbg/vsdbg/     drwxr-xr-x  pi:pi           (vsdbg, agent-writable for updates)
/opt/pidbg/logs/      drwxrwxr-x  pi:pi           (logs, agent-writable)
```

The agent has no sudo access. Any operation requiring elevated privileges (system service
management, etc.) is explicitly out of scope.

### Meadow.Daemon communication
The agent communicates with Meadow.Daemon via `http://127.0.0.1:5000`. This is local-only
and requires no authentication (consistent with how Meadow.Daemon is designed — it is
accessible to local processes only).

---

## 8. Secrets Summary

| Secret | Storage Location | Access |
|--------|-----------------|--------|
| SSH private key | User filesystem (`~/.pidbg/keys/<id>/`) | SSH.NET only, read on connect |
| SSH key passphrase | Windows Credential Manager | Read on connect |
| SSH password | Windows Credential Manager | Read on connect |
| gRPC auth | None — SSH handles auth | N/A |
| vsdbg port | In-memory only (ephemeral) | Session lifetime |

Nothing secret is stored in:
- `devices.json` (contains host, username, key path — not key material or passwords)
- Log files
- Agent binary or config
- VSIX package

---

## 9. Security Non-Goals (Phase 1)

These are explicitly deferred to later phases or left as user responsibility:
- mTLS on the gRPC channel (redundant with SSH)
- Audit logging of who connected to what Pi when
- Pi access control (multiple developers sharing one Pi)
- Revocation of compromised keys
- Hardened vsdbg command restrictions
