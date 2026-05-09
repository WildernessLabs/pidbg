# PiDbg — Update Strategy

---

## 1. Components That Need Updates

| Component | Update Owner | Update Mechanism |
|-----------|-------------|-----------------|
| PiDbg VSIX | Developer (Visual Studio) | VS Marketplace / VSIX Gallery |
| PiDbg.Agent | VSIX-triggered (automatic) | SFTP upload + systemd restart |
| vsdbg | Agent (auto) | Microsoft's `getvsdbgsh` script |

---

## 2. Agent Update Strategy

The agent update is fully automated. The VSIX checks the agent version on every connection
and upgrades silently if needed (or prompts, depending on update channel setting).

### Version negotiation
On `AgentClient.GetStatusAsync()`, the response includes:
```protobuf
message AgentStatus {
  string agent_version = 1;        // e.g., "1.2.0"
  string min_vsix_version = 2;     // minimum VSIX version this agent supports
  string protocol_version = 3;     // gRPC protocol version (for breaking changes)
  // ... other fields
}
```

VSIX checks:
1. If `protocol_version` mismatches: hard block, must update one side
2. If `agent_version` < VSIX's bundled expected version: offer/auto-update
3. If `agent_version` > VSIX's expected version: warn but continue (backward-compat required)

### Update channels
Configurable in VSIX Tools → Options → PiDbg:
- **Stable** (default): only update when versions are incompatible
- **Auto**: automatically update agent when new version is bundled with VSIX
- **Manual**: never auto-update; show prompt only

### Update mechanism

The VSIX bundles the latest agent binary (ARM64 self-contained) in its VSIX package.
At `%ProgramFiles(x86)%\PiDbg\agent\pidbg-agent` after VSIX install.

Update sequence:
```
1. VSIX detects version mismatch
2. VSIX shows in Output window: "Updating PiDbg agent from 1.0.0 to 1.1.0..."
3. VSIX uploads new binary via SFTP:
   → /opt/pidbg/agent/pidbg-agent.new
4. VSIX calls AgentClient.PrepareUpdateAsync()
   → Agent verifies SHA-256 of pidbg-agent.new
   → Agent sets file executable: chmod +x pidbg-agent.new
5. VSIX calls AgentClient.ApplyUpdateAsync()
   → Agent executes update script:
     mv /opt/pidbg/agent/pidbg-agent /opt/pidbg/agent/pidbg-agent.old
     mv /opt/pidbg/agent/pidbg-agent.new /opt/pidbg/agent/pidbg-agent
     systemctl --user restart pidbg-agent.service
   → Agent exits (systemd restarts new version)
6. VSIX polls Ping with 30-second timeout
7. New agent responds
8. VSIX verifies new version matches expected
9. VSIX removes pidbg-agent.old
```

### Rollback after bad update
If the new agent fails to start (systemd restart fails), systemd's restart policy
(`Restart=on-failure`, `StartLimitBurst=5`) exhausts retries. The VSIX detects no response
to Ping and offers to roll back:
```
VSIX: Re-SSH to /opt/pidbg/agent/
      mv pidbg-agent.old pidbg-agent
      systemctl --user restart pidbg-agent.service
```
This is a recovery command shown in the Output window for manual execution if automatic
rollback is not possible (the old agent isn't responding either).

---

## 3. vsdbg Update Strategy

vsdbg is tied to the Visual Studio version. When the VSIX updates (new VS version), it
bumps the required vsdbg version.

### Detection
On every F5, agent checks:
```
if (required_vsdbg_version > installed_vsdbg_version)
    return FailedPrecondition("vsdbg update required")
```

### Update execution
Agent re-runs the install script with the new version:
```bash
bash /tmp/getvsdbgsh.sh -v <new-version> -l /opt/pidbg/vsdbg
```
This is idempotent — it overwrites the existing vsdbg installation.

vsdbg updates happen transparently during the "starting vsdbg" phase of the next debug
launch. There is no separate update prompt. Update takes 15–60 seconds on first install,
5–15 seconds on update (script downloads only changed files).

### Air-gapped vsdbg update
For Pi devices without internet access:
1. VSIX bundles vsdbg ARM64 tarball for the expected version
2. VSIX detects version mismatch and uploads the tarball via SFTP
3. Agent extracts the tarball to `/opt/pidbg/vsdbg/` instead of downloading

This is the `InstallVsdbgFromUpload` RPC path.

---

## 4. VSIX Update Strategy

The VSIX is distributed via:
1. **Visual Studio Marketplace** — primary channel, in-VS auto-update
2. **GitHub Releases** — secondary channel, for offline installation

VSIX versioning follows semantic versioning: `Major.Minor.Patch`.
- `Major`: breaking changes to VSIX↔Agent protocol
- `Minor`: new features, backward-compatible protocol additions
- `Patch`: bug fixes, no protocol changes

VS will notify users of VSIX updates through the "Extensions and Updates" dialog.
The VSIX specifies `<InstallationTarget Version="[17.12, 19.0)" />` in the manifest.

---

## 5. Protocol Versioning for Breaking Changes

The gRPC protocol version is independent of component versions. It is a simple integer.

When a breaking change is made to the proto:
1. Increment `protocol_version` in `AgentStatus`
2. VSIX checks: if agent's protocol_version ≠ VSIX's expected, hard block
3. Error message: "Protocol version mismatch. Update PiDbg agent to version X.Y.Z"

Non-breaking changes (adding fields, new RPCs) do not increment protocol version.
Proto evolution rules (backward-compatible field additions) are followed strictly.

---

## 6. Update Timing

Updates never interrupt an active debug session. The update check happens:
1. When VS connects to a device (F5 or Device Manager open)
2. After a debug session ends (if an update was detected during the session)

An update detected mid-session is deferred: the Output window shows
"Agent update available — will apply after this debug session ends."
