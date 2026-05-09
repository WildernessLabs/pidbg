# Meadow.Daemon — gRPC Contracts

The existing `pidbg.v1.DebugAgentService` is superseded by `meadow.daemon.v1.MeadowDaemonService`.
The new service is a superset: all `DebugAgentService` RPCs are present (possibly renamed),
plus new RPCs for application lifecycle, OTA update management, and health streaming.

The VSIX must be updated to target `MeadowDaemonService` instead of `DebugAgentService`.
The `PiDbg.Contracts` project is updated to include the new proto files.

---

## common.proto

```protobuf
syntax = "proto3";

package meadow.daemon.v1;

option csharp_namespace = "Meadow.Daemon.Contracts.V1";

import "google/protobuf/timestamp.proto";

message Empty {}

// ── Version info ──────────────────────────────────────────────────────────────

message DaemonVersion {
  string version = 1;
  string protocol_version = 2;   // incremented on breaking changes only
  string min_vsix_version = 3;
  string dotnet_runtime = 4;     // e.g., "10.0.1"
  string commit_hash = 5;        // git SHA, for support diagnostics
}

// ── Device info ───────────────────────────────────────────────────────────────

message DeviceInfo {
  string hostname = 1;
  string os_name = 2;            // "Debian GNU/Linux"
  string os_version = 3;         // "12 (bookworm)"
  string architecture = 4;       // "aarch64"
  string dotnet_version = 5;     // "10.0.1"
  int64 disk_free_bytes = 6;
  int64 disk_total_bytes = 7;
  string machine_id = 8;         // /etc/machine-id
  string serial_number = 9;      // Pi serial from /proc/cpuinfo
}

// ── Health ────────────────────────────────────────────────────────────────────

enum HealthState {
  HEALTH_STATE_UNSPECIFIED = 0;
  HEALTH_STATE_HEALTHY = 1;
  HEALTH_STATE_DEGRADED = 2;     // e.g., disk almost full
  HEALTH_STATE_UNHEALTHY = 3;    // e.g., managed app crashed, vsdbg stuck
}

message HealthStatus {
  HealthState state = 1;
  DaemonVersion daemon = 2;
  DeviceInfo device = 3;
  VsdbgInfo vsdbg = 4;
  ApplicationStatus app_status = 5;
  int32 active_debug_sessions = 6;
  repeated string warnings = 7;
  google.protobuf.Timestamp timestamp = 8;
}

message VsdbgInfo {
  bool installed = 1;
  string version = 2;
  string install_path = 3;
}

// ── Logging ───────────────────────────────────────────────────────────────────

enum LogLevel {
  LOG_LEVEL_UNSPECIFIED = 0;
  LOG_LEVEL_TRACE = 1;
  LOG_LEVEL_DEBUG = 2;
  LOG_LEVEL_INFORMATION = 3;
  LOG_LEVEL_WARNING = 4;
  LOG_LEVEL_ERROR = 5;
  LOG_LEVEL_CRITICAL = 6;
}

message LogEvent {
  google.protobuf.Timestamp timestamp = 1;
  LogLevel level = 2;
  string message = 3;
  string source_context = 4;
  string correlation_id = 5;
  map<string, string> properties = 6;
}

message StreamLogsRequest {
  LogLevel min_level = 1;
  string session_id = 2;         // optional: filter to session
}

// ── Ping ──────────────────────────────────────────────────────────────────────

message PingRequest {
  string correlation_id = 1;
}

message PingResponse {
  string correlation_id = 1;
  DaemonVersion version = 2;
  google.protobuf.Timestamp server_time = 3;
}
```

---

## deployment.proto

```protobuf
syntax = "proto3";

package meadow.daemon.v1;

option csharp_namespace = "Meadow.Daemon.Contracts.V1";

import "google/protobuf/timestamp.proto";

// ── Deployment types ──────────────────────────────────────────────────────────

// Deployment slot distinguishes production versions from debug deployments.
// Production deployments get versioned (000001, 000002, …) and support rollback.
// Debug deployments overwrite a fixed "debug" slot — no versioning, no rollback.
enum DeploymentSlot {
  DEPLOYMENT_SLOT_UNSPECIFIED = 0;
  DEPLOYMENT_SLOT_PRODUCTION = 1;    // versioned, retentioned, rollback-eligible
  DEPLOYMENT_SLOT_DEBUG = 2;         // single slot, always overwritten
}

message FileEntry {
  string relative_path = 1;
  string sha256 = 2;
  int64 size_bytes = 3;
}

message DeploymentManifest {
  string deployment_id = 1;
  string app_name = 2;
  DeploymentSlot slot = 3;
  repeated FileEntry files = 4;
  int64 total_bytes = 5;
  google.protobuf.Timestamp created_at = 6;
}

message DeploymentRecord {
  string deployment_id = 1;
  string app_name = 2;
  string version_label = 3;     // "000003" or "debug"
  DeploymentSlot slot = 4;
  string installed_path = 5;    // absolute path on Pi
  bool is_active = 6;
  google.protobuf.Timestamp deployed_at = 7;
  int32 file_count = 8;
  int64 total_bytes = 9;
}

// ── Deployment RPCs ───────────────────────────────────────────────────────────

message BeginDeploymentRequest {
  string app_name = 1;
  string deployment_id = 2;
  DeploymentSlot slot = 3;
  int32 file_count = 4;
  int64 total_bytes = 5;
  string entry_point = 6;        // e.g. "MyApp.dll" — recorded for app launch
  string correlation_id = 7;
}

message BeginDeploymentResponse {
  string deployment_id = 1;
  string staging_path = 2;
}

message DeploymentChunk {
  string deployment_id = 1;
  string relative_path = 2;
  bytes data = 3;
  uint64 offset = 4;
  bool is_last_chunk_for_file = 5;
}

message UploadChunksResponse {
  string deployment_id = 1;
  int32 files_received = 2;
  int64 bytes_received = 3;
}

message CommitDeploymentRequest {
  string deployment_id = 1;
  DeploymentManifest manifest = 2;
  bool activate_immediately = 3; // if true, set as active version after commit
}

message CommitDeploymentResponse {
  bool success = 1;
  string error_message = 2;
  string version_label = 3;      // assigned version label (e.g. "000003")
  string installed_path = 4;
  google.protobuf.Timestamp committed_at = 5;
}

message AbortDeploymentRequest {
  string deployment_id = 1;
}

message AbortDeploymentResponse {
  bool success = 1;
}

message ListDeploymentsRequest {
  string app_name = 1;
}

message ListDeploymentsResponse {
  repeated DeploymentRecord deployments = 1;
}

message GetCurrentManifestRequest {
  string app_name = 1;
  DeploymentSlot slot = 2;
}

message GetCurrentManifestResponse {
  bool exists = 1;
  DeploymentManifest manifest = 2;
  string version_label = 3;
}

message SetActiveVersionRequest {
  string app_name = 1;
  string version_label = 2;      // roll back to this version
}

message SetActiveVersionResponse {
  bool success = 1;
  string previous_version = 2;
  string error_message = 3;
}

message DeleteVersionRequest {
  string app_name = 1;
  string version_label = 2;
}

message DeleteVersionResponse {
  bool success = 1;
  string error_message = 2;
}

message PruneDeploymentsRequest {
  string app_name = 1;
  int32 keep_count = 2;          // keep this many most-recent production versions
}

message PruneDeploymentsResponse {
  int32 versions_deleted = 1;
  int64 bytes_freed = 2;
}
```

---

## process.proto

```protobuf
syntax = "proto3";

package meadow.daemon.v1;

option csharp_namespace = "Meadow.Daemon.Contracts.V1";

import "google/protobuf/timestamp.proto";

// ── Process / application types ───────────────────────────────────────────────

enum AppState {
  APP_STATE_UNSPECIFIED = 0;
  APP_STATE_NOT_DEPLOYED = 1;   // no active deployment exists
  APP_STATE_STOPPED = 2;
  APP_STATE_STARTING = 3;
  APP_STATE_RUNNING = 4;
  APP_STATE_STOPPING = 5;
  APP_STATE_CRASHED = 6;        // exited with non-zero code
}

message ApplicationStatus {
  string app_name = 1;
  AppState state = 2;
  int32 pid = 3;
  int32 last_exit_code = 4;
  google.protobuf.Timestamp started_at = 5;
  google.protobuf.Timestamp last_stopped_at = 6;
  int32 restart_count = 7;
  string active_version = 8;
}

message OutputLine {
  bool is_stderr = 1;
  string line = 2;
  google.protobuf.Timestamp timestamp = 3;
}

// ── Process RPCs ──────────────────────────────────────────────────────────────

message StartApplicationRequest {
  string app_name = 1;
  repeated string args = 2;
  map<string, string> environment_variables = 3;
  string working_directory = 4;
  bool use_debug_slot = 5;       // run from debug deployment slot
  string correlation_id = 6;
}

message StartApplicationResponse {
  bool success = 1;
  int32 pid = 2;
  string error_message = 3;
  google.protobuf.Timestamp started_at = 4;
}

message StopApplicationRequest {
  string app_name = 1;
  int32 grace_period_seconds = 2;  // 0 = SIGKILL immediately
  string correlation_id = 3;
}

message StopApplicationResponse {
  bool success = 1;
  int32 exit_code = 2;
  string error_message = 3;
}

message RestartApplicationRequest {
  string app_name = 1;
  int32 grace_period_seconds = 2;
  string correlation_id = 3;
}

message RestartApplicationResponse {
  bool success = 1;
  int32 new_pid = 2;
  string error_message = 3;
}

message GetApplicationStatusRequest {
  string app_name = 1;
}

message StreamOutputRequest {
  string app_name = 1;
  bool include_stderr = 2;
  int32 tail_lines = 3;     // replay last N lines before streaming live; 0 = live only
}

message ListProcessesRequest {
  bool dotnet_only = 1;
}

message ProcessInfo {
  int32 pid = 1;
  string name = 2;
  string command_line = 3;
  string working_directory = 4;
  double cpu_percent = 5;
  int64 memory_bytes = 6;
}

message ListProcessesResponse {
  repeated ProcessInfo processes = 1;
}
```

---

## session.proto

```protobuf
syntax = "proto3";

package meadow.daemon.v1;

option csharp_namespace = "Meadow.Daemon.Contracts.V1";

import "google/protobuf/timestamp.proto";

// ── Debug session types ───────────────────────────────────────────────────────

enum SessionMode {
  SESSION_MODE_UNSPECIFIED = 0;
  SESSION_MODE_LAUNCH = 1;   // vsdbg launches the app from debug slot
  SESSION_MODE_ATTACH = 2;   // vsdbg attaches to a running PID
}

enum SessionState {
  SESSION_STATE_UNSPECIFIED = 0;
  SESSION_STATE_STARTING = 1;
  SESSION_STATE_VSDBG_READY = 2;
  SESSION_STATE_APP_RUNNING = 3;
  SESSION_STATE_STOPPING = 4;
  SESSION_STATE_STOPPED = 5;
  SESSION_STATE_FAILED = 6;
}

// ── vsdbg management ──────────────────────────────────────────────────────────

message InstallVsdbgRequest {
  string version = 1;
  bool use_uploaded_tarball = 2;
}

message InstallVsdbgProgress {
  string message = 1;
  int32 percent_complete = 2;
  bool complete = 3;
  bool success = 4;
  string error = 5;
}

message VsdbgTarballChunk {
  bytes data = 1;
  uint64 offset = 2;
  bool is_last = 3;
}

message UploadVsdbgTarballResponse {
  bool success = 1;
  string sha256 = 2;
}

// ── Debug session RPCs ────────────────────────────────────────────────────────

message StartDebugSessionRequest {
  string session_id = 1;
  string app_name = 2;
  repeated string app_args = 3;
  map<string, string> environment_variables = 4;
  string working_directory = 5;
  SessionMode mode = 6;
  int32 attach_pid = 7;         // populated when mode = ATTACH
  string correlation_id = 8;
}

message StartDebugSessionResponse {
  string session_id = 1;
  int32 vsdbg_port = 2;         // Pi-local port (127.0.0.1 only)
  int32 vsdbg_pid = 3;
  int32 app_pid = 4;
  google.protobuf.Timestamp started_at = 5;
}

message StopDebugSessionRequest {
  string session_id = 1;
}

message StopDebugSessionResponse {
  bool success = 1;
  int32 app_exit_code = 2;
  google.protobuf.Timestamp stopped_at = 3;
}

message GetDebugSessionStatusRequest {
  string session_id = 1;
}

message DebugSessionStatus {
  string session_id = 1;
  SessionState state = 2;
  int32 app_pid = 3;
  int32 vsdbg_pid = 4;
  int32 vsdbg_port = 5;
  string error_message = 6;
  google.protobuf.Timestamp started_at = 7;
}

message ListDebugSessionsRequest {}

message ListDebugSessionsResponse {
  repeated DebugSessionStatus sessions = 1;
}
```

---

## meadow_daemon.proto

```protobuf
syntax = "proto3";

package meadow.daemon.v1;

option csharp_namespace = "Meadow.Daemon.Contracts.V1";

import "common.proto";
import "deployment.proto";
import "process.proto";
import "session.proto";

// ── Agent self-update ─────────────────────────────────────────────────────────

message PrepareUpdateRequest {
  string new_version = 1;
  string expected_sha256 = 2;
}

message PrepareUpdateResponse {
  bool ready = 1;
  string error = 2;
}

message ApplyUpdateRequest {
  string new_version = 1;
}

message ApplyUpdateResponse {
  bool update_triggered = 1;
  // Daemon exits immediately after responding. Poll Ping until new version responds.
}

// ─────────────────────────────────────────────────────────────────────────────
//
// MeadowDaemonService
//
// The unified gRPC service for Meadow.Daemon. Supersedes pidbg.v1.DebugAgentService.
// Listens on 127.0.0.1:50051. All access is via SSH port-forward tunnel.
//
// Error codes:
//   UNAVAILABLE         → daemon not ready or shutting down
//   FAILED_PRECONDITION → precondition unmet (vsdbg not installed, no deployment, etc.)
//   NOT_FOUND           → app / session / version not found
//   RESOURCE_EXHAUSTED  → disk full, port range exhausted
//   DATA_LOSS           → SHA-256 mismatch
//   ALREADY_EXISTS      → duplicate session ID
//   ABORTED             → operation cancelled
//   INTERNAL            → unexpected server error
//
// ─────────────────────────────────────────────────────────────────────────────

service MeadowDaemonService {

  // ── Daemon status ─────────────────────────────────────────────────────────

  rpc Ping(PingRequest) returns (PingResponse);
  rpc GetHealth(Empty) returns (HealthStatus);
  rpc StreamHealth(Empty) returns (stream HealthStatus);
  rpc StreamLogs(StreamLogsRequest) returns (stream LogEvent);

  // ── Application lifecycle ─────────────────────────────────────────────────
  // "Application" = the production-managed app supervised by this daemon.

  rpc StartApplication(StartApplicationRequest) returns (StartApplicationResponse);
  rpc StopApplication(StopApplicationRequest) returns (StopApplicationResponse);
  rpc RestartApplication(RestartApplicationRequest) returns (RestartApplicationResponse);
  rpc GetApplicationStatus(GetApplicationStatusRequest) returns (ApplicationStatus);
  rpc StreamOutput(StreamOutputRequest) returns (stream OutputLine);
  rpc ListProcesses(ListProcessesRequest) returns (ListProcessesResponse);

  // ── Deployment ────────────────────────────────────────────────────────────
  // Supports PRODUCTION slot (versioned) and DEBUG slot (always-overwritten).

  rpc BeginDeployment(BeginDeploymentRequest) returns (BeginDeploymentResponse);
  rpc UploadDeploymentChunks(stream DeploymentChunk) returns (UploadChunksResponse);
  rpc CommitDeployment(CommitDeploymentRequest) returns (CommitDeploymentResponse);
  rpc AbortDeployment(AbortDeploymentRequest) returns (AbortDeploymentResponse);

  rpc ListDeployments(ListDeploymentsRequest) returns (ListDeploymentsResponse);
  rpc GetCurrentManifest(GetCurrentManifestRequest) returns (GetCurrentManifestResponse);
  rpc SetActiveVersion(SetActiveVersionRequest) returns (SetActiveVersionResponse);
  rpc DeleteVersion(DeleteVersionRequest) returns (DeleteVersionResponse);
  rpc PruneDeployments(PruneDeploymentsRequest) returns (PruneDeploymentsResponse);

  // ── vsdbg management ──────────────────────────────────────────────────────

  rpc GetVsdbgInfo(Empty) returns (VsdbgInfo);
  rpc InstallVsdbg(InstallVsdbgRequest) returns (stream InstallVsdbgProgress);
  rpc UploadVsdbgTarball(stream VsdbgTarballChunk) returns (UploadVsdbgTarballResponse);

  // ── Debug sessions ────────────────────────────────────────────────────────

  rpc StartDebugSession(StartDebugSessionRequest) returns (StartDebugSessionResponse);
  rpc StopDebugSession(StopDebugSessionRequest) returns (StopDebugSessionResponse);
  rpc GetDebugSessionStatus(GetDebugSessionStatusRequest) returns (DebugSessionStatus);
  rpc ListDebugSessions(ListDebugSessionsRequest) returns (ListDebugSessionsResponse);

  // ── Daemon self-update ────────────────────────────────────────────────────

  rpc GetDaemonVersion(Empty) returns (DaemonVersion);
  rpc PrepareUpdate(PrepareUpdateRequest) returns (PrepareUpdateResponse);
  rpc ApplyUpdate(ApplyUpdateRequest) returns (ApplyUpdateResponse);
}
```

---

## Protocol Evolution Rules

1. Never reuse a field number. When removing a field, mark it `reserved`.
2. New optional fields may be added at any proto version.
3. `protocol_version` in `DaemonVersion` is incremented ONLY on breaking changes.
4. A breaking change is: removing an RPC, changing an RPC signature incompatibly,
   changing field semantics (not just adding fields).
5. The VSIX checks `protocol_version` on first connect. Mismatches block the session
   with a clear error: "Update your PiDbg extension" or "Update Meadow.Daemon".
6. Current protocol version: `"1"`. Target for first release.
